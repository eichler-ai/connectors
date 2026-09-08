package hub

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"log/slog"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub/diag"
	"github.com/eichler-ai/connectors/hub/internal/bridge"
	"github.com/eichler-ai/connectors/hub/internal/registry"
	"github.com/eichler-ai/connectors/hub/protocol"
)

// Host is the set of hub services a connector's tools call: Exec, the
// registry, and the identity of the calling MCP session. One Host serves
// every connector; the connector slug travels with each call.
type Host struct {
	reg     *registry.Registry
	bridges *bridge.Service
	log     *slog.Logger

	// userOf resolves the MCP session's user. In production it reads the
	// TokenInfo the bearer middleware attached; tests, which drive tools over
	// the SDK's in-memory transport where no HTTP layer exists, substitute a
	// fixed identity. It is the only injection point and exists for that.
	userOf func(req *mcp.CallToolRequest) (string, bool)

	defaultTimeout, maxTimeout time.Duration
}

const (
	// DefaultTimeout applies when a tool call names none; MaxTimeout caps what
	// it may ask for. 30 s matches the POC's measured round trips (§03);
	// 10 min covers a whole-document PDF export with margin and stays well
	// inside Cloud Run's 60-minute request ceiling.
	DefaultTimeout = 30 * time.Second
	MaxTimeout     = 10 * time.Minute
	// Source labels diagnostics raised by the hub itself.
	Source = "hub"
)

// Instance is one live bridge as tools report it (list_instances, get_status).
type Instance struct {
	InstanceID     string              `json:"instance_id"`
	Host           protocol.Host       `json:"host"`
	BridgeVersion  string              `json:"bridge_version"`
	ConnectedSince time.Time           `json:"connected_since"`
	Documents      []protocol.Document `json:"documents"`
}

func instanceOf(b *registry.Bridge) Instance {
	docs := b.Documents()
	if docs == nil {
		docs = []protocol.Document{}
	}
	return Instance{InstanceID: b.InstanceID, Host: b.Host, BridgeVersion: b.BridgeVersion, ConnectedSince: b.Since, Documents: docs}
}

// Result is a completed exec: the bridge's reply plus where it ran.
type Result struct {
	Reply    protocol.Result
	Instance Instance
	// Document is the document the exec was addressed to (the active one
	// when Target.DocumentID was empty), as the bridge last registered it.
	Document protocol.Document
}

// User returns the calling session's user id, or an `unauthenticated` record.
// Tool handlers call this first; a missing identity is a wiring bug (the
// endpoint is behind the bearer middleware), so it fails loudly.
func (h *Host) User(req *mcp.CallToolRequest) (string, *diag.Record) {
	if uid, ok := h.userOf(req); ok && uid != "" {
		return uid, nil
	}
	return "", diag.New(diag.SeverityError, "unauthenticated", Source, "the MCP session carries no user identity").
		WithRemedy("reconnect the MCP client with a valid bearer token")
}

// Instances lists the user's live bridges for a connector, oldest first.
func (h *Host) Instances(user, connector string) []Instance {
	var out []Instance
	for _, b := range h.reg.List(user, connector) {
		out = append(out, instanceOf(b))
	}
	if out == nil {
		out = []Instance{}
	}
	return out
}

// Resolve picks the bridge a target names, applying the §07 rule: an
// explicit instance_id must exist; no instance_id is fine only when the user
// has exactly one bridge for the connector.
func (h *Host) resolve(user, connector string, target Target) (*registry.Bridge, *diag.Record) {
	if target.InstanceID != "" {
		b, ok := h.reg.Get(user, connector, target.InstanceID)
		if !ok {
			return nil, diag.New(diag.SeverityError, "unknown-instance", Source,
				fmt.Sprintf("no %s bridge with instance_id %q is connected for this user", connector, target.InstanceID)).
				WithRemedy("call list_instances and use one of the instance_id values it returns")
		}
		return b, nil
	}
	all := h.reg.List(user, connector)
	switch len(all) {
	case 1:
		return all[0], nil
	case 0:
		return nil, diag.New(diag.SeverityError, "no-bridge", Source,
			fmt.Sprintf("no %s bridge is connected for this user", connector)).
			WithRemedy("open the connector's task pane in the host application and check it shows Connected",
				"call list_instances to confirm it appears")
	default:
		ids := make([]string, 0, len(all))
		for _, b := range all {
			ids = append(ids, b.InstanceID)
		}
		return nil, diag.New(diag.SeverityError, "ambiguous-instance", Source,
			fmt.Sprintf("%d %s bridges are connected for this user; instance_id is required", len(all), connector)).
			WithDetail(map[string]any{"instance_ids": ids}).
			WithRemedy("call list_instances and pass the instance_id of the one you mean")
	}
}

// Exec runs script on the connector's bridge for user (§09). Failures the hub
// itself detects come back as a diagnostic record with a stable code; a
// script that threw is a Result with OK=false, which the caller reports as
// an error with the script's own detail.
func (h *Host) Exec(ctx context.Context, user string, c Connector, target Target, script Script) (Result, *diag.Record) {
	if err := c.Validate(ctx, script); err != nil {
		return Result{}, diag.New(diag.SeverityError, "invalid-script", Source, err.Error())
	}
	timeout := script.Timeout
	if timeout <= 0 {
		timeout = h.defaultTimeout
	}
	if timeout > h.maxTimeout {
		return Result{}, diag.New(diag.SeverityError, "invalid-timeout", Source,
			fmt.Sprintf("timeout %s exceeds the maximum of %s", timeout, h.maxTimeout))
	}
	b, rec := h.resolve(user, c.Slug(), target)
	if rec != nil {
		return Result{}, rec
	}
	doc, found := b.Document(target.DocumentID)
	if target.DocumentID != "" && !found {
		// Advertised-but-unhonoured addressing must fail loudly, never fall
		// back to the active document (CONVENTIONS.md).
		return Result{}, diag.New(diag.SeverityError, "unknown-document", Source,
			fmt.Sprintf("bridge %s has no document with id %q", b.InstanceID, target.DocumentID)).
			WithRemedy("call list_instances for the current document ids")
	}

	hash := sha256.Sum256([]byte(script.Source))
	start := time.Now()
	res, err := h.bridges.Exec(ctx, b, bridge.ExecRequest{DocumentID: target.DocumentID, Script: script.Source, Language: script.Language, Timeout: timeout})
	// The audit line (§12): identities, hash, outcome, sizes — never the
	// script or the result.
	attrs := []any{"user", user, "connector", c.Slug(), "instance_id", b.InstanceID, "document", doc.ID,
		"script_sha256", hex.EncodeToString(hash[:8]), "script_bytes", len(script.Source), "elapsed", time.Since(start).Round(time.Millisecond)}
	if err != nil {
		h.log.Info("exec: failed", append(attrs, "err", err)...)
		return Result{}, execError(err, b, timeout)
	}
	code := ""
	if res.Error != nil {
		code = res.Error.Code
		if code == "" {
			code = res.Error.Name
		}
	}
	h.log.Info("exec: done", append(attrs, "ok", res.OK, "code", code, "result_bytes", len(res.Result), "truncated", res.Truncated, "notices", len(res.Notices))...)
	return Result{Reply: res, Instance: instanceOf(b), Document: doc}, nil
}

func execError(err error, b *registry.Bridge, timeout time.Duration) *diag.Record {
	switch {
	case errors.Is(err, bridge.ErrTimeout):
		return diag.New(diag.SeverityError, "timeout", Source,
			fmt.Sprintf("bridge %s did not reply within %s; a cancel was sent but the host may be unable to interrupt the script", b.InstanceID, timeout)).
			WithDetail(map[string]any{"timeout_ms": timeout.Milliseconds()}).
			WithRemedy("if the script was legitimately long, retry with a larger timeout_ms",
				"if the host is hung, reload its task pane; a late reply from this run will be discarded")
	case errors.Is(err, bridge.ErrBridgeGone):
		return diag.New(diag.SeverityError, "bridge-reconnected", Source,
			fmt.Sprintf("bridge %s disconnected or reconnected while the script was running; its outcome is unknown", b.InstanceID)).
			WithRemedy("call list_instances, inspect the document state, and re-run only if the script is safe to repeat")
	case errors.Is(err, context.Canceled):
		return diag.New(diag.SeverityError, "cancelled", Source, "the MCP request was cancelled before the script replied")
	default:
		return diag.New(diag.SeverityError, "bridge-send-failed", Source, err.Error())
	}
}
