package connector

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"strings"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub"
	"github.com/eichler-ai/connectors/hub/diag"
	"github.com/eichler-ai/connectors/internal/xlsxgen"
)

// CreateWorkbookSheet is one worksheet of create_workbook's from-data
// source: rows[i][j] is the cell at row i+1, column j+1. A cell is a
// string, number, bool, or a formula string starting with "=" — the same
// convention execute_script uses (skill.md "Cell values").
type CreateWorkbookSheet struct {
	Name string  `json:"name,omitempty" jsonschema:"sheet name; default Sheet1, Sheet2, ..."`
	Rows [][]any `json:"rows,omitempty" jsonschema:"2-D array of cell values; a null cell is left empty"`
}

// CreateWorkbookIn is create_workbook's input: name is always a hint (the
// actual OneDrive filename embeds a short unique token — see doc_key
// below), data present means from-data, absent means blank.
type CreateWorkbookIn struct {
	Name string                `json:"name,omitempty" jsonschema:"a filename hint; default Workbook. The actual OneDrive filename appends a short unique token"`
	Data []CreateWorkbookSheet `json:"data,omitempty" jsonschema:"sheets to populate the new workbook with; omit for a blank workbook"`
}

// CreateWorkbookOut is what a completed create_workbook looks like.
type CreateWorkbookOut struct {
	WebURL string `json:"web_url,omitempty" jsonschema:"the link to hand the user; opening it opens the workbook in Excel for the web"`
	// DocKey is the identity execute_script/list_instances will see once
	// the pane connects to this file (RFC excel/docs/rfc-graph-create-and-
	// open.md §3.3): match a list_instances document's id against this to
	// find the bridge for the file this call just created.
	DocKey   string       `json:"doc_key,omitempty"`
	OpenHint string       `json:"open_hint,omitempty"`
	Error    *diag.Record `json:"error,omitempty"`
}

const createWorkbookDescription = "Create a new Excel workbook in the user's OneDrive and return a link to open it. " +
	"Pass data (sheets of rows) to populate it, or omit data for a blank workbook. " +
	"The response's doc_key is the document id list_instances will report once the user opens web_url and the pane connects — " +
	"call list_instances after they say they've opened it to find the right bridge before driving the document. " +
	"Requires the user to have granted Microsoft file access at sign-in; if they haven't, this returns graph-not-connected."

// docKeyFor builds the personal-OneDrive URL shape the RFC's live spike
// (§3.3) found the web pane reports as Office.context.document.url:
// https://d.docs.live.net/<CID>/<folder path>/<filename>, with the folder
// segment entirely absent for a root-level file. Live finding 2026-09-08
// (a follow-up to the RFC's original spike, which tested a root file and so
// looked folderless): create_workbook always uploads into "Eichler
// Connectors", so the folder segment is always present in practice, but
// this stays general rather than hard-coding that folder name. doc_key is
// constructed to equal the pane's URL exactly, string for string (literal
// spaces, matching what the pane reports — see graph.DriveItem.Folder),
// so the agent's list_instances match is exact, not a heuristic.
func docKeyFor(item hub.DriveItem) string {
	segs := []string{item.DriveID}
	if item.Folder != "" {
		segs = append(segs, item.Folder)
	}
	segs = append(segs, item.Name)
	return "https://d.docs.live.net/" + strings.Join(segs, "/")
}

func registerCreateWorkbook(reg *hub.ToolRegistry, c *Connector) {
	mcp.AddTool(reg.Server, &mcp.Tool{Name: "create_workbook", Description: createWorkbookDescription},
		func(ctx context.Context, req *mcp.CallToolRequest, in CreateWorkbookIn) (*mcp.CallToolResult, CreateWorkbookOut, error) {
			user, rec := reg.Host.User(req)
			if rec != nil {
				return fail(rec), CreateWorkbookOut{Error: rec}, nil
			}
			content, err := buildWorkbook(in.Data)
			if err != nil {
				rec := diag.New(diag.SeverityError, "invalid-data", Source, err.Error())
				return fail(rec), CreateWorkbookOut{Error: rec}, nil
			}
			name, err := uniqueWorkbookName(in.Name)
			if err != nil {
				rec := diag.New(diag.SeverityError, "internal", Source, "could not generate a unique filename: "+err.Error())
				return fail(rec), CreateWorkbookOut{Error: rec}, nil
			}
			// A blank workbook is one empty sheet (xlsxgen.Blank); from-data is
			// one sheet per entry. Passed through only for the audit row.
			sheets := len(in.Data)
			if sheets == 0 {
				sheets = 1
			}
			item, rec := reg.Host.CreateWorkbookFile(ctx, user, c, name, content, sheets)
			if rec != nil {
				return fail(rec), CreateWorkbookOut{Error: rec}, nil
			}
			docKey := docKeyFor(item)
			// The correlation spike's headline check (RFC §3.3, §6): log the
			// constructed doc_key next to what hub.Host already logged from
			// the driveItem, so a live test can compare them against what
			// the pane actually registers even if the construction is off.
			reg.Host.Log().Info("create_workbook: doc_key constructed", "user", user, "doc_key", docKey, "web_url", item.WebURL)
			out := CreateWorkbookOut{
				WebURL:   item.WebURL,
				DocKey:   docKey,
				OpenHint: openHint(item.WebURL),
			}
			text := fmt.Sprintf("Created %s: %s\n%s", item.Name, item.WebURL, out.OpenHint)
			content2 := []mcp.Content{
				&mcp.TextContent{Text: text},
				&mcp.ResourceLink{URI: item.WebURL, Name: item.Name, MIMEType: exportMIMEType("xlsx")},
			}
			return &mcp.CallToolResult{Content: content2, StructuredContent: out}, out, nil
		})
}

// openHint is the human text create_workbook returns alongside web_url.
// Pre-distribution (no auto-open manifest yet — RFC §3.4) the user must
// sideload the connector and open its pane manually after clicking the
// link; this says so rather than implying it "just works".
func openHint(webURL string) string {
	return "Open this to let me drive it: " + webURL +
		". Until the connector auto-opens (coming with distribution), also open the MCP Bridge pane from the Home tab, then tell me and I'll call list_instances."
}

// buildWorkbook turns create_workbook's sheets into .xlsx bytes: from-data
// when sheets is non-empty, blank otherwise (RFC §3.2).
func buildWorkbook(sheets []CreateWorkbookSheet) ([]byte, error) {
	if len(sheets) == 0 {
		return xlsxgen.Blank()
	}
	out := make([]xlsxgen.Sheet, len(sheets))
	for i, s := range sheets {
		out[i] = xlsxgen.Sheet{Name: s.Name, Rows: s.Rows}
	}
	return xlsxgen.Build(out)
}

// uniqueWorkbookName builds "<hint>-<token>.xlsx": the RFC §3.3 spike found
// the pane's doc_key carries the bare filename with no folder, so two
// same-named files in different folders would be indistinguishable to it —
// the short random token sidesteps that by making every created file's name
// unique regardless of where it lands.
func uniqueWorkbookName(hint string) (string, error) {
	name := strings.TrimSpace(hint)
	name = strings.TrimSuffix(name, ".xlsx")
	if name == "" {
		name = "Workbook"
	}
	b := make([]byte, 4)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return fmt.Sprintf("%s-%s.xlsx", name, hex.EncodeToString(b)), nil
}
