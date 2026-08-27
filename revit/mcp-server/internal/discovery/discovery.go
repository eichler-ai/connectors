// Package discovery implements the broker's side of PRD §08 (API discovery
// tools): routing list_functions/search_functions/describe_function to a
// live add-in wire connection.
//
// Deliberately independent of internal/execution: reflection over
// RevitAPI.dll/RevitAPIUI.dll never touches a Document or the UI thread
// (PRD §08 "Execution locus"), so discovery has no bearing on — and must
// never be routed through — execution.Manager's busy/pending/unrecoverable
// state machine (PRD §06). Router tracks its own independent set of
// attached instance connections rather than sharing execution.Manager's.
//
// Router does hold a read-only reference to internal/registry, unlike
// execution.Manager -- a narrower, different kind of coupling (instance
// bookkeeping, not execution state) needed once multi-version support
// (PRD §11) made it possible for differently-versioned Revit instances to
// be connected at once: an unscoped discovery call (no instance_id) needs
// to know whether "any connected instance" is actually safe to pick
// silently, and every response needs to say which Revit version it
// reflects so results from different instances are never ambiguous to the
// caller.
package discovery

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"sync"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

const source = "mcp-server.internal.discovery"

// wireTimeout bounds every discovery wire round trip. Discovery is meant to
// be fast/live (PRD §08: "trivially cheap live, every call" for
// describe_function; list_functions/search_functions "stay fast" via
// bounded scope), not agent-timeout-configurable the way execute_script is.
const wireTimeout = 15 * time.Second

// Router tracks live add-in wire connections and forwards discovery calls
// to one of them. Its own map, independent of execution.Manager's — see
// package doc. reg is read-only from this package's perspective (only ever
// Get, never Register/Remove) — see the package doc for why Router needs it.
type Router struct {
	mu    sync.Mutex
	conns map[string]*transport.Conn
	reg   *registry.Registry
}

// NewRouter builds an empty Router. reg may be nil (e.g. in tests that
// don't exercise the multi-version disambiguation/revit_version behavior) —
// every reg access below is nil-checked, degrading to the pre-multi-version
// behavior (silent sorted-first pick, no revit_version in responses) rather
// than panicking.
func NewRouter(reg *registry.Registry) *Router {
	return &Router{conns: make(map[string]*transport.Conn), reg: reg}
}

// AttachInstance registers the wire connection to use for instanceID. A
// second call for the same instanceID (e.g. after a reconnect) replaces the
// prior connection.
func (r *Router) AttachInstance(instanceID string, conn *transport.Conn) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.conns[instanceID] = conn
}

// DetachInstance drops the wire connection for instanceID.
func (r *Router) DetachInstance(instanceID string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	delete(r.conns, instanceID)
}

func errNoInstanceConnected() *diag.Record {
	return diag.New(diag.SeverityError, "no_instance_connected", source,
		"discovery needs at least one live Revit instance connected, and none is").
		WithRemedy("launch Revit with the MCP Bridge add-in loaded, or call list_instances to check connection state")
}

func errInstanceNotFound(instanceID string) *diag.Record {
	return diag.New(diag.SeverityError, "instance_not_found", source,
		fmt.Sprintf("instance %q is not registered with the broker (no live connection)", instanceID)).
		WithDetail(map[string]any{"instance_id": instanceID}).
		WithRemedy("confirm the instance_id from a recent register/reconnect, then retry")
}

// errAmbiguousInstanceVersion is returned when instance_id is omitted and
// the connected instances span more than one Revit version -- silently
// picking one (the pre-multi-version behavior) would hand back
// version-specific API data with nothing telling the caller it's
// version-specific, and non-deterministic results across repeat calls.
// candidates lists every connected instance's id and Revit version so the
// caller can pick one without a separate list_instances round trip.
func errAmbiguousInstanceVersion(candidates []map[string]string) *diag.Record {
	return diag.New(diag.SeverityError, "ambiguous_instance_version", source,
		"instance_id was omitted, but connected instances span more than one Revit version -- discovery results would be silently version-specific").
		WithDetail(map[string]any{"candidates": candidates}).
		WithRemedy("pass instance_id to pick a specific instance (see the candidates list, or call list_instances)")
}

func errWireCallFailed(method string, err error) *diag.Record {
	return diag.New(diag.SeverityError, "wire_call_failed", source,
		fmt.Sprintf("%s did not complete: %s", method, err.Error())).
		WithDetail(map[string]any{"method": method}).
		WithRemedy("retry the call; if this persists the instance may need a Revit restart")
}

func errWireDecodeFailed(method string, err error) *diag.Record {
	return diag.New(diag.SeverityError, "wire_response_malformed", source,
		fmt.Sprintf("%s response could not be decoded: %s", method, err.Error())).
		WithDetail(map[string]any{"method": method})
}

func fromRPCError(rpcErr *transport.RPCError) *diag.Record {
	if rpcErr.Data != nil {
		return rpcErr.Data
	}
	return diag.New(diag.SeverityError, "add_in_error", source,
		fmt.Sprintf("discovery call failed: %s", rpcErr.Message))
}

// resolveConn picks the wire connection to use for instanceID: the
// explicitly named instance if given (error if it isn't attached), else a
// deterministic pick — sorted instance IDs, first one — from whatever's
// currently attached (error if nothing is attached at all), UNLESS the
// attached instances span more than one Revit version (PRD §11), in which
// case an unscoped call is genuinely ambiguous and errors instead of
// silently picking one (see errAmbiguousInstanceVersion). Also returns the
// resolved instance's own id, so callers can look up its Revit version for
// the response without a second, separately-locked pass over the map.
func (r *Router) resolveConn(instanceID string) (*transport.Conn, string, *diag.Record) {
	r.mu.Lock()
	defer r.mu.Unlock()

	if instanceID != "" {
		conn, ok := r.conns[instanceID]
		if !ok {
			return nil, "", errInstanceNotFound(instanceID)
		}
		return conn, instanceID, nil
	}

	if len(r.conns) == 0 {
		return nil, "", errNoInstanceConnected()
	}
	ids := make([]string, 0, len(r.conns))
	for id := range r.conns {
		ids = append(ids, id)
	}
	sort.Strings(ids)

	if r.reg != nil && len(ids) > 1 {
		versionsSeen := map[string]bool{}
		candidates := make([]map[string]string, 0, len(ids))
		for _, id := range ids {
			version := ""
			if inst, ok := r.reg.Get(id); ok {
				version = inst.RevitVersion
			}
			versionsSeen[version] = true
			candidates = append(candidates, map[string]string{"instance_id": id, "revit_version": version})
		}
		if len(versionsSeen) > 1 {
			return nil, "", errAmbiguousInstanceVersion(candidates)
		}
	}

	return r.conns[ids[0]], ids[0], nil
}

// callWire performs one JSON-RPC round trip for method against conn,
// returning the raw result JSON unmodified on success — the caller (the MCP
// tool layer) decodes it into typed output; this package must not
// double-decode/re-encode, which would risk silently dropping fields the
// add-in returns that this package doesn't know about.
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
	// Confirm the result is at least well-formed JSON (a decode-and-discard
	// check, not a decode-and-reencode) so a malformed add-in response
	// surfaces as a diagnostic error rather than being handed upstream
	// as-is and failing unpredictably later.
	var probe any
	if err := json.Unmarshal(raw, &probe); err != nil {
		return nil, errWireDecodeFailed(method, err)
	}
	return raw, nil
}

// call resolves instanceID (or picks one, per resolveConn) and forwards the
// wire call, also returning the resolved instance's Revit version (empty if
// reg is nil or the instance isn't in it) so the mcpserver layer can stamp
// every discovery response with which Revit version it reflects.
func (r *Router) call(ctx context.Context, instanceID, method string, params map[string]any) (json.RawMessage, string, *diag.Record) {
	conn, resolvedID, drec := r.resolveConn(instanceID)
	if drec != nil {
		return nil, "", drec
	}
	revitVersion := ""
	if r.reg != nil {
		if inst, ok := r.reg.Get(resolvedID); ok {
			revitVersion = inst.RevitVersion
		}
	}
	raw, callErr := callWire(ctx, conn, method, params)
	if callErr != nil {
		return nil, "", callErr
	}
	return raw, revitVersion, nil
}

// ListFunctions forwards to the add-in's list_functions wire method. See
// PRD §08.
func (r *Router) ListFunctions(ctx context.Context, instanceID string, params map[string]any) (json.RawMessage, string, *diag.Record) {
	return r.call(ctx, instanceID, "list_functions", params)
}

// SearchFunctions forwards to the add-in's search_functions wire method. See
// PRD §08.
func (r *Router) SearchFunctions(ctx context.Context, instanceID string, params map[string]any) (json.RawMessage, string, *diag.Record) {
	return r.call(ctx, instanceID, "search_functions", params)
}

// DescribeFunction forwards to the add-in's describe_function wire method.
// See PRD §08.
func (r *Router) DescribeFunction(ctx context.Context, instanceID string, params map[string]any) (json.RawMessage, string, *diag.Record) {
	return r.call(ctx, instanceID, "describe_function", params)
}
