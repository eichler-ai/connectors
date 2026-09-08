package connector

import (
	"context"
	"encoding/json"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub"
	"github.com/eichler-ai/connectors/hub/diag"
	"github.com/eichler-ai/connectors/hub/protocol"
)

// GetStatusIn is the input schema for get_status.
type GetStatusIn struct {
	InstanceID string `json:"instance_id,omitempty" jsonschema:"which bridge to ask; required only when more than one is connected"`
}

// GetStatusOut is what the workbook looks like right now.
type GetStatusOut struct {
	InstanceID string        `json:"instance_id,omitempty"`
	Host       protocol.Host `json:"host"`
	Workbook   string        `json:"workbook,omitempty"`
	// Sheet is the active sheet; Sheets lists every sheet in order.
	Sheet  string   `json:"sheet,omitempty"`
	Sheets []string `json:"sheets,omitempty"`
	// Selection is the selected range's address, or empty when the selection
	// is not a range (a chart, for example).
	Selection string `json:"selection,omitempty"`
	// ExcelAPI is the highest ExcelApi requirement set the host supports.
	ExcelAPI string       `json:"excel_api,omitempty"`
	Error    *diag.Record `json:"error,omitempty"`
}

// statusScript is run through the ordinary exec path, so get_status is also
// the cheapest end-to-end check that the bridge is alive.
const statusScript = `const wb = context.workbook; wb.load("name");
const ws = wb.worksheets.getActiveWorksheet(); ws.load("name");
const sheets = wb.worksheets; sheets.load("items/name");
await context.sync();
let selection = "";
try { const sel = wb.getSelectedRange(); sel.load("address"); await context.sync(); selection = sel.address; } catch (e) { selection = ""; }
let api = "";
const req = (typeof Office !== "undefined" && Office.context && Office.context.requirements) || null;
if (req) { for (let minor = 1; minor <= 30; minor++) { if (req.isSetSupported("ExcelApi", "1." + minor)) api = "1." + minor; } }
return { workbook: wb.name, sheet: ws.name, sheets: sheets.items.map(s => s.name), selection: selection, excel_api: api };`

func registerGetStatus(reg *hub.ToolRegistry, c *Connector) {
	mcp.AddTool(reg.Server, &mcp.Tool{
		Name:        "get_status",
		Description: "Report the connected workbook: its name, active sheet, all sheet names, the current selection, host and Office.js API level. Use it before a write to confirm where a script will land.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in GetStatusIn) (*mcp.CallToolResult, GetStatusOut, error) {
		user, rec := reg.Host.User(req)
		if rec != nil {
			return fail(rec), GetStatusOut{Error: rec}, nil
		}
		res, rec := reg.Host.Exec(ctx, user, c, hub.Target{InstanceID: in.InstanceID, Client: hub.ClientName(req)}, hub.Script{Language: Language, Source: statusScript})
		if rec != nil {
			return fail(rec), GetStatusOut{Error: rec}, nil
		}
		out := GetStatusOut{InstanceID: res.Instance.InstanceID, Host: res.Instance.Host}
		if !res.Reply.OK {
			out.Error = scriptError(res.Reply.Error)
			return fail(out.Error), out, nil
		}
		if err := json.Unmarshal(res.Reply.Result, &out); err != nil {
			out.Error = diag.New(diag.SeverityError, "bad-status", Source, "the bridge's status reply was not the expected shape: "+err.Error())
			return fail(out.Error), out, nil
		}
		return nil, out, nil
	})
}
