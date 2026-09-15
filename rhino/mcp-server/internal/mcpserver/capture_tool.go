package mcpserver

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
)

// maxInlineCaptureBytes bounds what capture_view returns INLINE as image content
// (base64 inflates 4/3; the client's MCP output ceiling is ~500k chars). A larger
// capture is written to file_path instead (or refused, when no file_path is set).
const maxInlineCaptureBytes = 4 << 20

// CaptureViewIn is capture_view's input (rhino/docs/PRD.md §11).
type CaptureViewIn struct {
	InstanceID            string `json:"instance_id" jsonschema:"instance_id of the target Rhino, from list_instances"`
	DocumentID            string `json:"document_id,omitempty" jsonschema:"document to capture; omit for the active document"`
	Target                string `json:"target,omitempty" jsonschema:"\"active\" (default), \"all\" (one image per viewport), a viewport name such as Perspective, Top, Front, Right, or \"canvas\" to capture the open Grasshopper canvas as currently framed. display_mode/zoom/grid/axes do not apply to the canvas."`
	DisplayMode           string `json:"display_mode,omitempty" jsonschema:"display mode for the capture only (Wireframe, Shaded, Rendered, Ghosted, X-Ray, Technical, Artistic, Pen, Arctic, Raytraced); the viewport's own mode is restored after"`
	Zoom                  string `json:"zoom,omitempty" jsonschema:"\"none\" (default: the viewport as the person sees it), \"extents\" (zoom to everything), or \"selected\"; the previous view is restored after"`
	Width                 int    `json:"width,omitempty" jsonschema:"pixel width; default keeps the viewport's aspect at 1024 px on the long edge, hard cap 2048"`
	Height                int    `json:"height,omitempty" jsonschema:"pixel height; see width"`
	TransparentBackground bool   `json:"transparent_background,omitempty"`
	DrawGrid              *bool  `json:"draw_grid,omitempty" jsonschema:"draw the construction grid; default true"`
	DrawAxes              *bool  `json:"draw_axes,omitempty" jsonschema:"draw the world axes icon; default true"`
	Format                string `json:"format,omitempty" jsonschema:"\"jpeg\" (default, ~100 KB at the default size) or \"png\" (lossless, several times larger); a transparent background is always png"`
	FilePath              string `json:"file_path,omitempty" jsonschema:"absolute path on THIS machine (the one running the MCP server, i.e. where Rhino is) to save the image to; the returned image is still shown inline when small enough. A .png/.jpg extension sets the format when 'format' is not given. With target \"all\" the viewport name is appended to each file. Parent folders are created. Use it to keep a copy, or to get a large capture (png / a big width) that is too big to return inline."`
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
	MIMEType string `json:"mime_type"`
	// FilePath is where the image was written, when file_path was requested; empty otherwise.
	FilePath string `json:"file_path,omitempty"`
	// Inline is true when this image was also returned inline as image content (small enough).
	Inline bool `json:"inline"`
}

// RegisterCapture adds capture_view.
func RegisterCapture(s *mcp.Server, router *execution.Router) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "capture_view",
		Description: "Look at a Rhino viewport or the Grasshopper canvas: returns an image (JPEG by default) of the active viewport, a named one, all of them, " +
			"or the open Grasshopper canvas (target \"canvas\"), inline as image content, bounded to 1024 px on the long edge (2048 max). For a viewport, " +
			"optionally zoom to extents or selection and switch display mode for the shot; the viewport is restored afterwards and nothing in the document " +
			"changes. Pass file_path to also save the image to disk (and to get a capture too large to return inline). Busy while a script is running. " +
			"Use it to check what a script actually produced, or to see a Grasshopper definition.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in CaptureViewIn) (*mcp.CallToolResult, CaptureViewOut, error) {
		target := in.Target
		if target == "" {
			target = "active"
		}
		zoom := in.Zoom
		if zoom == "" {
			zoom = "none"
		}
		// When the caller names a file but not a format, let the file's extension choose it, so
		// "save shot.png" really writes a PNG rather than JPEG bytes under a .png name.
		format := in.Format
		if format == "" {
			format = formatFromExt(in.FilePath)
		}
		res, drec := router.CaptureView(ctx, in.InstanceID, execution.CaptureOptions{
			DocumentID: in.DocumentID, Target: target, DisplayMode: in.DisplayMode, Zoom: zoom,
			Width: in.Width, Height: in.Height, TransparentBackground: in.TransparentBackground, DrawGrid: in.DrawGrid, DrawAxes: in.DrawAxes, Format: format,
		})
		if drec != nil {
			out := CaptureViewOut{Images: []CapturedImageOut{}, Error: drec}
			b, _ := json.MarshalIndent(out, "", "  ")
			return &mcp.CallToolResult{IsError: true, Content: []mcp.Content{&mcp.TextContent{Text: string(b)}}}, out, nil
		}
		out := CaptureViewOut{Images: []CapturedImageOut{}, Notices: res.Notices}

		// Where each image is written, when file_path was requested (one path per image).
		paths, drec := captureFilePaths(in.FilePath, res.Images)
		if drec != nil {
			out.Error = drec
			return errorResult(out), out, nil
		}

		// Inline is all-or-nothing: the whole set fits the client's output budget, or none of it
		// does and it goes to disk (or is refused when there is nowhere to put it).
		total := 0
		for _, img := range res.Images {
			total += len(img.Bytes)
		}
		inline := total <= maxInlineCaptureBytes
		if !inline && in.FilePath == "" {
			out.Error = diag.New(diag.SeverityError, "capture-too-large", captureSource,
				fmt.Sprintf("the capture is %d KB decoded, over the ~%d MB that fits inline in one result", total>>10, maxInlineCaptureBytes>>20)).
				WithRemedy("pass file_path to save it to disk instead, or shrink it: capture one viewport, a smaller width, or format jpeg (the default) rather than png")
			return errorResult(out), out, nil
		}

		var content []mcp.Content
		for i, img := range res.Images {
			co := CapturedImageOut{Viewport: img.Viewport, Width: img.Width, Height: img.Height, Bytes: len(img.Bytes), MIMEType: img.MIMEType, Inline: inline}
			if paths != nil {
				if err := writeCaptureFile(paths[i], img.Bytes); err != nil {
					out.Error = diag.New(diag.SeverityError, "capture-write-failed", captureSource,
						fmt.Sprintf("could not write the capture to %q: %v", paths[i], err)).
						WithRemedy("check the path is writable on the machine running Rhino, and that its folder can be created")
					return errorResult(out), out, nil
				}
				co.FilePath = paths[i]
			}
			out.Images = append(out.Images, co)
			if inline {
				content = append(content, &mcp.TextContent{Text: fmt.Sprintf("%s (%dx%d)", img.Viewport, img.Width, img.Height)})
				content = append(content, &mcp.ImageContent{Data: img.Bytes, MIMEType: img.MIMEType})
			}
		}
		if !inline {
			// file_path is set (else we errored above): the image lives on disk, not inline.
			out.Notices = append(out.Notices, *diag.New(diag.SeverityInfo, "capture-saved-not-inlined", captureSource,
				fmt.Sprintf("the capture is %d KB, too large to show inline; it was written to disk instead", total>>10)).
				WithRemedy("open the saved file(s) to view; capture a smaller width or format jpeg if you want it inline"))
			content = append(content, &mcp.TextContent{Text: "saved to: " + strings.Join(paths, ", ")})
		}
		return &mcp.CallToolResult{Content: content}, out, nil
	})
}

// captureSource is the §01 source for capture_view's own (non-router) diagnostics.
const captureSource = "mcp-server.mcpserver.capture"

// errorResult renders a CaptureViewOut carrying an Error as an IsError tool result, matching the
// router-error shape.
func errorResult(out CaptureViewOut) *mcp.CallToolResult {
	b, _ := json.MarshalIndent(out, "", "  ")
	return &mcp.CallToolResult{IsError: true, Content: []mcp.Content{&mcp.TextContent{Text: string(b)}}}
}

// captureFilePaths resolves the on-disk target for each image, or nil when no file_path was given.
// A single image is written to file_path verbatim (a leading ~ expanded); with several images the
// viewport name is inserted before the extension so they do not overwrite each other.
func captureFilePaths(filePath string, images []execution.CapturedImage) ([]string, *diag.Record) {
	if filePath == "" {
		return nil, nil
	}
	if strings.HasPrefix(filePath, "~/") {
		if home, err := os.UserHomeDir(); err == nil {
			filePath = filepath.Join(home, filePath[2:])
		}
	}
	if len(images) <= 1 {
		return []string{filePath}, nil
	}
	ext := filepath.Ext(filePath)
	stem := strings.TrimSuffix(filePath, ext)
	paths := make([]string, len(images))
	for i, img := range images {
		e := ext
		if e == "" {
			e = extForMIME(img.MIMEType)
		}
		paths[i] = fmt.Sprintf("%s-%s%s", stem, safeViewportName(img.Viewport), e)
	}
	return paths, nil
}

// writeCaptureFile writes one image's bytes, creating parent folders as needed.
func writeCaptureFile(path string, data []byte) error {
	if dir := filepath.Dir(path); dir != "" {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return err
		}
	}
	return os.WriteFile(path, data, 0o644)
}

// formatFromExt maps a file_path extension to a capture format ("png"/"jpeg"), or "" when it is not a
// recognised image extension (the plug-in then applies its own default).
func formatFromExt(path string) string {
	switch strings.ToLower(filepath.Ext(path)) {
	case ".png":
		return "png"
	case ".jpg", ".jpeg":
		return "jpeg"
	default:
		return ""
	}
}

func extForMIME(mime string) string {
	switch mime {
	case "image/png":
		return ".png"
	case "image/jpeg":
		return ".jpg"
	default:
		return ""
	}
}

// safeViewportName makes a viewport name safe to splice into a filename.
func safeViewportName(name string) string {
	repl := func(r rune) rune {
		switch r {
		case '/', '\\', ':', '*', '?', '"', '<', '>', '|', ' ':
			return '_'
		}
		return r
	}
	out := strings.Map(repl, name)
	if out == "" {
		return "viewport"
	}
	return out
}
