package mcpserver

import (
	"context"
	"encoding/json"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
)

const (
	defaultTimeoutMs     = 30_000
	defaultMaxDurationMs = 600_000
)

// ExecuteScriptIn is execute_script's input (rhino/docs/PRD.md §06).
type ExecuteScriptIn struct {
	InstanceID              string `json:"instance_id" jsonschema:"instance_id of the target Rhino, from list_instances"`
	DocumentID              string `json:"document_id,omitempty" jsonschema:"document_id of the target document within that instance (from list_instances). Routes for real: the script runs against that document and an id matching no open document fails with document-not-found plus an open_documents list. Omit for the active document. On Windows every instance has exactly one document"`
	Language                string `json:"language" jsonschema:"\"csharp\" or \"python\". Required, no default: the two hosts differ and a script for one does not run in the other. Python lands in a later bridge build; a build without it answers language-not-available"`
	Script                  string `json:"script" jsonschema:"the script body. C#: a Roslyn script whose scope holds exactly three globals, Document (Rhino.RhinoDoc), CancellationToken and Connector; only System is imported, so qualify Rhino types. Return a value with a return statement"`
	TimeoutMs               int    `json:"timeout_ms,omitempty" jsonschema:"milliseconds to wait for completion before returning a pending/running status; default 30000"`
	MaxDurationMs           int    `json:"max_duration_ms,omitempty" jsonschema:"hard ceiling on the run's total time, independent of timeout_ms; on lapse the bridge cancels the run cooperatively; default 600000"`
	ConfirmLifecycleActions bool   `json:"confirm_lifecycle_actions,omitempty" jsonschema:"set true to allow RhinoDoc.Save/SaveAs/Export/Close/Open/Create and the RunScript command tokens that do the same; these act outside the document's content (the filesystem, the person's open session, which documents are open) so the post-run undo cannot revert them, and without this flag such a script is refused before it runs (script-lifecycle-confirmation-required)"`
	Label                   string `json:"label,omitempty" jsonschema:"short name for what this run does, shown as its entry in Rhino's Undo history prefixed 'MCP: '"`
}

// PollExecutionIn is poll_execution's input.
type PollExecutionIn struct {
	ExecutionID string `json:"execution_id" jsonschema:"execution_id returned by a prior execute_script or poll_execution"`
	TimeoutMs   int    `json:"timeout_ms,omitempty" jsonschema:"milliseconds to wait for completion before returning the current status again; default 30000"`
}

// CancelExecutionIn is cancel_execution's input.
type CancelExecutionIn struct {
	ExecutionID string `json:"execution_id" jsonschema:"execution_id of the in-flight execution"`
}

// ExecutionOut is the shared result shape (PRD §06's two-shape contract).
type ExecutionOut struct {
	Status      string                    `json:"status"`
	ExecutionID string                    `json:"execution_id,omitempty"`
	Output      string                    `json:"output,omitempty"`
	ReturnValue string                    `json:"return_value,omitempty"`
	Notices     []diag.Record             `json:"notices,omitempty"`
	Files       []execution.FileRecord    `json:"files,omitempty"`
	Mutations   *execution.MutationReport `json:"mutations,omitempty"`
	Error       *diag.Record              `json:"error,omitempty"`
}

// RegisterExecution adds execute_script, poll_execution and cancel_execution.
func RegisterExecution(s *mcp.Server, router *execution.Router) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "execute_script",
		Description: "Compile and run a script against an open Rhino document, inside one Rhino command that becomes one entry in the Undo history; " +
			"a script that throws is undone. Returns the completed result if it finishes within timeout_ms, otherwise a pending/running/busy status " +
			"with an execution_id for poll_execution. C# scope: Document (Rhino.RhinoDoc), CancellationToken, Connector (this connector's own API, " +
			"under the Eichler.Connectors.Rhino namespace). Interactive getters (RhinoGet, GetObject, GetPoint...) are refused: nobody is at the keyboard. " +
			"Call get_skills for the rules.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in ExecuteScriptIn) (*mcp.CallToolResult, ExecutionOut, error) {
		if in.Language != "csharp" && in.Language != "python" {
			return toolError(diag.New(diag.SeverityError, "invalid-param", "mcp-server.internal.mcpserver",
				"language must be \"csharp\" or \"python\"").WithRemedy("pass language: \"csharp\""))
		}
		if in.Script == "" {
			return toolError(diag.New(diag.SeverityError, "invalid-param", "mcp-server.internal.mcpserver", "script is required and empty").WithRemedy("pass the script body in script"))
		}
		timeoutMs, maxDurationMs := in.TimeoutMs, in.MaxDurationMs
		if timeoutMs <= 0 {
			timeoutMs = defaultTimeoutMs
		}
		if maxDurationMs <= 0 {
			maxDurationMs = defaultMaxDurationMs
		}
		res, drec := router.ExecuteScript(ctx, in.InstanceID, in.Script, execution.Options{
			Language: in.Language, DocumentID: in.DocumentID, TimeoutMs: timeoutMs, MaxDurationMs: maxDurationMs,
			ConfirmLifecycleActions: in.ConfirmLifecycleActions, Label: in.Label,
		})
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
		res, drec := router.PollExecution(ctx, in.ExecutionID, timeoutMs)
		return toolResult(res, drec)
	})

	mcp.AddTool(s, &mcp.Tool{
		Name:        "cancel_execution",
		Description: "Request cooperative cancellation of an in-flight execution. The script must observe its CancellationToken to actually stop; one that does not resolves to \"unrecoverable\" once the bridge's grace period lapses, and that Rhino must then be restarted.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in CancelExecutionIn) (*mcp.CallToolResult, ExecutionOut, error) {
		res, drec := router.CancelExecution(ctx, in.ExecutionID)
		return toolResult(res, drec)
	})
}

func toolError(drec *diag.Record) (*mcp.CallToolResult, ExecutionOut, error) {
	out := ExecutionOut{Status: "error", Error: drec}
	return errorCallToolResult(out), out, nil
}

func toolResult(res *execution.Result, drec *diag.Record) (*mcp.CallToolResult, ExecutionOut, error) {
	if drec != nil {
		return toolError(drec)
	}
	out := ExecutionOut{Status: res.Status, ExecutionID: res.ExecutionID, Output: res.Output, ReturnValue: res.ReturnValue,
		Notices: res.Notices, Files: res.Files, Mutations: res.Mutations, Error: res.ErrorDetail}
	if res.Status == "error" || res.Status == "unrecoverable" {
		return errorCallToolResult(out), out, nil
	}
	return nil, out, nil
}

// errorCallToolResult: MCP's tools/call contract -- a failed call is a normal result with IsError
// and readable content, so the calling model sees it (the Revit server's reasoning).
func errorCallToolResult(out ExecutionOut) *mcp.CallToolResult {
	b, _ := json.MarshalIndent(out, "", "  ")
	return &mcp.CallToolResult{IsError: true, Content: []mcp.Content{&mcp.TextContent{Text: string(b)}}}
}
