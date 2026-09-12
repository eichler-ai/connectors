package mcpserver

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/restart"
)

const restartSource = "mcp-server.internal.mcpserver"

// RestartRhinoIn is restart_rhino's input (PRD §10/§15).
type RestartRhinoIn struct {
	InstanceID              string `json:"instance_id" jsonschema:"instance_id of the Rhino to restart, from list_instances"`
	ConfirmLifecycleActions bool   `json:"confirm_lifecycle_actions,omitempty" jsonschema:"set true to actually restart; without it the tool returns a preview (what would be quit and reopened) and does nothing"`
	DiscardUnsaved          bool   `json:"discard_unsaved,omitempty" jsonschema:"set true to restart even when a document has unsaved changes, discarding them; without it the tool refuses when anything is unsaved"`
}

// RestartRhinoOut is restart_rhino's result.
type RestartRhinoOut struct {
	// Status is "preview", "restarted", "blocked" (unsaved work) or "error".
	Status          string       `json:"status"`
	ReopenDocuments []string     `json:"reopen_documents,omitempty"`
	Unsaved         []string     `json:"unsaved,omitempty"`
	Notice          string       `json:"notice,omitempty"`
	Error           *diag.Record `json:"error,omitempty"`
}

// restartFn performs the actual restart; a var so tests can substitute it (the real one kills and
// relaunches a process).
var restartFn = func(ctx context.Context, pid int, reopen []string) (string, error) {
	return restart.Restart(ctx, restart.DefaultRunner(), pid, reopen)
}

// planRestart decides what a restart would do from the documents' save states: which saved Rhino files to
// reopen, what is unsaved, and whether to preview / block / proceed. Pure, so it is unit-tested directly.
func planRestart(states []execution.DocSaveState, confirm, discard bool) (status string, reopen, unsaved []string) {
	for _, s := range states {
		if s.Modified {
			unsaved = append(unsaved, s.Kind+" \""+s.Title+"\"")
		}
		if s.Kind == "rhino" && s.Path != "" {
			reopen = append(reopen, s.Path)
		}
	}
	switch {
	case !confirm:
		return "preview", reopen, unsaved
	case len(unsaved) > 0 && !discard:
		return "blocked", reopen, unsaved
	default:
		return "proceed", reopen, unsaved
	}
}

// RegisterRestart adds restart_rhino (PRD §10/§15): quit and relaunch a Rhino so a freshly installed
// plug-in loads, guarding unsaved work and reopening saved documents.
func RegisterRestart(s *mcp.Server, reg *registry.Registry, router *execution.Router) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "restart_rhino",
		Description: "Restart a Rhino instance so a freshly installed plug-in (install_plugin) loads -- Rhino loads new " +
			"packages only at startup. Gated: without confirm_lifecycle_actions it returns a preview of what would be quit " +
			"and reopened. It refuses (status \"blocked\") when any Rhino document or Grasshopper definition has unsaved " +
			"changes, unless discard_unsaved is set. On restart it reopens the SAVED Rhino documents by path; Grasshopper " +
			"definitions and unsaved/untitled documents are not reopened. macOS only in v1.",
	}, func(ctx context.Context, _ *mcp.CallToolRequest, in RestartRhinoIn) (*mcp.CallToolResult, RestartRhinoOut, error) {
		if in.InstanceID == "" {
			out := RestartRhinoOut{Status: "error", Error: diag.New(diag.SeverityError, "invalid-param", restartSource, "instance_id is required").WithRemedy("pass instance_id from list_instances")}
			return restartErr(out), out, nil
		}
		inst, ok := reg.Get(in.InstanceID)
		if !ok {
			out := RestartRhinoOut{Status: "error", Error: diag.New(diag.SeverityError, "instance-not-found", restartSource,
				fmt.Sprintf("no connected Rhino instance has instance_id %q", in.InstanceID)).WithRemedy("call list_instances and pick a current instance_id")}
			return restartErr(out), out, nil
		}
		states, drec := router.RestartSnapshot(ctx, in.InstanceID)
		if drec != nil {
			out := RestartRhinoOut{Status: "error", Error: drec}
			return restartErr(out), out, nil
		}

		status, reopen, unsaved := planRestart(states, in.ConfirmLifecycleActions, in.DiscardUnsaved)
		switch status {
		case "preview":
			return nil, RestartRhinoOut{
				Status: "preview", ReopenDocuments: reopen, Unsaved: unsaved,
				Notice: previewNotice(inst.PID, reopen, unsaved),
			}, nil
		case "blocked":
			out := RestartRhinoOut{
				Status: "blocked", Unsaved: unsaved,
				Error: diag.New(diag.SeverityError, "restart-blocked-unsaved", restartSource,
					"refusing to restart Rhino while these are unsaved: "+strings.Join(unsaved, ", ")).
					WithRemedy("save them first (a script with confirm_lifecycle_actions can Save), or pass discard_unsaved: true to restart and lose the changes"),
			}
			return restartErr(out), out, nil
		default: // proceed
			note, err := restartFn(ctx, inst.PID, reopen)
			if err != nil {
				out := RestartRhinoOut{Status: "error", Error: diag.New(diag.SeverityError, "restart-failed", restartSource, err.Error()).
					WithRemedy("restart Rhino manually to load the plug-in")}
				return restartErr(out), out, nil
			}
			return nil, RestartRhinoOut{Status: "restarted", ReopenDocuments: reopen, Notice: note}, nil
		}
	})
}

func previewNotice(pid int, reopen, unsaved []string) string {
	b := &strings.Builder{}
	fmt.Fprintf(b, "Would quit Rhino (pid %d) and relaunch it, reopening %d saved document(s).", pid, len(reopen))
	if len(unsaved) > 0 {
		fmt.Fprintf(b, " %d item(s) have unsaved changes (%s) — the restart will be refused unless you save them or pass discard_unsaved.", len(unsaved), strings.Join(unsaved, ", "))
	}
	b.WriteString(" Grasshopper definitions are not reopened automatically. Pass confirm_lifecycle_actions: true to proceed.")
	return b.String()
}

func restartErr(out RestartRhinoOut) *mcp.CallToolResult {
	b, _ := json.MarshalIndent(out, "", "  ")
	return &mcp.CallToolResult{IsError: true, Content: []mcp.Content{&mcp.TextContent{Text: string(b)}}}
}
