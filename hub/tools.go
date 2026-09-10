package hub

import (
	"context"
	"encoding/json"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub/diag"
)

// Error reporting follows the Revit server: a failed tool call is a normal
// result with IsError set and the diagnostic record both as the structured
// output's `error` field and serialised as the text content — that is what
// the calling model actually reads. Protocol-level JSON-RPC errors are for
// broken requests, not failed work.

// ErrorResult builds the failed-call result for rec. The SDK always marshals
// the handler's typed output into StructuredContent, so every tool's Out type
// carries an `error` field the handler sets to the same record alongside
// returning this.
func ErrorResult(rec *diag.Record) *mcp.CallToolResult {
	b, _ := json.MarshalIndent(map[string]any{"error": rec}, "", "  ")
	return &mcp.CallToolResult{
		IsError: true,
		Content: []mcp.Content{&mcp.TextContent{Text: string(b)}},
	}
}

// GetSkillsIn takes no arguments: the document is small enough to return
// whole (each connector's skill test enforces the budget), so there is no
// section selector to reason about.
type GetSkillsIn struct{}

// GetSkillsOut carries the document and enough provenance to notice a stale
// hub: the connector name and the hub build that served it.
type GetSkillsOut struct {
	Connector  string `json:"connector"`
	HubVersion string `json:"hub_version"`
	Skill      string `json:"skill"`
}

// ListInstancesIn takes no arguments; it always returns the caller's full
// live registry for this connector.
type ListInstancesIn struct{}

// ListInstancesOut lists the caller's live bridges.
type ListInstancesOut struct {
	Instances []Instance `json:"instances"`
	// SignedInAs is the Microsoft account this MCP session is signed in as
	// (email or display name), so the caller can compare it against what the
	// pane's own "Signed in as …" shows and spot an account mismatch (#251).
	// Empty when the identity can't be resolved.
	SignedInAs string `json:"signed_in_as,omitempty"`
	// Hint is set only when no bridge is connected: the pane and the session
	// must be the same Microsoft account or nothing routes, and an empty list
	// otherwise gives no clue why (#251).
	Hint  string       `json:"hint,omitempty"`
	Error *diag.Record `json:"error,omitempty"`
}

// registerGenericTools adds the tools every connector gets (§09): get_skills
// and list_instances.
func registerGenericTools(s *mcp.Server, h *Host, c Connector, version string) {
	skill := string(c.Skill())
	mcp.AddTool(s, &mcp.Tool{
		Name: "get_skills",
		Description: "Return this connector's skill file: the script contract, limits, target and safety rules, and the host quirks that are known to trip generated scripts. " +
			"Call it once at the start of a session before writing any script.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in GetSkillsIn) (*mcp.CallToolResult, GetSkillsOut, error) {
		out := GetSkillsOut{Connector: c.Slug(), HubVersion: version, Skill: skill}
		// The text content is the document itself, not a JSON wrapper, so a
		// client that only shows text still reads it as prose.
		return &mcp.CallToolResult{Content: []mcp.Content{&mcp.TextContent{Text: skill}}, StructuredContent: out}, out, nil
	})

	mcp.AddTool(s, &mcp.Tool{
		Name: "list_instances",
		Description: "List every " + c.Slug() + " bridge currently connected for you, with its host, open documents and instance_id. " +
			"With one bridge connected, tools target it by default; with several, pass instance_id.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in ListInstancesIn) (*mcp.CallToolResult, ListInstancesOut, error) {
		user, rec := h.User(req)
		if rec != nil {
			return ErrorResult(rec), ListInstancesOut{Instances: []Instance{}, Error: rec}, nil
		}
		out := ListInstancesOut{Instances: h.Instances(user, c.Slug()), SignedInAs: h.SignedInLabel(ctx, user)}
		if len(out.Instances) == 0 {
			out.Hint = accountMismatchHint(c.Slug(), out.SignedInAs)
		}
		return nil, out, nil
	})
}

// accountMismatchHint is the guidance list_instances returns when the caller
// has no connected bridge: the most common cause is the pane being signed in
// as a different Microsoft account than the session (#251), which otherwise
// presents as a silent empty list. signedInAs may be "" — the wording still
// makes sense.
func accountMismatchHint(slug, signedInAs string) string {
	account := "the same Microsoft account this session is signed in as"
	if signedInAs != "" {
		account = signedInAs
	}
	return "No " + slug + " bridge is connected for this MCP session. Open the MCP Bridge task pane in the host app and sign in with " + account +
		". If the pane is already open and shows Connected, it is signed in as a different Microsoft account — use 'Switch account' in the pane to match this session."
}
