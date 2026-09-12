package mcpserver

import (
	"context"
	"encoding/json"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
)

const inspectSource = "mcp-server.internal.mcpserver"

// InspectDefinitionIn is inspect_gh_definition's input (PRD §10).
type InspectDefinitionIn struct {
	InstanceID            string `json:"instance_id" jsonschema:"instance_id of the target Rhino, from list_instances"`
	GrasshopperDocumentID string `json:"gh_document_id,omitempty" jsonschema:"which open Grasshopper definition to inspect; omit for the active canvas definition (its gh_document_id is echoed back for later calls)"`
	NameFilter            string `json:"name_filter,omitempty" jsonschema:"narrow to objects whose nickname or type name contains this (case-insensitive), e.g. \"slider\""`
	Offset                int    `json:"offset,omitempty" jsonschema:"start of the page into the matching objects; default 0"`
	Limit                 int    `json:"limit,omitempty" jsonschema:"objects per page; default 200, capped at 500"`
}

// InspectDefinitionOut is inspect_gh_definition's result: the definition's structure, or an error.
type InspectDefinitionOut struct {
	Definition *execution.InspectResult `json:"definition,omitempty"`
	Error      *diag.Record             `json:"error,omitempty"`
}

// doInspect validates the input and maps the result/diagnostic to the tool output. The inspect call is
// injected so the handler is unit-testable without a live bridge. The bool is whether the result is an error.
func doInspect(in InspectDefinitionIn, inspect func(ghDocumentID, nameFilter string, offset, limit int) (*execution.InspectResult, *diag.Record)) (InspectDefinitionOut, bool) {
	if in.InstanceID == "" {
		return InspectDefinitionOut{Error: diag.New(diag.SeverityError, "invalid-param", inspectSource, "instance_id is required").
			WithRemedy("pass instance_id from list_instances")}, true
	}
	res, drec := inspect(in.GrasshopperDocumentID, in.NameFilter, in.Offset, in.Limit)
	if drec != nil {
		return InspectDefinitionOut{Error: drec}, true
	}
	return InspectDefinitionOut{Definition: res}, false
}

// RegisterInspect adds inspect_gh_definition (PRD §10): a read-only look at an open Grasshopper definition's
// structure — its objects, their canvas positions, and how they are wired — so an agent can learn the
// nicknames to drive/read and can frame a canvas capture.
func RegisterInspect(s *mcp.Server, router *execution.Router) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "inspect_gh_definition",
		Description: "Read the structure of an open Grasshopper definition: every object on the canvas with its instance guid, " +
			"nickname, type, canvas position (pivot and bounds), and component-level wiring (upstream/downstream neighbour guids). " +
			"Omit gh_document_id for the active canvas definition; its id is echoed back for execute_script/capture. Narrow with " +
			"name_filter and page with offset/limit (default 200, max 500). Read-only. Use it to learn the nicknames to drive with " +
			"Connector.Grasshopper, to understand how a definition is put together, and to pick the objects to frame in a canvas capture.",
	}, func(ctx context.Context, _ *mcp.CallToolRequest, in InspectDefinitionIn) (*mcp.CallToolResult, InspectDefinitionOut, error) {
		out, isErr := doInspect(in, func(ghDocumentID, nameFilter string, offset, limit int) (*execution.InspectResult, *diag.Record) {
			return router.InspectDefinition(ctx, in.InstanceID, ghDocumentID, nameFilter, offset, limit)
		})
		if isErr {
			return inspectErr(out), out, nil
		}
		return nil, out, nil
	})
}

func inspectErr(out InspectDefinitionOut) *mcp.CallToolResult {
	b, _ := json.MarshalIndent(out, "", "  ")
	return &mcp.CallToolResult{IsError: true, Content: []mcp.Content{&mcp.TextContent{Text: string(b)}}}
}
