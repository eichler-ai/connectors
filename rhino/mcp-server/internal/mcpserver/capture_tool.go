package mcpserver

import (
	"context"
	"encoding/json"
	"fmt"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
)

// CaptureViewIn is capture_view's input (rhino/docs/PRD.md §11).
type CaptureViewIn struct {
	InstanceID            string `json:"instance_id" jsonschema:"instance_id of the target Rhino, from list_instances"`
	DocumentID            string `json:"document_id,omitempty" jsonschema:"document to capture; omit for the active document"`
	Target                string `json:"target,omitempty" jsonschema:"\"active\" (default), \"all\" (one image per viewport), or a viewport name such as Perspective, Top, Front, Right"`
	DisplayMode           string `json:"display_mode,omitempty" jsonschema:"display mode for the capture only (Wireframe, Shaded, Rendered, Ghosted, X-Ray, Technical, Artistic, Pen, Arctic, Raytraced); the viewport's own mode is restored after"`
	Zoom                  string `json:"zoom,omitempty" jsonschema:"\"none\" (default: the viewport as the person sees it), \"extents\" (zoom to everything), or \"selected\"; the previous view is restored after"`
	Width                 int    `json:"width,omitempty" jsonschema:"pixel width; default keeps the viewport's aspect at 1280 px on the long edge, hard cap 2048"`
	Height                int    `json:"height,omitempty" jsonschema:"pixel height; see width"`
	TransparentBackground bool   `json:"transparent_background,omitempty"`
	DrawGrid              *bool  `json:"draw_grid,omitempty" jsonschema:"draw the construction grid; default true"`
	DrawAxes              *bool  `json:"draw_axes,omitempty" jsonschema:"draw the world axes icon; default true"`
}

// CaptureViewOut is the structured half of the result; the images themselves are MCP image content.
type CaptureViewOut struct {
	Images  []CapturedImageOut `json:"images"`
	Notices []diag.Record      `json:"notices,omitempty"`
	Error   *diag.Record       `json:"error,omitempty"`
}

// CapturedImageOut describes one returned image.
type CapturedImageOut struct {
	Viewport string `json:"viewport"`
	Width    int    `json:"width"`
	Height   int    `json:"height"`
	Bytes    int    `json:"bytes"`
}

// RegisterCapture adds capture_view.
func RegisterCapture(s *mcp.Server, router *execution.Router) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "capture_view",
		Description: "Look at a Rhino viewport: returns a PNG of the active viewport, a named one, or all of them, inline as image content, " +
			"bounded to 1280 px on the long edge (2048 max). Optionally zoom to extents or selection and switch display mode for the " +
			"shot; the viewport is restored afterwards and nothing in the document changes. Busy while a script is running. " +
			"Use it to check what a script actually produced.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in CaptureViewIn) (*mcp.CallToolResult, CaptureViewOut, error) {
		target := in.Target
		if target == "" {
			target = "active"
		}
		zoom := in.Zoom
		if zoom == "" {
			zoom = "none"
		}
		res, drec := router.CaptureView(ctx, in.InstanceID, execution.CaptureOptions{
			DocumentID: in.DocumentID, Target: target, DisplayMode: in.DisplayMode, Zoom: zoom,
			Width: in.Width, Height: in.Height, TransparentBackground: in.TransparentBackground, DrawGrid: in.DrawGrid, DrawAxes: in.DrawAxes,
		})
		if drec != nil {
			out := CaptureViewOut{Images: []CapturedImageOut{}, Error: drec}
			b, _ := json.MarshalIndent(out, "", "  ")
			return &mcp.CallToolResult{IsError: true, Content: []mcp.Content{&mcp.TextContent{Text: string(b)}}}, out, nil
		}
		out := CaptureViewOut{Images: []CapturedImageOut{}, Notices: res.Notices}
		var content []mcp.Content
		for _, img := range res.Images {
			out.Images = append(out.Images, CapturedImageOut{Viewport: img.Viewport, Width: img.Width, Height: img.Height, Bytes: len(img.PNG)})
			content = append(content, &mcp.TextContent{Text: fmt.Sprintf("%s (%dx%d)", img.Viewport, img.Width, img.Height)})
			content = append(content, &mcp.ImageContent{Data: img.PNG, MIMEType: img.MIMEType})
		}
		return &mcp.CallToolResult{Content: content}, out, nil
	})
}
