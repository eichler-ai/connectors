package connector

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"os"
	"regexp"
	"strings"
	"sync"
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
	srv   *hub.Server
	http  *httptest.Server
	cs    *mcp.ClientSession
	uid   string
	files *fakeFiles
}

// fakeFiles is this package's stand-in for hub/internal/files.Temp, which
// excel/connector cannot import (Go's internal rule: only packages under
// hub/ may). It is the same shape — bytes in memory, signed URLs served by
// its own httptest.Server — just local to this test package, and it is what
// exercises "the tool returns a working signed URL" for export_file without
// reaching into the hub's internal store.
type fakeFiles struct {
	mu      sync.Mutex
	objects map[string][]byte
	srv     *httptest.Server
}

func newFakeFiles() *fakeFiles {
	f := &fakeFiles{objects: map[string][]byte{}}
	f.srv = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		f.mu.Lock()
		b, ok := f.objects[strings.TrimPrefix(r.URL.Path, "/")]
		f.mu.Unlock()
		if !ok {
			http.NotFound(w, r)
			return
		}
		_, _ = w.Write(b)
	}))
	return f
}

func (f *fakeFiles) Put(_ context.Context, user, connector, id, ext string, r io.Reader) (hub.ObjectRef, error) {
	b, err := io.ReadAll(r)
	if err != nil {
		return hub.ObjectRef{}, err
	}
	key := fmt.Sprintf("%s/%s/%s.%s", user, connector, id, ext)
	f.mu.Lock()
	f.objects[key] = b
	f.mu.Unlock()
	return hub.ObjectRef{Key: key, Bytes: int64(len(b))}, nil
}

func (f *fakeFiles) SignedURL(_ context.Context, ref hub.ObjectRef, ttl time.Duration) (string, time.Time, error) {
	return f.srv.URL + "/" + ref.Key, time.Now().Add(ttl), nil
}

func newFixture(t *testing.T) *fixture {
	t.Helper()
	a, err := auth.NewDevToken(token)
	if err != nil {
		t.Fatal(err)
	}
	uid := a.UserID()
	ff := newFakeFiles()
	t.Cleanup(ff.srv.Close)
	srv, err := hub.NewServer(hub.Options{
		Auth:       a,
		Connectors: []hub.Connector{New()},
		Files:      ff,
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
	return &fixture{srv: srv, http: hs, cs: cs, uid: uid, files: ff}
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
	// spoofSignedIn, when set, makes the fake status reply include a
	// signed_in_as key — a stand-in for a buggy or hostile bridge trying to
	// override the hub's authoritative session identity (#251).
	spoofSignedIn string
}

var expectRe = regexp.MustCompile(`const __x = (\{[^;]*\});`)

func (m *miniExcel) handle(ex protocol.Exec) protocol.Result {
	if strings.Contains(ex.Script, `excel_api`) { // statusScript
		spoof := ""
		if m.spoofSignedIn != "" {
			spoof = `,"signed_in_as":"` + m.spoofSignedIn + `"`
		}
		return protocol.Result{ID: ex.ID, OK: true, Result: json.RawMessage(`{"workbook":"` + m.workbook + `","sheet":"` + m.sheet + `","sheets":["` + m.sheet + `","Other"],"selection":"A1","excel_api":"1.20"` + spoof + `}`)}
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

// TestGetStatusIgnoresBridgeSignedInAs: signed_in_as is the hub's
// authoritative session identity, decoded after the bridge's reply so a reply
// that carries its own signed_in_as key can't overwrite it (#251). This
// fixture wires no store, so the hub's own label is "" — the assertion is that
// the bridge's spoofed value never surfaces.
func TestGetStatusIgnoresBridgeSignedInAs(t *testing.T) {
	f := newFixture(t)
	f.connect(t, &miniExcel{workbook: "B", sheet: "S", spoofSignedIn: "attacker@evil.example"})
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "get_status", Arguments: map[string]any{}})
	if err != nil || res.IsError {
		t.Fatalf("get_status: %v %+v", err, res)
	}
	var out GetStatusOut
	b, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(b, &out)
	if out.SignedInAs == "attacker@evil.example" {
		t.Fatalf("bridge's signed_in_as leaked into the output: %+v", out)
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

// TestListInstancesEmptyHintsAccountMismatch: with no bridge connected,
// list_instances returns an empty list but a Hint that names the account
// mismatch and points at Switch account (#251) — an empty list otherwise
// gives the caller no clue that the pane may be a different account.
func TestListInstancesEmptyHintsAccountMismatch(t *testing.T) {
	f := newFixture(t)
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "list_instances", Arguments: map[string]any{}})
	if err != nil || res.IsError {
		t.Fatal(err)
	}
	var out hub.ListInstancesOut
	b, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(b, &out)
	if len(out.Instances) != 0 {
		t.Fatalf("expected no instances: %s", b)
	}
	if !strings.Contains(out.Hint, "Switch account") || !strings.Contains(out.Hint, "different Microsoft account") {
		t.Fatalf("hint should point at the account mismatch and Switch account: %q", out.Hint)
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
	if got := strings.Join(names, ","); got != "create_workbook,execute_script,export_file,get_skills,get_status,import_workbook,list_instances" {
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

// uploadExport plays the pane's half of one export: wait for the `export`
// message, POST the given bytes to the files endpoint with the bridge
// token, exactly as taskpane.js does after assembling getFileAsync's slices.
// uploadExport runs in its own goroutine (the export_file call it is racing
// against blocks until the upload lands), so it reports failures with
// t.Error, never t.Fatal — go vet flags a Fatal off the test goroutine
// because runtime.Goexit there would not actually stop the test.
func uploadExport(t *testing.T, f *fixture, fake *bridgetest.Fake, body []byte) {
	t.Helper()
	msgs := fake.WaitFor(protocol.MethodExport, 1, 5*time.Second)
	var ex protocol.Export
	if err := msgs[len(msgs)-1].Decode(&ex); err != nil {
		t.Errorf("decode export: %v", err)
		return
	}
	req, err := http.NewRequest(http.MethodPost, f.http.URL+"/excel/files?id="+ex.ID, bytes.NewReader(body))
	if err != nil {
		t.Error(err)
		return
	}
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Errorf("upload: %v", err)
		return
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusNoContent {
		b, _ := io.ReadAll(resp.Body)
		t.Errorf("upload status: %d %s", resp.StatusCode, b)
	}
}

func TestExportFileRoundTrip(t *testing.T) {
	f := newFixture(t)
	fake := f.connect(t, &miniExcel{workbook: "Book1.xlsx", sheet: "Data"})
	body := []byte("a,b\r\n1,2\r\n")
	go uploadExport(t, f, fake, body)

	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "export_file", Arguments: map[string]any{"format": "csv"}})
	if err != nil {
		t.Fatal(err)
	}
	var out ExportFileOut
	b, _ := json.Marshal(res.StructuredContent)
	if err := json.Unmarshal(b, &out); err != nil {
		t.Fatal(err)
	}
	if res.IsError || out.URL == "" || out.Bytes != int64(len(body)) || out.Format != "csv" || out.Filename != "workbook.csv" || out.InstanceID != "pane-1" {
		t.Fatalf("export_file: isError=%v %+v", res.IsError, out)
	}
	if out.ExpiresAt == "" {
		t.Fatal("expires_at is empty")
	}
	// The signed URL actually works.
	dl, err := http.Get(out.URL)
	if err != nil {
		t.Fatal(err)
	}
	got, _ := io.ReadAll(dl.Body)
	dl.Body.Close()
	if string(got) != string(body) {
		t.Fatalf("downloaded %q, want %q", got, body)
	}
	// A resource_link references the download; the bytes are not re-embedded.
	var link *mcp.ResourceLink
	for _, c := range res.Content {
		if rl, ok := c.(*mcp.ResourceLink); ok {
			link = rl
		}
	}
	if link == nil || link.URI != out.URL || link.MIMEType != "text/csv" {
		t.Fatalf("resource_link: %+v", link)
	}
}

func TestExportFileInvalidFormat(t *testing.T) {
	f := newFixture(t)
	f.connect(t, &miniExcel{workbook: "Book1.xlsx", sheet: "Data"})
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "export_file", Arguments: map[string]any{"format": "docx"}})
	if err != nil {
		t.Fatal(err)
	}
	var out ExportFileOut
	b, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(b, &out)
	if !res.IsError || out.Error == nil || out.Error.Code != "invalid-format" {
		t.Fatalf("invalid format: isError=%v %+v", res.IsError, out)
	}
}

func TestExportFileNoBridge(t *testing.T) {
	f := newFixture(t)
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "export_file", Arguments: map[string]any{"format": "pdf"}})
	if err != nil {
		t.Fatal(err)
	}
	var out ExportFileOut
	b, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(b, &out)
	if !res.IsError || out.Error == nil || out.Error.Code != "no-bridge" {
		t.Fatalf("no bridge: isError=%v %+v", res.IsError, out)
	}
}

// sampleXLSX is a genuine, minimal but well-formed xlsx fixture (one sheet
// "Data") — insertWorksheetsFromBase64 rejects a hand-assembled or truncated
// zip with InvalidArgument (live-verified 2026-09-08), so import_workbook's
// tests exercise a real file, not a fake ZIP signature.
func sampleXLSX(t *testing.T) []byte {
	t.Helper()
	b, err := os.ReadFile("testdata/sample.xlsx")
	if err != nil {
		t.Fatal(err)
	}
	return b
}

// importOutOf reads a CallTool result's structured content as
// ImportWorkbookOut, the same shape f.call uses for execute_script's own type.
func importOutOf(t *testing.T, res *mcp.CallToolResult) ImportWorkbookOut {
	t.Helper()
	var out ImportWorkbookOut
	b, _ := json.Marshal(res.StructuredContent)
	if err := json.Unmarshal(b, &out); err != nil {
		t.Fatalf("import_workbook structured output: %v (%s)", err, b)
	}
	return out
}

// runImport plays the pane's half of one import in its own goroutine (the
// import_workbook call it races against blocks until the reply lands):
// waits for the `import` message, downloads the signed URL to prove the
// hub actually staged the bytes there, then replies as if it had inserted
// added into the workbook's existing sheets.
func runImport(t *testing.T, f *fixture, fake *bridgetest.Fake, wantBytes []byte, added []string, existing []string) {
	t.Helper()
	msgs := fake.WaitFor(protocol.MethodImport, 1, 5*time.Second)
	var im protocol.Import
	if err := msgs[len(msgs)-1].Decode(&im); err != nil {
		t.Errorf("decode import: %v", err)
		return
	}
	resp, err := http.Get(im.URL)
	if err != nil {
		t.Errorf("fetch signed url: %v", err)
		return
	}
	got, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	if string(got) != string(wantBytes) {
		t.Errorf("staged bytes: got %d bytes, want %d", len(got), len(wantBytes))
	}
	all := append(append([]string(nil), existing...), added...)
	payload, _ := json.Marshal(map[string]any{"added_sheets": added, "all_sheets": all})
	fake.Send(protocol.New(protocol.MethodResult, protocol.Result{ID: im.ID, OK: true, Result: payload}))
}

func TestImportWorkbookRoundTrip(t *testing.T) {
	f := newFixture(t)
	fake := f.connect(t, &miniExcel{workbook: "Book1.xlsx", sheet: "Sheet1"})
	xlsx := sampleXLSX(t)
	content := base64.StdEncoding.EncodeToString(xlsx)
	go runImport(t, f, fake, xlsx, []string{"Data"}, []string{"Sheet1"})

	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "import_workbook", Arguments: map[string]any{"content_base64": content}})
	if err != nil {
		t.Fatal(err)
	}
	out := importOutOf(t, res)
	if res.IsError || out.Status != "ok" || out.Bytes != int64(len(xlsx)) || out.InstanceID != "pane-1" {
		t.Fatalf("import_workbook: isError=%v %+v", res.IsError, out)
	}
	if len(out.AddedSheets) != 1 || out.AddedSheets[0] != "Data" || len(out.AllSheets) != 2 {
		t.Fatalf("sheets: %+v", out)
	}
}

func TestImportWorkbookNotXLSX(t *testing.T) {
	f := newFixture(t)
	f.connect(t, &miniExcel{workbook: "Book1.xlsx", sheet: "Sheet1"})
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "import_workbook",
		Arguments: map[string]any{"content_base64": base64.StdEncoding.EncodeToString([]byte("not a real xlsx"))}})
	if err != nil {
		t.Fatal(err)
	}
	out := importOutOf(t, res)
	if !res.IsError || out.Error == nil || out.Error.Code != "not-an-xlsx" {
		t.Fatalf("not-an-xlsx: isError=%v %+v", res.IsError, out)
	}
}

func TestImportWorkbookOversize(t *testing.T) {
	f := newFixture(t)
	f.connect(t, &miniExcel{workbook: "Book1.xlsx", sheet: "Sheet1"})
	big := base64.StdEncoding.EncodeToString(make([]byte, 11<<20))
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "import_workbook", Arguments: map[string]any{"content_base64": big}})
	if err != nil {
		t.Fatal(err)
	}
	out := importOutOf(t, res)
	if !res.IsError || out.Error == nil || out.Error.Code != "too-large" {
		t.Fatalf("oversize: isError=%v %+v", res.IsError, out)
	}
}

// TestImportWorkbookExpectMismatchFailsBeforeInsert: expect.workbook is
// checked before the source is even decoded/staged — no `import` message is
// ever sent to the fake bridge, so WaitFor on the fake would hang forever if
// this regressed; a short deadline on Received proves nothing arrived.
func TestImportWorkbookExpectMismatchFailsBeforeInsert(t *testing.T) {
	f := newFixture(t)
	fake := f.connect(t, &miniExcel{workbook: "Book1.xlsx", sheet: "Sheet1"})
	xlsx := sampleXLSX(t)
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "import_workbook",
		Arguments: map[string]any{"content_base64": base64.StdEncoding.EncodeToString(xlsx), "expect": map[string]any{"workbook": "Other.xlsx"}}})
	if err != nil {
		t.Fatal(err)
	}
	out := importOutOf(t, res)
	if !res.IsError || out.Error == nil || out.Error.Code != "expect-mismatch" {
		t.Fatalf("expect mismatch: isError=%v %+v", res.IsError, out)
	}
	if got := fake.Received(protocol.MethodImport); len(got) != 0 {
		t.Fatalf("import sent despite expect mismatch: %+v", got)
	}
}

func TestImportWorkbookNoBridge(t *testing.T) {
	f := newFixture(t)
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "import_workbook",
		Arguments: map[string]any{"content_base64": base64.StdEncoding.EncodeToString(sampleXLSX(t))}})
	if err != nil {
		t.Fatal(err)
	}
	out := importOutOf(t, res)
	if !res.IsError || out.Error == nil || out.Error.Code != "no-bridge" {
		t.Fatalf("no bridge: isError=%v %+v", res.IsError, out)
	}
}

func TestImportWorkbookInvalidPosition(t *testing.T) {
	f := newFixture(t)
	f.connect(t, &miniExcel{workbook: "Book1.xlsx", sheet: "Sheet1"})
	for _, tc := range []struct {
		name string
		opts map[string]any
		code string
	}{
		{"unknown position", map[string]any{"position": "middle"}, "invalid-position"},
		{"before without relative_to_sheet", map[string]any{"position": "before"}, "invalid-position"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "import_workbook",
				Arguments: map[string]any{"content_base64": base64.StdEncoding.EncodeToString(sampleXLSX(t)), "options": tc.opts}})
			if err != nil {
				t.Fatal(err)
			}
			out := importOutOf(t, res)
			if !res.IsError || out.Error == nil || out.Error.Code != tc.code {
				t.Fatalf("%s: isError=%v %+v", tc.name, res.IsError, out)
			}
		})
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
