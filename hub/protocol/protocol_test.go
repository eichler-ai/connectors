package protocol

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestEnvelopeRoundTrip(t *testing.T) {
	msg := New(MethodExec, Exec{ID: "e1", Script: "return 1", Language: "officejs", TimeoutMs: 30000, Limits: Limits{ResultBytes: 16 << 20}})
	data, err := json.Marshal(msg)
	if err != nil {
		t.Fatal(err)
	}
	// The wire shape is fixed: a JSON-RPC 2.0 notification.
	var wire map[string]json.RawMessage
	if err := json.Unmarshal(data, &wire); err != nil {
		t.Fatal(err)
	}
	if string(wire["jsonrpc"]) != `"2.0"` || string(wire["method"]) != `"exec"` || wire["params"] == nil || wire["id"] != nil {
		t.Fatalf("wire shape: %s", data)
	}
	var back Message
	if err := json.Unmarshal(data, &back); err != nil {
		t.Fatal(err)
	}
	var ex Exec
	if err := back.Decode(&ex); err != nil {
		t.Fatal(err)
	}
	if ex.ID != "e1" || ex.Limits.ResultBytes != 16<<20 || ex.TimeoutMs != 30000 {
		t.Fatalf("decoded: %+v", ex)
	}
}

func TestDecodeRejectsBadEnvelope(t *testing.T) {
	var h Hello
	if err := (Message{JSONRPC: "1.0", Method: MethodHello, Params: json.RawMessage(`{}`)}).Decode(&h); err == nil {
		t.Fatal("wrong jsonrpc version accepted")
	}
	if err := (Message{JSONRPC: "2.0", Method: MethodHello}).Decode(&h); err == nil {
		t.Fatal("missing params accepted")
	}
}

func TestExportEnvelopeRoundTrip(t *testing.T) {
	msg := New(MethodExport, Export{ID: "x1", Format: "csv", DocumentID: "wb1"})
	data, err := json.Marshal(msg)
	if err != nil {
		t.Fatal(err)
	}
	var wire map[string]json.RawMessage
	if err := json.Unmarshal(data, &wire); err != nil {
		t.Fatal(err)
	}
	if string(wire["jsonrpc"]) != `"2.0"` || string(wire["method"]) != `"export"` || wire["params"] == nil {
		t.Fatalf("wire shape: %s", data)
	}
	var back Message
	if err := json.Unmarshal(data, &back); err != nil {
		t.Fatal(err)
	}
	var ex Export
	if err := back.Decode(&ex); err != nil {
		t.Fatal(err)
	}
	if ex.ID != "x1" || ex.Format != "csv" || ex.DocumentID != "wb1" {
		t.Fatalf("decoded: %+v", ex)
	}
	// document_id is omitempty: a whole-workbook export names none.
	data, _ = json.Marshal(Export{ID: "x2", Format: "pdf"})
	if strings.Contains(string(data), "document_id") {
		t.Fatalf("empty document_id was not omitted: %s", data)
	}
}

func TestFieldNamesAreSnakeCase(t *testing.T) {
	// §08 names fields in snake_case; a stray Go-style name would silently
	// break the JavaScript side.
	data, _ := json.Marshal(Hello{ProtocolVersion: 1, BridgeVersion: "v", InstanceID: "i"})
	for _, want := range []string{`"protocol_version"`, `"bridge_version"`, `"instance_id"`} {
		if !strings.Contains(string(data), want) {
			t.Fatalf("hello lacks %s: %s", want, data)
		}
	}
	data, _ = json.Marshal(Result{ID: "x", DurationMs: 1, Error: &ScriptError{DebugInfo: json.RawMessage(`1`)}})
	for _, want := range []string{`"duration_ms"`, `"debug_info"`} {
		if !strings.Contains(string(data), want) {
			t.Fatalf("result lacks %s: %s", want, data)
		}
	}
}
