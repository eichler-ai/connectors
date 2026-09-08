package connector

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub"
	"github.com/eichler-ai/connectors/hub/diag"
)

// exportFormats are the only values export_file accepts; each is also the
// stored object's file extension (hub/internal/files: files/{user}/excel/{id}.{ext}).
var exportFormats = map[string]bool{"csv": true, "xlsx": true, "pdf": true}

// ExportFileIn is export_file's input.
type ExportFileIn struct {
	Format     string `json:"format" jsonschema:"csv, xlsx or pdf: csv is the active sheet's used range, xlsx and pdf are the whole workbook"`
	InstanceID string `json:"instance_id,omitempty" jsonschema:"which bridge to export from, from list_instances; required only when more than one is connected"`
	Filename   string `json:"filename,omitempty" jsonschema:"a filename hint for the download; the extension always matches format"`
}

// ExportFileOut is what a completed export looks like.
type ExportFileOut struct {
	URL        string       `json:"url,omitempty" jsonschema:"a short-lived signed URL; download it directly, no further auth"`
	Bytes      int64        `json:"bytes,omitempty"`
	Format     string       `json:"format,omitempty"`
	Filename   string       `json:"filename,omitempty"`
	ExpiresAt  string       `json:"expires_at,omitempty"`
	InstanceID string       `json:"instance_id,omitempty"`
	Error      *diag.Record `json:"error,omitempty"`
}

const exportFileDescription = "Export the connected workbook to a file: csv (the active sheet's used range, matching Excel's own CSV export), " +
	"xlsx (the whole workbook) or pdf (the whole workbook, all sheets). Returns a short-lived signed URL to download it, plus its size in bytes. " +
	"There is no base64 anywhere in this tool's output or in a script that produces one — use export_file, not execute_script, to get file bytes out."

func registerExportFile(reg *hub.ToolRegistry, c *Connector) {
	mcp.AddTool(reg.Server, &mcp.Tool{Name: "export_file", Description: exportFileDescription},
		func(ctx context.Context, req *mcp.CallToolRequest, in ExportFileIn) (*mcp.CallToolResult, ExportFileOut, error) {
			user, rec := reg.Host.User(req)
			if rec != nil {
				return fail(rec), ExportFileOut{Error: rec}, nil
			}
			if !exportFormats[in.Format] {
				rec := diag.New(diag.SeverityError, "invalid-format", Source, fmt.Sprintf("format %q is not one of csv, xlsx, pdf", in.Format))
				return fail(rec), ExportFileOut{Error: rec}, nil
			}
			res, rec := reg.Host.Export(ctx, user, c, hub.Target{InstanceID: in.InstanceID}, hub.ExportRequest{Format: in.Format})
			if rec != nil {
				return fail(rec), ExportFileOut{Error: rec}, nil
			}
			filename := exportFilename(in.Filename, in.Format)
			out := ExportFileOut{
				URL: res.URL, Bytes: res.Bytes, Format: in.Format, Filename: filename,
				ExpiresAt: res.ExpiresAt.UTC().Format(time.RFC3339), InstanceID: res.Instance.InstanceID,
			}
			// A resource_link references the download by URL rather than
			// inlining the bytes as base64 into the tool result: re-embedding
			// a multi-MiB export into the MCP transport would reintroduce
			// exactly the "base64 in a script" problem the file exchange
			// exists to avoid (PRD §10), just one layer up. A client that
			// can act on a resource_link (offer a download, fetch it itself)
			// gets one; every client still has the URL in the structured
			// output and in the text content below.
			text := fmt.Sprintf("Exported %s (%d bytes): %s (expires %s)", filename, res.Bytes, res.URL, out.ExpiresAt)
			content := []mcp.Content{
				&mcp.TextContent{Text: text},
				&mcp.ResourceLink{URI: res.URL, Name: filename, MIMEType: exportMIMEType(in.Format), Size: &res.Bytes},
			}
			return &mcp.CallToolResult{Content: content, StructuredContent: out}, out, nil
		})
}

// exportFilename applies the format's extension to hint, defaulting to
// "workbook" when the caller gave none.
func exportFilename(hint, format string) string {
	name := strings.TrimSpace(hint)
	if name == "" {
		name = "workbook"
	}
	name = strings.TrimSuffix(name, "."+format)
	return name + "." + format
}

func exportMIMEType(format string) string {
	switch format {
	case "csv":
		return "text/csv"
	case "xlsx":
		return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
	case "pdf":
		return "application/pdf"
	default:
		return "application/octet-stream"
	}
}
