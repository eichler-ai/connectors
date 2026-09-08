package connector

import (
	"context"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub"
	"github.com/eichler-ai/connectors/hub/diag"
	"github.com/eichler-ai/connectors/hub/protocol"
)

// ImportOptions is import_workbook's friendly shape for Office.js's
// InsertWorksheetOptions (protocol.ImportOptions carries it over the wire).
// Position defaults to "end" (after the last existing sheet, §10 reversed);
// "before"/"after" require RelativeToSheet, which the pane resolves to a
// worksheet itself.
type ImportOptions struct {
	SheetNames      []string `json:"sheet_names,omitempty" jsonschema:"which sheets of the source file to insert; omit for every sheet"`
	Position        string   `json:"position,omitempty" jsonschema:"start, end (default), before or after; before/after require relative_to_sheet"`
	RelativeToSheet string   `json:"relative_to_sheet,omitempty" jsonschema:"an existing sheet name the position is relative to"`
}

var importPositions = map[string]string{
	"":       "End",
	"start":  "Beginning",
	"end":    "End",
	"before": "Before",
	"after":  "After",
}

func (o ImportOptions) toProtocol() (protocol.ImportOptions, *diag.Record) {
	positionType, ok := importPositions[o.Position]
	if !ok {
		return protocol.ImportOptions{}, diag.New(diag.SeverityError, "invalid-position", Source,
			"position must be start, end, before or after, not \""+o.Position+"\"")
	}
	if (positionType == "Before" || positionType == "After") && o.RelativeToSheet == "" {
		return protocol.ImportOptions{}, diag.New(diag.SeverityError, "invalid-position", Source,
			"position \""+o.Position+"\" requires relative_to_sheet")
	}
	return protocol.ImportOptions{SheetNamesToInsert: o.SheetNames, PositionType: positionType, RelativeToSheet: o.RelativeToSheet}, nil
}

// ImportWorkbookIn is import_workbook's input: exactly one source, plus the
// same expect.workbook safety §10 gives execute_script — this WRITES to the
// open document and there is no undo.
type ImportWorkbookIn struct {
	ContentBase64 string         `json:"content_base64,omitempty" jsonschema:"the .xlsx file, base64-encoded; decoded must be under 10 MiB. Exactly one of content_base64 or source_url"`
	SourceURL     string         `json:"source_url,omitempty" jsonschema:"an https URL the hub fetches server-side (under 10 MiB); private/loopback addresses are refused. Exactly one of content_base64 or source_url"`
	InstanceID    string         `json:"instance_id,omitempty" jsonschema:"which bridge to import into, from list_instances; required only when more than one is connected"`
	Options       *ImportOptions `json:"options,omitempty" jsonschema:"which sheets to insert and where; default is every sheet, placed after the last existing one"`
	Expect        *ImportExpect  `json:"expect,omitempty" jsonschema:"fail fast unless the active workbook matches; this writes to the open document and there is no undo"`
}

// ImportExpect is execute_script's Expect narrowed to workbook: import_workbook
// has no notion of "the sheet it ran on" to check against, since it adds
// sheets rather than acting on one.
type ImportExpect struct {
	Workbook string `json:"workbook,omitempty" jsonschema:"expected workbook name (file name as Excel shows it)"`
}

// ImportWorkbookOut is what a completed import looks like.
type ImportWorkbookOut struct {
	Status      string       `json:"status" jsonschema:"ok or error"`
	AddedSheets []string     `json:"added_sheets,omitempty" jsonschema:"the actual resulting names of the inserted sheets — Office renames on a collision"`
	AllSheets   []string     `json:"all_sheets,omitempty"`
	Workbook    string       `json:"workbook,omitempty"`
	InstanceID  string       `json:"instance_id,omitempty"`
	Bytes       int64        `json:"bytes,omitempty"`
	Error       *diag.Record `json:"error,omitempty"`
}

const importWorkbookDescription = "Copy the sheets of an .xlsx into the connected workbook (Office.js insertWorksheetsFromBase64). " +
	"Pass the file as content_base64 (small files) or source_url (an https URL the hub fetches). This WRITES to the open workbook " +
	"and there is no undo; pass expect.workbook to fail fast if the active workbook is not the one you mean. Office renames the " +
	"incoming sheet on a name collision — added_sheets reports the actual resulting names."

func registerImportWorkbook(reg *hub.ToolRegistry, c *Connector) {
	mcp.AddTool(reg.Server, &mcp.Tool{Name: "import_workbook", Description: importWorkbookDescription},
		func(ctx context.Context, req *mcp.CallToolRequest, in ImportWorkbookIn) (*mcp.CallToolResult, ImportWorkbookOut, error) {
			user, rec := reg.Host.User(req)
			if rec != nil {
				return fail(rec), ImportWorkbookOut{Status: "error", Error: rec}, nil
			}
			opts := ImportOptions{}
			if in.Options != nil {
				opts = *in.Options
			}
			protoOpts, rec := opts.toProtocol()
			if rec != nil {
				return fail(rec), ImportWorkbookOut{Status: "error", Error: rec}, nil
			}
			expectWorkbook := ""
			if in.Expect != nil {
				expectWorkbook = in.Expect.Workbook
			}
			target := hub.Target{InstanceID: in.InstanceID}
			if expectWorkbook != "" {
				if rec := checkExpectedWorkbook(ctx, reg, c, user, target, expectWorkbook); rec != nil {
					return fail(rec), ImportWorkbookOut{Status: "error", Error: rec}, nil
				}
			}
			res, rec := reg.Host.Import(ctx, user, c, target, hub.ImportRequest{
				Source:  hub.ImportSource{ContentBase64: in.ContentBase64, SourceURL: in.SourceURL},
				Options: protoOpts,
			})
			if rec != nil {
				return fail(rec), ImportWorkbookOut{Status: "error", Error: rec}, nil
			}
			out := ImportWorkbookOut{
				Status: "ok", AddedSheets: res.AddedSheets, AllSheets: res.AllSheets,
				Workbook: res.Document.Title, InstanceID: res.Instance.InstanceID, Bytes: res.Bytes,
			}
			return nil, out, nil
		})
}

// checkExpectedWorkbook fails fast on a workbook mismatch before any bytes
// are fetched, stored or sent to the pane (§10 safety: import_workbook writes
// to the open document and has no undo, so the check belongs before the
// expensive and irreversible parts, not after). It reuses execute_script's
// own expect prelude via a no-op script rather than duplicating the
// workbook-name lookup, which get_status also already does.
func checkExpectedWorkbook(ctx context.Context, reg *hub.ToolRegistry, c *Connector, user string, target hub.Target, workbook string) *diag.Record {
	res, rec := reg.Host.Exec(ctx, user, c, target, hub.Script{Language: Language, Source: wrap("return null;", Expect{Workbook: workbook})})
	if rec != nil {
		return rec
	}
	if !res.Reply.OK {
		return scriptError(res.Reply.Error)
	}
	return nil
}
