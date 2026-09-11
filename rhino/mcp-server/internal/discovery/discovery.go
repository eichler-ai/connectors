// Package discovery implements the server's side of PRD §09 API discovery:
// routing list_functions/search_functions/describe_function/dump_members to a
// live plug-in wire connection.
//
// Deliberately independent of internal/execution: reflection over RhinoCommon
// (and loaded plug-ins) never touches a document or the main thread (PRD §09),
// so discovery has no bearing on — and is never routed through —
// execution's busy/pending/unrecoverable state. Unlike the Revit connector's
// broker, the Rhino server has no singleton and keeps no parallel connection
// map: it reads live connections straight from the dialer (the Conns seam
// below), and holds a read-only registry reference only so an unscoped call
// can tell whether the connected instances span more than one Rhino version
// (in which case picking one silently would hand back version-specific results
// with nothing saying so) and so every response can name which version it
// reflects.
package discovery

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"time"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/internal/servercore/transport"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/registry"
)

const source = "mcp-server.internal.discovery"

// unknownRhinoVersion marks a connected instance whose Rhino version is not in
// the registry. Never the empty string — "" is also the zero value of the
// registry field, so a distinct sentinel keeps "genuinely unknown" legible in
// the candidates list rather than leaking a blank version to the caller.
const unknownRhinoVersion = "unknown"

// wireTimeout bounds every discovery round trip. Discovery is meant to be fast
// and live (PRD §09), not agent-timeout-configurable the way execute_script is.
const wireTimeout = 15 * time.Second

// Conns is the live-connection source the Router reads — satisfied by the
// dialer's Manager (Conn/Connected). Kept as an interface so the Router is
// unit-testable with a fake, exactly as execution.Router's Conns is.
type Conns interface {
	Conn(instanceID string) (*transport.Conn, bool)
	Connected() []string
}

// Router forwards discovery calls to a live plug-in connection. It owns no
// connection state: conns is the dialer, reg is read-only (only Get). See the
// package doc for why reg is needed.
type Router struct {
	conns Conns
	reg   *registry.Registry
}

// NewRouter binds a Router to the dialer's connection source and the registry.
// reg must not be nil: the multi-version disambiguation and the rhino_version
// stamped on every response are load-bearing correctness (PRD §05/§09), so a
// missing registry fails loudly here rather than silently degrading at query
// time. Tests that don't exercise version disambiguation pass registry.New().
func NewRouter(conns Conns, reg *registry.Registry) *Router {
	if reg == nil {
		panic("discovery.NewRouter: reg must not be nil")
	}
	return &Router{conns: conns, reg: reg}
}

func errNoInstanceConnected() *diag.Record {
	return diag.New(diag.SeverityError, "no-instance-connected", source,
		"discovery needs at least one live Rhino instance connected, and none is").
		WithRemedy("launch Rhino with the MCP Bridge plug-in loaded, or call list_instances to check connection state")
}

func errInstanceNotFound(instanceID string) *diag.Record {
	return diag.New(diag.SeverityError, "instance-not-found", source,
		fmt.Sprintf("instance %q is not connected to this server", instanceID)).
		WithDetail(map[string]any{"instance_id": instanceID}).
		WithRemedy("confirm the instance_id from list_instances, then retry")
}

// errAmbiguousInstanceVersion is returned when instance_id is omitted and the
// connected instances span more than one Rhino version — silently picking one
// would hand back version-specific API data with nothing telling the caller,
// and non-deterministic results across repeat calls. candidates lists every
// connected instance's id and Rhino version so the caller can pick without a
// separate list_instances round trip.
func errAmbiguousInstanceVersion(candidates []map[string]string) *diag.Record {
	return diag.New(diag.SeverityError, "ambiguous-instance-version", source,
		"instance_id was omitted, but the connected instances span more than one Rhino version -- discovery results would be silently version-specific").
		WithDetail(map[string]any{"candidates": candidates}).
		WithRemedy("pass instance_id to pick a specific instance (see the candidates list, or call list_instances)")
}

func errWireCallFailed(method string, err error) *diag.Record {
	return diag.New(diag.SeverityError, "wire-call-failed", source,
		fmt.Sprintf("%s did not complete: %s", method, err.Error())).
		WithDetail(map[string]any{"method": method}).
		WithRemedy("retry the call; if this persists the instance may need a Rhino restart")
}

func errWireDecodeFailed(method string, err error) *diag.Record {
	return diag.New(diag.SeverityError, "wire-response-malformed", source,
		fmt.Sprintf("%s response could not be decoded: %s", method, err.Error())).
		WithDetail(map[string]any{"method": method})
}

func fromRPCError(rpcErr *transport.RPCError) *diag.Record {
	if rpcErr.Data != nil {
		return rpcErr.Data
	}
	return diag.New(diag.SeverityError, "bridge-error", source,
		fmt.Sprintf("discovery call failed: %s", rpcErr.Message))
}

// resolveConn picks the connection for instanceID: the named instance if given
// (error if not connected), else a deterministic pick (sorted ids, first) from
// whatever is connected (error if nothing is), UNLESS the connected instances
// span more than one Rhino version, in which case an unscoped call is ambiguous
// and errors instead of silently picking one. Returns the resolved id too.
func (r *Router) resolveConn(instanceID string) (*transport.Conn, string, *diag.Record) {
	if instanceID != "" {
		conn, ok := r.conns.Conn(instanceID)
		if !ok {
			return nil, "", errInstanceNotFound(instanceID)
		}
		return conn, instanceID, nil
	}

	ids := r.conns.Connected()
	if len(ids) == 0 {
		return nil, "", errNoInstanceConnected()
	}
	sort.Strings(ids)

	if len(ids) > 1 {
		versionsSeen := map[string]bool{}
		candidates := make([]map[string]string, 0, len(ids))
		for _, id := range ids {
			version := unknownRhinoVersion
			if inst, ok := r.reg.Get(id); ok && inst.RhinoVersion != "" {
				version = inst.RhinoVersion
			}
			versionsSeen[version] = true
			candidates = append(candidates, map[string]string{"instance_id": id, "rhino_version": version})
		}
		if len(versionsSeen) > 1 {
			return nil, "", errAmbiguousInstanceVersion(candidates)
		}
	}

	conn, ok := r.conns.Conn(ids[0])
	if !ok {
		// The instance dropped between Connected() and Conn(); treat as none connected.
		return nil, "", errNoInstanceConnected()
	}
	return conn, ids[0], nil
}

// callWire performs one JSON-RPC round trip and returns the raw result JSON
// unmodified (the tool layer decodes it into typed output — this package must
// not double-decode/re-encode and risk dropping fields the plug-in returns).
func callWire(ctx context.Context, conn *transport.Conn, method string, params map[string]any) (json.RawMessage, *diag.Record) {
	callCtx, cancel := context.WithTimeout(ctx, wireTimeout)
	defer cancel()

	raw, rpcErr, err := conn.Call(callCtx, method, params)
	if err != nil {
		return nil, errWireCallFailed(method, err)
	}
	if rpcErr != nil {
		return nil, fromRPCError(rpcErr)
	}
	// Confirm the result is well-formed JSON (decode-and-discard, not re-encode)
	// so a malformed response surfaces as a diagnostic rather than failing later.
	var probe any
	if err := json.Unmarshal(raw, &probe); err != nil {
		return nil, errWireDecodeFailed(method, err)
	}
	return raw, nil
}

// resolve is resolveConn plus the registry's Rhino version for the resolved
// instance (empty when the registry no longer has it) — the one place the
// "which version answered" rule lives.
func (r *Router) resolve(instanceID string) (*transport.Conn, string, string, *diag.Record) {
	conn, resolvedID, drec := r.resolveConn(instanceID)
	if drec != nil {
		return nil, "", "", drec
	}
	rhinoVersion := ""
	if inst, ok := r.reg.Get(resolvedID); ok {
		rhinoVersion = inst.RhinoVersion
	}
	return conn, resolvedID, rhinoVersion, nil
}

// call resolves instanceID (or picks one) and forwards, returning the resolved
// instance's Rhino version so the tool layer can stamp every response with it.
func (r *Router) call(ctx context.Context, instanceID, method string, params map[string]any) (json.RawMessage, string, *diag.Record) {
	conn, _, rhinoVersion, drec := r.resolve(instanceID)
	if drec != nil {
		return nil, "", drec
	}
	raw, callErr := callWire(ctx, conn, method, params)
	if callErr != nil {
		return nil, "", callErr
	}
	return raw, rhinoVersion, nil
}

// ResolveInstance applies the same instance selection as the wire calls without
// making one, returning the resolved instance_id and its Rhino version. The
// broker-side search index uses it so an index lookup honours the same routing
// a plug-in call would have.
func (r *Router) ResolveInstance(instanceID string) (string, string, *diag.Record) {
	_, resolvedID, rhinoVersion, drec := r.resolve(instanceID)
	return resolvedID, rhinoVersion, drec
}

// ListFunctions forwards to the plug-in's list_functions wire method (PRD §09).
func (r *Router) ListFunctions(ctx context.Context, instanceID string, params map[string]any) (json.RawMessage, string, *diag.Record) {
	return r.call(ctx, instanceID, "list_functions", params)
}

// SearchFunctions forwards to the plug-in's search_functions wire method — the
// plug-in's own keyword ranker, used as the fallback while the broker's
// semsearch index is still building (PRD §09).
func (r *Router) SearchFunctions(ctx context.Context, instanceID string, params map[string]any) (json.RawMessage, string, *diag.Record) {
	return r.call(ctx, instanceID, "search_functions", params)
}

// DescribeFunction forwards to the plug-in's describe_function wire method (PRD §09).
func (r *Router) DescribeFunction(ctx context.Context, instanceID string, params map[string]any) (json.RawMessage, string, *diag.Record) {
	return r.call(ctx, instanceID, "describe_function", params)
}

// DumpMembers forwards to the plug-in's dump_members wire method: one page of
// the documented member corpus, from which the shared semsearch manager builds
// the broker index. Never agent-facing; the manager pages through it on attach.
// Satisfies semsearch/manager.Source.
func (r *Router) DumpMembers(ctx context.Context, instanceID string, offset, limit int) (json.RawMessage, string, *diag.Record) {
	return r.call(ctx, instanceID, "dump_members", map[string]any{"offset": offset, "limit": limit})
}
