// Package mcpserver registers the agent-facing MCP tools — execute_script,
// poll_execution, cancel_execution (PRD §04/§06; Phase 1 scope per §15) —
// against the official MCP Go SDK's Server, delegating routing to an
// execution.Manager.
//
// Error reporting: the MCP tools/call spec expects a failed tool call to
// come back as a normal result with IsError set and human/agent-readable
// Content, not as a JSON-RPC protocol-level error — that's how the calling
// model actually gets to see and react to the failure. So here the shared
// diagnostic-record shape (PRD §01) is carried both in CallToolResult's
// structured output (the `error` field) and serialized as the result's text
// content, rather than in the JSON-RPC envelope's error.data the way it is
// on the broker<->add-in wire protocol (internal/transport), where a bare
// JSON-RPC error is the only channel available and does carry the record in
// error.data directly.
package mcpserver

import (
	"context"
	"encoding/json"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/execution"
)

const (
	defaultTimeoutMs     = 30_000
	defaultMaxDurationMs = 600_000
)

// ExecuteScriptIn is the input schema for the execute_script tool.
type ExecuteScriptIn struct {
	InstanceID           string `json:"instance_id" jsonschema:"instance_id of the target Revit instance, from a prior register/list_instances"`
	DocumentID           string `json:"document_id" jsonschema:"document_id of the target document within that instance"`
	Script               string `json:"script" jsonschema:"C# script body to compile and run against the document"`
	TimeoutMs            int    `json:"timeout_ms,omitempty" jsonschema:"milliseconds to wait for completion before returning a pending/running status; default 30000"`
	MaxDurationMs        int    `json:"max_duration_ms,omitempty" jsonschema:"hard ceiling on total script runtime in milliseconds, independent of timeout_ms; default 600000"`
	OverwriteOutputFiles bool   `json:"overwrite_output_files,omitempty" jsonschema:"if true, Publish() calls that would overwrite an existing exported file succeed and replace it; if false (default), such a collision fails that one file's publish rather than overwriting it silently"`
}

// PollExecutionIn is the input schema for the poll_execution tool.
type PollExecutionIn struct {
	ExecutionID string `json:"execution_id" jsonschema:"execution_id returned by a prior execute_script or poll_execution call"`
	TimeoutMs   int    `json:"timeout_ms,omitempty" jsonschema:"milliseconds to wait for completion before returning the current status again; default 30000"`
}

// CancelExecutionIn is the input schema for the cancel_execution tool.
type CancelExecutionIn struct {
	ExecutionID string `json:"execution_id" jsonschema:"execution_id of the in-flight execution to cancel"`
}

// ExecutionOut is the output schema shared by all three tools — PRD §06's
// two-shape contract: either a terminal result (Status
// success/error/cancelled/unrecoverable, with Output/Notices as relevant)
// or a non-terminal status (pending/running/busy) with ExecutionID for the
// caller to poll.
type ExecutionOut struct {
	Status      string                 `json:"status"`
	ExecutionID string                 `json:"execution_id"`
	Output      string                 `json:"output,omitempty"`
	Notices     []diag.Record          `json:"notices,omitempty"`
	Files       []execution.FileRecord `json:"files,omitempty"`
	Error       *diag.Record           `json:"error,omitempty"`
}

// Register adds execute_script, poll_execution, and cancel_execution to s,
// routed through mgr.
func Register(s *mcp.Server, mgr *execution.Manager) {
	mcp.AddTool(s, &mcp.Tool{
		Name:        "execute_script",
		Description: "Compile and run a C# script against an open Revit document. Returns the completed result if it finishes within timeout_ms, otherwise a pending/running/busy status with an execution_id to pass to poll_execution.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in ExecuteScriptIn) (*mcp.CallToolResult, ExecutionOut, error) {
		timeoutMs := in.TimeoutMs
		if timeoutMs <= 0 {
			timeoutMs = defaultTimeoutMs
		}
		maxDurationMs := in.MaxDurationMs
		if maxDurationMs <= 0 {
			maxDurationMs = defaultMaxDurationMs
		}
		res, drec := mgr.ExecuteScript(ctx, in.InstanceID, in.DocumentID, in.Script, timeoutMs, maxDurationMs, in.OverwriteOutputFiles)
		return toolResult(res, drec)
	})

	mcp.AddTool(s, &mcp.Tool{
		Name:        "poll_execution",
		Description: "Poll a previously started execution until it completes. Returns the completed result if it finishes within timeout_ms, otherwise the current pending/running status again.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in PollExecutionIn) (*mcp.CallToolResult, ExecutionOut, error) {
		timeoutMs := in.TimeoutMs
		if timeoutMs <= 0 {
			timeoutMs = defaultTimeoutMs
		}
		res, drec := mgr.PollExecution(ctx, in.ExecutionID, timeoutMs)
		return toolResult(res, drec)
	})

	mcp.AddTool(s, &mcp.Tool{
		Name:        "cancel_execution",
		Description: "Request cooperative cancellation of an in-flight execution. The script must observe its CancellationToken to actually stop; a script that doesn't cooperate (or never responds) resolves to \"unrecoverable\" once the broker's own cancellation grace period lapses.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in CancelExecutionIn) (*mcp.CallToolResult, ExecutionOut, error) {
		res, drec := mgr.CancelExecution(ctx, in.ExecutionID)
		return toolResult(res, drec)
	})
}

// isErrorStatus reports whether status is one the agent must see flagged as
// IsError:true, per PRD §01's shared record and the MCP tools/call
// convention that a failed call surfaces as a normal result with IsError
// set (not a bare JSON-RPC error) so the calling model can see and react to
// it. "cancelled" is deliberately excluded: the agent asked for that one.
func isErrorStatus(s execution.Status) bool {
	return s == execution.StatusError || s == execution.StatusUnrecoverable
}

func toolResult(res *execution.Result, drec *diag.Record) (*mcp.CallToolResult, ExecutionOut, error) {
	if drec != nil {
		out := ExecutionOut{Status: "error", Error: drec}
		return errorCallToolResult(out), out, nil
	}
	out := ExecutionOut{
		Status:      string(res.Status),
		ExecutionID: res.ExecutionID,
		Output:      res.Output,
		Notices:     res.Notices,
		Files:       res.Files,
		Error:       res.ErrorDetail,
	}
	if isErrorStatus(res.Status) {
		// The wire round trip itself succeeded, but the add-in reported a
		// terminal error/unrecoverable outcome (out.Error carries the
		// detail) — this must be flagged IsError:true exactly like a
		// wire-level failure, or the agent sees a "successful" tool call
		// that actually failed.
		return errorCallToolResult(out), out, nil
	}
	return nil, out, nil
}

func errorCallToolResult(out ExecutionOut) *mcp.CallToolResult {
	b, _ := json.MarshalIndent(out, "", "  ")
	return &mcp.CallToolResult{
		IsError: true,
		Content: []mcp.Content{&mcp.TextContent{Text: string(b)}},
	}
}
