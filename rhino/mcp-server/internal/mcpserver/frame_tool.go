package mcpserver

import (
	"context"
	"encoding/json"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
)

const frameSource = "mcp-server.internal.mcpserver"

// FrameCanvasIn is frame_canvas's input (PRD §11).
type FrameCanvasIn struct {
	InstanceID            string   `json:"instance_id" jsonschema:"instance_id of the target Rhino, from list_instances"`
	GrasshopperDocumentID string   `json:"gh_document_id,omitempty" jsonschema:"which open Grasshopper definition to frame (also made the active canvas document); omit for the active canvas definition"`
	Components            []string `json:"components,omitempty" jsonschema:"nicknames or instance guids (from inspect_gh_definition) to frame; omit to frame the whole definition"`
	UpstreamDepth         int      `json:"upstream_depth,omitempty" jsonschema:"also include this many levels of the components' sources (upstream); default 0 (just the components)"`
	DownstreamDepth       int      `json:"downstream_depth,omitempty" jsonschema:"also include this many levels of the components' recipients (downstream); default 0"`
	Padding               *float64 `json:"padding,omitempty" jsonschema:"canvas-unit margin around the framed region; default 20. 0 for a tight frame"`
}

// FrameCanvasOut is frame_canvas's result: the framed region, or an error.
type FrameCanvasOut struct {
	Frame *execution.FrameResult `json:"frame,omitempty"`
	Error *diag.Record           `json:"error,omitempty"`
}

// doFrame validates input and maps the result/diagnostic to the tool output. The frame call is injected so
// the handler is unit-testable without a live bridge. The bool is whether the result is an error.
func doFrame(in FrameCanvasIn, frame func(gh string, components []string, up, down int, padding float64) (*execution.FrameResult, *diag.Record)) (FrameCanvasOut, bool) {
	if in.InstanceID == "" {
		return FrameCanvasOut{Error: diag.New(diag.SeverityError, "invalid-param", frameSource, "instance_id is required").
			WithRemedy("pass instance_id from list_instances")}, true
	}
	// A nil padding means "use the bridge default"; encode that as -1 so a caller can still request 0.
	padding := -1.0
	if in.Padding != nil {
		padding = *in.Padding
	}
	res, drec := frame(in.GrasshopperDocumentID, in.Components, in.UpstreamDepth, in.DownstreamDepth, padding)
	if drec != nil {
		return FrameCanvasOut{Error: drec}, true
	}
	return FrameCanvasOut{Frame: res}, false
}

// RegisterFrame adds frame_canvas (PRD §11): frames the live Grasshopper canvas on a neighbourhood of
// components so a subsequent capture_view target=canvas shows just that region.
func RegisterFrame(s *mcp.Server, router *execution.Router) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "frame_canvas",
		Description: "Frame the open Grasshopper canvas on part of a definition, so capture_view target=canvas shows just that region. " +
			"Give one or more components (nicknames or guids from inspect_gh_definition) and, optionally, how many levels of their wiring to " +
			"include upstream (their sources) and downstream (their recipients) — both default 0, i.e. just the named components. Omit components " +
			"to frame the whole definition. Also makes the definition the active canvas document. Needs the Grasshopper editor open. This moves the " +
			"canvas view (like panning a viewport); it changes nothing in the document.",
	}, func(ctx context.Context, _ *mcp.CallToolRequest, in FrameCanvasIn) (*mcp.CallToolResult, FrameCanvasOut, error) {
		out, isErr := doFrame(in, func(gh string, components []string, up, down int, padding float64) (*execution.FrameResult, *diag.Record) {
			return router.FrameCanvas(ctx, in.InstanceID, gh, components, up, down, padding)
		})
		if isErr {
			return frameErr(out), out, nil
		}
		return nil, out, nil
	})
}

func frameErr(out FrameCanvasOut) *mcp.CallToolResult {
	b, _ := json.MarshalIndent(out, "", "  ")
	return &mcp.CallToolResult{IsError: true, Content: []mcp.Content{&mcp.TextContent{Text: string(b)}}}
}
