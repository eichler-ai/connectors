// Package protocol defines bridge protocol v1 (hub PRD §08): the messages a
// bridge — an Excel task pane, a Figma plugin, a Revit add-in in remote mode —
// exchanges with the hub. It is JSON-RPC 2.0 framing over WebSocket text
// frames (or NDJSON over TCP for a local-mode server), and it is the one
// schema every connector shares, so it lives outside internal/ with no
// dependency on the hub runtime: a local stdio server imports this package,
// not the hub.
//
// Every message is a JSON-RPC notification: {"jsonrpc":"2.0","method":M,
// "params":P}. Request/response correlation is not done with JSON-RPC ids but
// with the exec id carried inside the params of `exec`, `result` and
// `cancel`. That is deliberate — one message shape in both directions keeps
// the browser client trivial, a result is then a first-class message that
// can arrive late or after a reconnect without the framing layer having to
// know about it, and there is nothing in v1 for which a JSON-RPC error
// response would be the right channel (a failed script is a successful
// `result` with ok:false; a refused hello is a WebSocket close with a reason).
package protocol

import (
	"encoding/json"
	"fmt"

	"github.com/eichler-ai/connectors/hub/diag"
)

// Version is the protocol version this package describes. A bridge sends it
// in hello; the hub accepts the current and the previous version and reports
// `bridge-outdated` for anything older (§08). There is no previous version
// yet, so exactly 1 is accepted.
const Version = 1

// Method names, exactly as in the §08 table.
const (
	MethodHello    = "hello"    // bridge → hub, first message
	MethodRegister = "register" // bridge → hub, live documents[] update
	MethodExec     = "exec"     // hub → bridge
	MethodResult   = "result"   // bridge → hub
	MethodExport   = "export"   // hub → bridge, ask for a file (§10/§11)
	MethodImport   = "import"   // hub → bridge, insert sheets from a signed URL (§10/§11, reversed)
	MethodCancel   = "cancel"   // hub → bridge, best effort
	MethodNotice   = "notice"   // either direction, out-of-band diagnostic
	MethodReplaced = "replaced" // hub → bridge, connection superseded
	MethodPing     = "ping"     // either direction
	MethodPong     = "pong"     // either direction
)

// Message is the wire envelope.
type Message struct {
	JSONRPC string          `json:"jsonrpc"`
	Method  string          `json:"method"`
	Params  json.RawMessage `json:"params,omitempty"`
}

// New wraps params in an envelope for method. It panics on a params value
// that cannot be marshalled, which for the fixed types below is a programming
// error rather than a runtime condition.
func New(method string, params any) Message {
	raw, err := json.Marshal(params)
	if err != nil {
		panic(fmt.Sprintf("protocol: marshal %s params: %v", method, err))
	}
	return Message{JSONRPC: "2.0", Method: method, Params: raw}
}

// Decode unmarshals m.Params into v, checking the envelope first.
func (m Message) Decode(v any) error {
	if m.JSONRPC != "2.0" {
		return fmt.Errorf("protocol: jsonrpc is %q, want \"2.0\"", m.JSONRPC)
	}
	if len(m.Params) == 0 {
		return fmt.Errorf("protocol: %s has no params", m.Method)
	}
	return json.Unmarshal(m.Params, v)
}

// Host identifies the application a bridge runs inside.
type Host struct {
	// App is the host application name as the host reports it ("Excel",
	// "Revit"); Platform is where it runs ("OfficeOnline", "PC", "Mac");
	// Version is the host's own version string.
	App      string `json:"app"`
	Platform string `json:"platform"`
	Version  string `json:"version"`
}

// Document is one entry of a bridge's live documents[].
type Document struct {
	// ID is the host's stable identifier for the document within this bridge,
	// or the best available stand-in (Excel for the web has no document id in
	// Office.js, so the add-in uses the workbook URL, falling back to the
	// name). Title is the user-visible name, Path the location if any.
	ID    string `json:"id"`
	Title string `json:"title"`
	Path  string `json:"path,omitempty"`
	// Active marks the document the host would apply a script to by default.
	Active bool `json:"active"`
	// Detail carries host-specific state a connector wants visible in
	// list_instances without a schema change here (Excel: the active sheet).
	Detail map[string]any `json:"detail,omitempty"`
}

// Hello is the first message a bridge sends. Anything else first, or an
// unacceptable hello, closes the socket with a reason.
type Hello struct {
	// Connector is the slug of the connector this bridge belongs to and must
	// match the /<connector>/bridge path it dialled.
	Connector       string `json:"connector"`
	ProtocolVersion int    `json:"protocol_version"`
	// BridgeVersion is the bridge's own build/version string, for
	// `bridge-outdated` reporting and logs.
	BridgeVersion string `json:"bridge_version"`
	// InstanceID is stable per bridge runtime (per pane load, per plugin
	// process) and is what makes a reconnect a replacement rather than a
	// second instance (§07).
	InstanceID string     `json:"instance_id"`
	Host       Host       `json:"host"`
	Documents  []Document `json:"documents"`
	// Token is the bridge token (§06) — in phase 0 the shared dev token. The
	// hub never logs it.
	Token string `json:"token"`
}

// Register replaces the bridge's documents[] wholesale; sent on every
// document change so the registry stays live.
type Register struct {
	Documents []Document `json:"documents"`
}

// Limits are the caps the hub asks the bridge to honour for one exec.
type Limits struct {
	// ResultBytes is the largest serialised result the hub will accept; the
	// bridge truncates beyond it and sets Result.Truncated. The hub's own
	// socket read limit is set above this figure, so a bridge that ignores it
	// gets its connection closed rather than the hub buffering unboundedly.
	ResultBytes int `json:"result_bytes"`
}

// Exec asks the bridge to run a script.
type Exec struct {
	// ID correlates the eventual Result and any Cancel. Unique per hub
	// process; an unknown id in a result is dropped and logged.
	ID string `json:"id"`
	// DocumentID selects a document where the host has several open; empty
	// means the host's active/default document.
	DocumentID string `json:"document_id,omitempty"`
	// Script is opaque to the hub; Language is the connector-defined tag that
	// says how to run it ("officejs", "csharp", …).
	Script   string `json:"script"`
	Language string `json:"language"`
	// TimeoutMs is the cooperative deadline. The hub stops waiting at this
	// point regardless; the bridge uses it to abandon work where it can.
	TimeoutMs int64  `json:"timeout_ms"`
	Limits    Limits `json:"limits"`
}

// ScriptError is what a failed script reports. Name and Message are the
// thrown error's; Code, DebugInfo and Stack are whatever the host attached
// (Office.js puts its error code and the failing statement there).
type ScriptError struct {
	Name      string          `json:"name"`
	Message   string          `json:"message"`
	Code      string          `json:"code,omitempty"`
	DebugInfo json.RawMessage `json:"debug_info,omitempty"`
	Stack     string          `json:"stack,omitempty"`
}

// Result is the bridge's reply to an Exec.
type Result struct {
	ID string `json:"id"`
	// OK is false when Error is set; a script that threw is still a
	// successful round trip and travels here, not as a protocol error.
	OK bool `json:"ok"`
	// Result is the script's JSON return value, left raw so the hub never
	// re-serialises a multi-megabyte payload it does not interpret.
	Result     json.RawMessage `json:"result,omitempty"`
	Error      *ScriptError    `json:"error,omitempty"`
	DurationMs float64         `json:"duration_ms"`
	// Truncated is set when the bridge cut the result to Limits.ResultBytes.
	Truncated bool `json:"truncated,omitempty"`
	// Notices are things the bridge resolved or observed on the agent's behalf
	// while running this script (observability over silence).
	Notices []diag.Record `json:"notices,omitempty"`
}

// Export asks the bridge to produce a file and upload its bytes to the hub
// over plain HTTPS — POST /<connector>/files?id=<id> with the same bridge
// token presented in hello — rather than returning them as a `result` (§10:
// "nothing base64 in a script"; a whole-document export can be many MiB,
// which does not belong inside the WebSocket's JSON framing). ID correlates
// the eventual outcome exactly like Exec's: the bridge's own `result` (ok,
// or an error such as a failed getFileAsync) if it never gets as far as
// uploading, or — once the upload lands — a synthetic `result` the hub
// itself delivers with the signed URL. Format is a connector-defined tag
// (Excel: csv, xlsx, pdf); DocumentID selects a document as in Exec.
type Export struct {
	ID         string `json:"id"`
	Format     string `json:"format"`
	DocumentID string `json:"document_id,omitempty"`
}

// ImportOptions maps to Office.js Excel.InsertWorksheetOptions, the options
// argument of `workbook.insertWorksheetsFromBase64` (excel/poc/scripts/
// 14-insert-from-base64.js). RelativeToSheet is a sheet name, not an object
// reference — the wire protocol carries no object references, so the bridge
// resolves it locally with worksheets.getItem before calling insert.
type ImportOptions struct {
	// SheetNamesToInsert selects which sheets of the source file to bring in;
	// empty means every sheet.
	SheetNamesToInsert []string `json:"sheet_names_to_insert,omitempty"`
	// PositionType is one of Office.js's Excel.WorksheetPositionType string
	// values (None, Before, After, Beginning, End); empty defaults to End on
	// the bridge side (§10 reversed: "after the last existing sheet").
	PositionType string `json:"position_type,omitempty"`
	// RelativeToSheet names the sheet Before/After is relative to; required
	// only for those two PositionType values.
	RelativeToSheet string `json:"relative_to_sheet,omitempty"`
}

// Import asks the bridge to fetch url — a short-lived signed URL into the
// hub's file store, never the bytes themselves over the socket — and insert
// its worksheets into the currently open workbook via
// `workbook.insertWorksheetsFromBase64` (§10/§11, the export flow reversed:
// bytes → hub → GCS → signed URL → pane → Office, instead of pane → hub →
// GCS → signed URL → client). ID correlates the eventual `result` exactly
// like Exec's; DocumentID selects a document as in Exec.
type Import struct {
	ID         string `json:"id"`
	DocumentID string `json:"document_id,omitempty"`
	URL        string `json:"url"`
	// Options is always sent, never omitted: encoding/json's omitempty does
	// not apply to a struct value, and the hub always fills PositionType with
	// a concrete default (§10 reversed: "after the last existing sheet") so
	// there is no meaningful "absent options" to distinguish from "defaults".
	Options ImportOptions `json:"options"`
}

// Cancel asks the bridge to abandon a running exec. Best effort: a bridge that
// cannot interrupt (a task pane's single JavaScript thread) replies with a
// `cannot-cancel` notice instead.
type Cancel struct {
	ID string `json:"id"`
}

// Notice is an out-of-band diagnostic record in either direction, for
// conditions not tied to a specific in-flight exec (or arriving after it).
type Notice struct {
	// ExecID names the exec the notice is about, when there is one.
	ExecID string      `json:"exec_id,omitempty"`
	Record diag.Record `json:"record"`
}

// Replaced tells a bridge that a newer connection with its instance_id took
// over. It must not reconnect (§07: newest wins, with notice).
type Replaced struct {
	Reason string `json:"reason"`
}

// Ping and Pong carry nothing; they exist because idle WebSockets die under
// proxies and browsers cannot send WebSocket-level pings from script.
type (
	Ping struct{}
	Pong struct{}
)
