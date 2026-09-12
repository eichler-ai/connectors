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
	GrasshopperDocumentID   string `json:"gh_document_id,omitempty" jsonschema:"optional gh_document_id (from list_instances' grasshopper_documents) of the Grasshopper definition to bind: the script's ghdoc (Python) / GrasshopperDocument (C#) global becomes that GH_Document. Omit to leave it null. In C# it is typed object -- cast it: (Grasshopper.Kernel.GH_Document)GrasshopperDocument. An id matching no open definition fails with grasshopper-document-not-found (Grasshopper must be open with that definition loaded)"`
	Language                string `json:"language" jsonschema:"\"csharp\" or \"python\". Required, no default: the two hosts differ and a script for one does not run in the other. Python is real CPython 3 (Rhino 8's own): globals doc, ghdoc, connector, cancel; assign result to return a value; rhinoscriptsyntax and scriptcontext work and scriptcontext.doc is the routed document. A bridge whose Python host is still loading answers language-not-available with the reason; retry in a few seconds"`
	Script                  string `json:"script" jsonschema:"the script body. C#: a Roslyn script whose scope holds exactly three globals, Document (Rhino.RhinoDoc), CancellationToken and Connector; only System is imported, so qualify Rhino types; return a value with a return statement. Python: CPython 3 with globals doc (Rhino.RhinoDoc), ghdoc, connector and cancel (cancel.Check() raises when cancelled); import Rhino, rhinoscriptsyntax, scriptcontext as usual; assign a variable named result to return a value; print goes to output"`
	TimeoutMs               int    `json:"timeout_ms,omitempty" jsonschema:"milliseconds to wait for completion before returning a pending/running status; default 30000"`
	MaxDurationMs           int    `json:"max_duration_ms,omitempty" jsonschema:"hard ceiling on the run's total time, independent of timeout_ms; on lapse the bridge cancels the run cooperatively; default 600000"`
	ConfirmLifecycleActions bool   `json:"confirm_lifecycle_actions,omitempty" jsonschema:"set true to allow RhinoDoc.Save/SaveAs/Export/Close/Open/Create and the RunScript command tokens that do the same; these act outside the document's content (the filesystem, the person's open session, which documents are open) so the post-run undo cannot revert them, and without this flag such a script is refused before it runs (script-lifecycle-confirmation-required)"`
	Label                   string `json:"label,omitempty" jsonschema:"short name for what this run does. Rhino names the run's Undo entry after the bridge's command, so the label does not appear there; it is echoed on the result, exposed to the script as Connector.RunLabel, and reported by the undo tool when it reverts this run"`
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
	Status      string                       `json:"status"`
	ExecutionID string                       `json:"execution_id,omitempty"`
	Output      string                       `json:"output,omitempty"`
	ReturnValue string                       `json:"return_value,omitempty"`
	Notices     []diag.Record                `json:"notices,omitempty"`
	Files       []execution.FileRecord       `json:"files,omitempty"`
	Mutations   *execution.MutationReport    `json:"mutations,omitempty"`
	Grasshopper *execution.GrasshopperReport `json:"grasshopper,omitempty"`
	Error       *diag.Record                 `json:"error,omitempty"`
	LastRun     *execution.LastRun           `json:"last_run,omitempty"`
}

// UndoRedoIn is the input shared by the undo and redo tools (PRD §07).
type UndoRedoIn struct {
	InstanceID string `json:"instance_id" jsonschema:"instance_id of the target Rhino, from list_instances"`
	DocumentID string `json:"document_id,omitempty" jsonschema:"the document to act on; omit for the active document. Refused loudly (document-not-found) when no open document has this id"`
	Confirm    bool   `json:"confirm,omitempty" jsonschema:"needed only when the top of the undo stack is NOT the connector's own work: the plug-in tracks document changes made outside its own runs, and an undo of its own run (or a redo right after its own undo) with none since runs without confirm. Otherwise the call is refused (undo-confirmation-required) naming the last command Rhino ran; resend with confirm: true if reverting that is intended"`
	TimeoutMs  int    `json:"timeout_ms,omitempty" jsonschema:"how long to wait for the main thread; default 10000, max 30000"`
}

// RegisterUndoRedo adds undo and redo.
func RegisterUndoRedo(s *mcp.Server, router *execution.Router) {
	for _, direction := range []string{"undo", "redo"} {
		direction := direction
		opposite := "redo"
		if direction == "redo" {
			opposite = "undo"
		}
		mcp.AddTool(s, &mcp.Tool{
			Name: direction,
			Description: "Run Rhino's " + direction + " command on a document and report what it did: mutations carries the net change and " +
				"notices[] says whose work it was -- undo-reverted-connector-work (info) names the connector's run and label; " +
				"undo-reverted-other-work (warning) means a person's action was " + direction + "ne, as confirmed: call " + opposite + " at once if unintended. " +
				"The plug-in tracks document changes made outside its own runs, so an " + direction + " of its own work with none since needs no confirm. " +
				"For a mistake INSIDE a script, roll back there instead (raise/throw; the connector reverts the run). " +
				"Busy while a script runs, and scripts are busy while this runs.",
		}, func(ctx context.Context, req *mcp.CallToolRequest, in UndoRedoIn) (*mcp.CallToolResult, ExecutionOut, error) {
			timeoutMs := in.TimeoutMs
			if timeoutMs <= 0 {
				timeoutMs = 10_000
			}
			if timeoutMs > 30_000 {
				timeoutMs = 30_000
			}
			res, drec := router.UndoRedo(ctx, in.InstanceID, direction, in.Confirm, timeoutMs, in.DocumentID)
			return toolResult(res, drec)
		})
	}
}

// RegisterExecution adds execute_script, poll_execution and cancel_execution.
func RegisterExecution(s *mcp.Server, router *execution.Router) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "execute_script",
		Description: "Compile and run a script against an open Rhino document, inside one Rhino command that becomes one entry in the Undo history; " +
			"a script that throws is undone. Returns the completed result if it finishes within timeout_ms, otherwise a pending/running/busy status " +
			"with an execution_id for poll_execution. C# scope: Document (Rhino.RhinoDoc), CancellationToken, Connector (this connector's own API, " +
			"under the Eichler.Connectors.Rhino namespace). Interactive getters (RhinoGet, GetObject, GetPoint...) are refused: nobody is at the keyboard. " +
			"A run that triggers a Grasshopper solve carries a grasshopper report (solutions[] plus every component that errored/warned or ended non-Computed); errors are reported, not auto-resolved. " +
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
			Language: in.Language, DocumentID: in.DocumentID, GrasshopperDocumentID: in.GrasshopperDocumentID, TimeoutMs: timeoutMs, MaxDurationMs: maxDurationMs,
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
		Notices: res.Notices, Files: res.Files, Mutations: res.Mutations, Grasshopper: res.Grasshopper, Error: res.ErrorDetail, LastRun: res.LastRun}
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
