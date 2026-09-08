package connector

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub"
	"github.com/eichler-ai/connectors/hub/diag"
	"github.com/eichler-ai/connectors/hub/protocol"
)

// Expect names where the caller believes the script will run. Checked against
// the active workbook and sheet before the script body executes; a mismatch
// fails fast with `expect-mismatch` and runs nothing (§10 safety).
type Expect struct {
	Workbook string `json:"workbook,omitempty" jsonschema:"expected workbook name (file name as Excel shows it)"`
	Sheet    string `json:"sheet,omitempty" jsonschema:"expected active sheet name"`
}

// ExecuteScriptIn is the input schema for execute_script.
type ExecuteScriptIn struct {
	Script     string  `json:"script" jsonschema:"body of an async function whose one parameter is context (the Excel.RequestContext); whatever it returns comes back as result"`
	InstanceID string  `json:"instance_id,omitempty" jsonschema:"which bridge to run in, from list_instances; required only when more than one is connected"`
	TimeoutMs  int64   `json:"timeout_ms,omitempty" jsonschema:"cooperative deadline in milliseconds; default 30000, maximum 600000"`
	Expect     *Expect `json:"expect,omitempty" jsonschema:"fail fast unless the active workbook/sheet match"`
}

// Target is the workbook and sheet a script started on.
type Target struct {
	Workbook string `json:"workbook"`
	Sheet    string `json:"sheet"`
}

// ExecuteScriptOut is the output schema for execute_script.
type ExecuteScriptOut struct {
	Status string `json:"status" jsonschema:"ok or error"`
	// Result is the script's return value as JSON. When Truncated is set it
	// is instead {truncated:true, bytes, head} from the bridge.
	Result     any           `json:"result,omitempty"`
	Target     *Target       `json:"target,omitempty" jsonschema:"the active workbook and sheet when the script started"`
	InstanceID string        `json:"instance_id,omitempty"`
	DurationMs float64       `json:"duration_ms,omitempty"`
	Truncated  bool          `json:"truncated,omitempty"`
	Notices    []diag.Record `json:"notices,omitempty"`
	Error      *diag.Record  `json:"error,omitempty"`
}

const executeScriptDescription = "Run Office.js in the connected Excel workbook. `script` is the body of an async function with one parameter, " +
	"`context` (the Excel.RequestContext from Excel.run); return plain values, not proxy objects, and they come back as `result`. " +
	"Scripts run on the ACTIVE sheet unless they name one via context.workbook.worksheets.getItem(name); pass `expect` to fail fast if the active " +
	"workbook/sheet is not the one you mean. Call get_skills first for the contract, limits and known host quirks."

func registerExecuteScript(reg *hub.ToolRegistry, c *Connector) {
	mcp.AddTool(reg.Server, &mcp.Tool{Name: "execute_script", Description: executeScriptDescription},
		func(ctx context.Context, req *mcp.CallToolRequest, in ExecuteScriptIn) (*mcp.CallToolResult, ExecuteScriptOut, error) {
			user, rec := reg.Host.User(req)
			if rec != nil {
				return fail(rec), ExecuteScriptOut{Status: "error", Error: rec}, nil
			}
			expect := Expect{}
			if in.Expect != nil {
				expect = *in.Expect
			}
			res, rec := reg.Host.Exec(ctx, user, c,
				hub.Target{InstanceID: in.InstanceID},
				hub.Script{Language: Language, Source: wrap(in.Script, expect), Timeout: time.Duration(in.TimeoutMs) * time.Millisecond})
			if rec != nil {
				return fail(rec), ExecuteScriptOut{Status: "error", Error: rec}, nil
			}
			reply := res.Reply
			out := ExecuteScriptOut{InstanceID: res.Instance.InstanceID, DurationMs: reply.DurationMs, Truncated: reply.Truncated, Notices: reply.Notices}
			if !reply.OK {
				out.Status = "error"
				out.Error = scriptError(reply.Error)
				return fail(out.Error), out, nil
			}
			out.Status = "ok"
			out.Target, out.Result = unwrap(reply.Result, reply.Truncated)
			if targetImplicit(in.Script, expect.Sheet) {
				n := diag.New(diag.SeverityInfo, "target-implicit", Source,
					fmt.Sprintf("the script named no sheet, so it ran on the active one%s", describeTarget(out.Target))).
					WithRemedy("address a sheet explicitly with context.workbook.worksheets.getItem(\"Name\"), or pass expect.sheet")
				out.Notices = append(out.Notices, *n)
			}
			return nil, out, nil
		})
}

func describeTarget(t *Target) string {
	if t == nil {
		return ""
	}
	return fmt.Sprintf(" (%q in %q)", t.Sheet, t.Workbook)
}

func fail(rec *diag.Record) *mcp.CallToolResult { return hub.ErrorResult(rec) }

// wrap builds the script the bridge runs: a one-line prelude that records the
// active workbook/sheet, enforces expect, then the caller's body inside its
// own async function so its `return` still works, and finally an envelope
// {__hub, value} the hub unwraps. One line so the caller's line numbers in
// Office.js debug_info shift by a constant the skill file can state.
func wrap(script string, expect Expect) string {
	exp, _ := json.Marshal(expect)
	return `const __wb = context.workbook; __wb.load("name"); const __ws = __wb.worksheets.getActiveWorksheet(); __ws.load("name"); await context.sync(); const __hub = { workbook: __wb.name, sheet: __ws.name }; const __x = ` + string(exp) + `; ` +
		`for (const k of ["workbook", "sheet"]) { if (__x[k] && __x[k] !== __hub[k]) { const e = new Error("expected " + k + " " + JSON.stringify(__x[k]) + " but the active " + k + " is " + JSON.stringify(__hub[k])); e.name = "ExpectMismatch"; e.code = "expect-mismatch"; e.debugInfo = { expected: __x, actual: __hub }; throw e; } }
const __value = await (async () => {
` + script + `
})();
return { __hub: __hub, value: __value };`
}

// envelope is what wrap's script returns.
type envelope struct {
	Hub   *Target         `json:"__hub"`
	Value json.RawMessage `json:"value"`
}

// unwrap separates the prelude's target from the script's value. A truncated
// result is the bridge's {truncated, bytes, head} stand-in and has no
// envelope; it is returned as is.
func unwrap(raw json.RawMessage, truncated bool) (*Target, any) {
	var env envelope
	if truncated || json.Unmarshal(raw, &env) != nil || env.Hub == nil {
		return nil, decode(raw)
	}
	return env.Hub, decode(env.Value)
}

func decode(raw json.RawMessage) any {
	if len(raw) == 0 {
		return nil
	}
	var v any
	if err := json.Unmarshal(raw, &v); err != nil {
		// Not valid JSON is a bridge bug; surface the text rather than nothing.
		return string(raw)
	}
	return v
}

// scriptError turns the bridge's error into a diagnostic record. Office.js
// errors keep their code (`InvalidArgument`, `ItemNotFound`, …) as the
// record code so an agent can branch on them; expect-mismatch is its own
// code from the prelude.
func scriptError(e *protocol.ScriptError) *diag.Record {
	if e == nil {
		return diag.New(diag.SeverityError, "script-error", Source, "the script failed without an error object")
	}
	code := e.Code
	if code == "" {
		code = e.Name
	}
	if code == "" {
		code = "script-error"
	}
	detail := map[string]any{"name": e.Name}
	if len(e.DebugInfo) > 0 {
		detail["debug_info"] = decode(e.DebugInfo)
	}
	if e.Stack != "" {
		detail["stack"] = e.Stack
	}
	rec := diag.New(diag.SeverityError, code, Source, e.Message).WithDetail(detail)
	if code == "expect-mismatch" {
		rec.Remedy = []string{"call get_status to see the active workbook and sheet", "activate the intended sheet or drop the expect parameter if the active one is right"}
	}
	return rec
}
