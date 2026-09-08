package connector

import (
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net/http/httptest"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub"
	"github.com/eichler-ai/connectors/hub/bridgetest"
	"github.com/eichler-ai/connectors/hub/diag"
	"github.com/eichler-ai/connectors/hub/protocol"
	"github.com/eichler-ai/connectors/internal/auth"
)

const token = "excel-test-token-0123456789"

type fixture struct {
	srv  *hub.Server
	http *httptest.Server
	cs   *mcp.ClientSession
	uid  string
}

func newFixture(t *testing.T) *fixture {
	t.Helper()
	a, err := auth.NewDevToken(token)
	if err != nil {
		t.Fatal(err)
	}
	uid := a.UserID()
	srv, err := hub.NewServer(hub.Options{
		Auth:       a,
		Connectors: []hub.Connector{New()},
		Logger:     slog.New(slog.NewTextHandler(io.Discard, nil)),
		// In-memory MCP sessions have no bearer token; act as the dev user, the
		// identity the fake bridge's hello resolves to.
		UserOf: func(*mcp.CallToolRequest) (string, bool) { return uid, true },
	})
	if err != nil {
		t.Fatal(err)
	}
	hs := httptest.NewServer(srv.Handler())
	t.Cleanup(hs.Close)

	ct, st := mcp.NewInMemoryTransports()
	ctx := context.Background()
	if _, err := srv.MCPServer(Slug).Connect(ctx, st, nil); err != nil {
		t.Fatal(err)
	}
	cs, err := mcp.NewClient(&mcp.Implementation{Name: "test", Version: "0"}, nil).Connect(ctx, ct, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { cs.Close() })
	return &fixture{srv: srv, http: hs, cs: cs, uid: uid}
}

// miniExcel is a fake bridge that behaves like the prelude wrap() injects:
// it reads the expect JSON out of the script, compares it with its own
// workbook/sheet, and either throws expect-mismatch or returns the envelope
// around Value. The JavaScript prelude itself is only testable live; this
// covers everything on the hub side of it.
type miniExcel struct {
	workbook, sheet string
	value           json.RawMessage
	err             *protocol.ScriptError
	truncated       bool
}

var expectRe = regexp.MustCompile(`const __x = (\{[^;]*\});`)

func (m *miniExcel) handle(ex protocol.Exec) protocol.Result {
	if strings.Contains(ex.Script, `excel_api`) { // statusScript
		return protocol.Result{ID: ex.ID, OK: true, Result: json.RawMessage(`{"workbook":"` + m.workbook + `","sheet":"` + m.sheet + `","sheets":["` + m.sheet + `","Other"],"selection":"A1","excel_api":"1.20"}`)}
	}
	match := expectRe.FindStringSubmatch(ex.Script)
	if match == nil {
		return protocol.Result{ID: ex.ID, OK: false, Error: &protocol.ScriptError{Name: "TestError", Message: "no prelude in script"}}
	}
	var x Expect
	_ = json.Unmarshal([]byte(match[1]), &x)
	if (x.Workbook != "" && x.Workbook != m.workbook) || (x.Sheet != "" && x.Sheet != m.sheet) {
		return protocol.Result{ID: ex.ID, OK: false, Error: &protocol.ScriptError{Name: "ExpectMismatch", Code: "expect-mismatch", Message: "expected sheet " + x.Sheet + " but the active sheet is " + m.sheet,
			DebugInfo: json.RawMessage(`{"expected":` + match[1] + `,"actual":{"workbook":"` + m.workbook + `","sheet":"` + m.sheet + `"}}`)}}
	}
	if m.err != nil {
		return protocol.Result{ID: ex.ID, OK: false, Error: m.err}
	}
	if m.truncated {
		return protocol.Result{ID: ex.ID, OK: true, Truncated: true, Result: json.RawMessage(`{"truncated":true,"bytes":99,"head":"["}`)}
	}
	return protocol.Result{ID: ex.ID, OK: true, DurationMs: 7, Result: json.RawMessage(`{"__hub":{"workbook":"` + m.workbook + `","sheet":"` + m.sheet + `"},"value":` + string(m.value) + `}`)}
}

func (f *fixture) connect(t *testing.T, m *miniExcel) *bridgetest.Fake {
	t.Helper()
	fake := bridgetest.Dial(t, bridgetest.WSURL(f.http.URL, "/excel/bridge"), bridgetest.Options{Token: token, InstanceID: "pane-1",
		Documents: []protocol.Document{{ID: "https://onedrive/x.xlsx", Title: m.workbook, Active: true, Detail: map[string]any{"sheet": m.sheet}}}})
	fake.Handle = m.handle
	deadline := time.Now().Add(5 * time.Second)
	for len(f.srv.Host().Instances(f.uid, Slug)) == 0 {
		if time.Now().After(deadline) {
			t.Fatal("bridge never registered")
		}
		time.Sleep(5 * time.Millisecond)
	}
	return fake
}

func (f *fixture) call(t *testing.T, name string, args map[string]any) (*mcp.CallToolResult, ExecuteScriptOut) {
	t.Helper()
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: name, Arguments: args})
	if err != nil {
		t.Fatalf("%s: %v", name, err)
	}
	var out ExecuteScriptOut
	b, _ := json.Marshal(res.StructuredContent)
	if err := json.Unmarshal(b, &out); err != nil {
		t.Fatalf("%s structured output: %v (%s)", name, err, b)
	}
	return res, out
}

func TestExecuteScriptRoundTrip(t *testing.T) {
	f := newFixture(t)
	f.connect(t, &miniExcel{workbook: "Book1.xlsx", sheet: "Data", value: json.RawMessage(`{"rows":3}`)})

	res, out := f.call(t, "execute_script", map[string]any{"script": `const r = context.workbook.worksheets.getItem("Data").getRange("A1"); return {rows: 3};`})
	if res.IsError || out.Status != "ok" || out.InstanceID != "pane-1" || out.DurationMs != 7 {
		t.Fatalf("result: %+v", out)
	}
	if v, _ := out.Result.(map[string]any); v["rows"] != float64(3) {
		t.Fatalf("value: %#v", out.Result)
	}
	if out.Target == nil || out.Target.Workbook != "Book1.xlsx" || out.Target.Sheet != "Data" {
		t.Fatalf("target: %+v", out.Target)
	}
	if len(out.Notices) != 0 {
		t.Fatalf("a script that names its sheet got notices: %+v", out.Notices)
	}
	// The text content is the structured output serialised, for text-only clients.
	if txt := res.Content[0].(*mcp.TextContent).Text; !strings.Contains(txt, `"rows":3`) {
		t.Fatalf("text content: %s", txt)
	}
}

func TestTargetImplicitNotice(t *testing.T) {
	f := newFixture(t)
	f.connect(t, &miniExcel{workbook: "Book1.xlsx", sheet: "Sheet1", value: json.RawMessage(`null`)})
	_, out := f.call(t, "execute_script", map[string]any{"script": `context.workbook.getActiveWorksheet().getRange("A1").values = [[1]]; await context.sync();`})
	if len(out.Notices) != 1 || out.Notices[0].Code != "target-implicit" || out.Notices[0].Severity != diag.SeverityInfo || !strings.Contains(out.Notices[0].Message, `"Sheet1"`) {
		t.Fatalf("notices: %+v", out.Notices)
	}
	// expect.sheet counts as naming the target.
	_, out = f.call(t, "execute_script", map[string]any{"script": `context.workbook.getActiveWorksheet().getRange("A1").values = [[1]];`, "expect": map[string]any{"sheet": "Sheet1"}})
	if len(out.Notices) != 0 {
		t.Fatalf("expect.sheet still produced notices: %+v", out.Notices)
	}
}

func TestExpectMismatchFailsFast(t *testing.T) {
	f := newFixture(t)
	f.connect(t, &miniExcel{workbook: "Book1.xlsx", sheet: "Sheet1", value: json.RawMessage(`1`)})
	res, out := f.call(t, "execute_script", map[string]any{"script": "return 1", "expect": map[string]any{"workbook": "Book1.xlsx", "sheet": "Data"}})
	if !res.IsError || out.Status != "error" || out.Error == nil || out.Error.Code != "expect-mismatch" {
		t.Fatalf("mismatch: isError=%v %+v", res.IsError, out)
	}
	if len(out.Error.Remedy) == 0 || out.Error.Detail["debug_info"] == nil {
		t.Fatalf("mismatch record lacks remedy/detail: %+v", out.Error)
	}
	res, out = f.call(t, "execute_script", map[string]any{"script": "return 1", "expect": map[string]any{"workbook": "Book1.xlsx", "sheet": "Sheet1"}})
	if res.IsError || out.Status != "ok" {
		t.Fatalf("matching expect failed: %+v", out)
	}
}

func TestScriptErrorVerbatim(t *testing.T) {
	f := newFixture(t)
	f.connect(t, &miniExcel{workbook: "B", sheet: "S", err: &protocol.ScriptError{Name: "RichApi.Error", Code: "InvalidArgument", Message: "The argument is invalid or missing or has an incorrect format.",
		DebugInfo: json.RawMessage(`{"code":"InvalidArgument","errorLocation":"Range.values","statement":"var v = ...","surroundingStatements":["a","b"]}`), Stack: "RichApi.Error: ..."}})
	res, out := f.call(t, "execute_script", map[string]any{"script": "boom"})
	if !res.IsError || out.Error == nil || out.Error.Code != "InvalidArgument" || out.Error.Source != Source {
		t.Fatalf("error: %+v", out.Error)
	}
	di, _ := out.Error.Detail["debug_info"].(map[string]any)
	if di["errorLocation"] != "Range.values" || out.Error.Detail["stack"] != "RichApi.Error: ..." || out.Error.Detail["name"] != "RichApi.Error" {
		t.Fatalf("detail not verbatim: %+v", out.Error.Detail)
	}
}

func TestNoBridge(t *testing.T) {
	f := newFixture(t)
	res, out := f.call(t, "execute_script", map[string]any{"script": "return 1"})
	if !res.IsError || out.Error == nil || out.Error.Code != "no-bridge" {
		t.Fatalf("no bridge: %+v", out)
	}
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "get_status", Arguments: map[string]any{}})
	if err != nil || !res.IsError {
		t.Fatalf("get_status without a bridge: %v %+v", err, res)
	}
}

func TestTruncatedResultPassesThrough(t *testing.T) {
	f := newFixture(t)
	f.connect(t, &miniExcel{workbook: "B", sheet: "S", truncated: true})
	res, out := f.call(t, "execute_script", map[string]any{"script": "return big"})
	if res.IsError || !out.Truncated || out.Target != nil {
		t.Fatalf("truncated: %+v", out)
	}
	if v, _ := out.Result.(map[string]any); v["truncated"] != true || v["bytes"] != float64(99) {
		t.Fatalf("truncated stand-in: %#v", out.Result)
	}
}

func TestGetStatus(t *testing.T) {
	f := newFixture(t)
	f.connect(t, &miniExcel{workbook: "Budget.xlsx", sheet: "Q3"})
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "get_status", Arguments: map[string]any{}})
	if err != nil || res.IsError {
		t.Fatalf("get_status: %v %+v", err, res)
	}
	var out GetStatusOut
	b, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(b, &out)
	if out.Workbook != "Budget.xlsx" || out.Sheet != "Q3" || len(out.Sheets) != 2 || out.Selection != "A1" || out.ExcelAPI != "1.20" || out.InstanceID != "pane-1" || out.Host.App != "Excel" {
		t.Fatalf("status: %+v", out)
	}
}

func TestListInstancesShowsSheet(t *testing.T) {
	f := newFixture(t)
	f.connect(t, &miniExcel{workbook: "Budget.xlsx", sheet: "Q3"})
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "list_instances", Arguments: map[string]any{}})
	if err != nil || res.IsError {
		t.Fatal(err)
	}
	var out hub.ListInstancesOut
	b, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(b, &out)
	if len(out.Instances) != 1 || out.Instances[0].Documents[0].Title != "Budget.xlsx" || out.Instances[0].Documents[0].Detail["sheet"] != "Q3" {
		t.Fatalf("instances: %s", b)
	}
}

func TestToolSurface(t *testing.T) {
	f := newFixture(t)
	tools, err := f.cs.ListTools(context.Background(), nil)
	if err != nil {
		t.Fatal(err)
	}
	var names []string
	for _, tl := range tools.Tools {
		names = append(names, tl.Name)
	}
	if got := strings.Join(names, ","); got != "execute_script,get_skills,get_status,list_instances" {
		t.Fatalf("tools: %s", got)
	}
}

func TestValidate(t *testing.T) {
	c := New()
	if err := c.Validate(context.Background(), hub.Script{Source: "  \n"}); err == nil {
		t.Fatal("empty script accepted")
	}
	if err := c.Validate(context.Background(), hub.Script{Source: strings.Repeat("x", maxScriptBytes+1)}); err == nil {
		t.Fatal("oversize script accepted")
	}
	if err := c.Validate(context.Background(), hub.Script{Source: "return 1"}); err != nil {
		t.Fatal(err)
	}
}

func TestTargetImplicitHeuristic(t *testing.T) {
	cases := []struct {
		script, expectSheet string
		want                bool
	}{
		{`context.workbook.getActiveWorksheet().getRange("A1").values = [[1]]`, "", true},
		{`context.workbook.getSelectedRange().load("address")`, "", true},
		{`context.workbook.worksheets.getItem("Data").getRange("A1").values = [[1]]`, "", false},
		{`const ws = context.workbook.worksheets.getItemOrNullObject("X"); ws.getRange("A1")`, "", false},
		{`const ws = context.workbook.worksheets.add("New"); ws.getRange("A1")`, "", false},
		{`context.workbook.worksheets.getFirst().getUsedRange()`, "", false},
		{`context.workbook.getActiveWorksheet().getRange("A1")`, "Sheet1", false},
		{`const names = context.workbook.names; names.load("items"); await context.sync(); return names.items.length`, "", false},
		{`return 1`, "", false},
	}
	for _, tc := range cases {
		if got := targetImplicit(tc.script, tc.expectSheet); got != tc.want {
			t.Errorf("targetImplicit(%q, %q) = %v, want %v", tc.script, tc.expectSheet, got, tc.want)
		}
	}
}

func TestWrapAndUnwrap(t *testing.T) {
	s := wrap("return 42;", Expect{Sheet: "Data"})
	if !strings.Contains(s, `const __x = {"sheet":"Data"};`) || !strings.Contains(s, "\nreturn 42;\n") || !strings.HasSuffix(s, "return { __hub: __hub, value: __value };") {
		t.Fatalf("wrap: %s", s)
	}
	if lines := strings.Count(strings.SplitN(s, "return 42;", 2)[0], "\n"); lines != 2 {
		t.Fatalf("prelude is %d lines, want 2 (the skill file's line-offset statement)", lines)
	}
	target, v := unwrap(json.RawMessage(`{"__hub":{"workbook":"W","sheet":"S"},"value":[1,2]}`), false)
	if target == nil || target.Sheet != "S" || len(v.([]any)) != 2 {
		t.Fatalf("unwrap: %+v %#v", target, v)
	}
	if target, v := unwrap(json.RawMessage(`{"other":1}`), false); target != nil || v.(map[string]any)["other"] != float64(1) {
		t.Fatalf("unwrap without envelope: %+v %#v", target, v)
	}
	if target, v := unwrap(nil, false); target != nil || v != nil {
		t.Fatalf("unwrap empty: %+v %#v", target, v)
	}
}
