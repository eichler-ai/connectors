package transport

import (
	"bytes"
	"encoding/json"
	"io"
	"strings"
	"testing"
)

func TestFramerWriteMessageWritesOneLinePerObject(t *testing.T) {
	var buf bytes.Buffer
	f := NewFramer(&buf, &buf)

	m1, _ := NewNotification("register", map[string]string{"instance_id": "a"})
	m2, _ := NewNotification("register", map[string]string{"instance_id": "b"})

	if err := f.WriteMessage(m1); err != nil {
		t.Fatalf("WriteMessage 1: %v", err)
	}
	if err := f.WriteMessage(m2); err != nil {
		t.Fatalf("WriteMessage 2: %v", err)
	}

	lines := strings.Split(strings.TrimRight(buf.String(), "\n"), "\n")
	if len(lines) != 2 {
		t.Fatalf("got %d lines, want 2: %q", len(lines), buf.String())
	}
	for _, line := range lines {
		var raw map[string]any
		if err := json.Unmarshal([]byte(line), &raw); err != nil {
			t.Errorf("line not valid JSON: %q: %v", line, err)
		}
	}
}

func TestFramerReadMessageRoundTrips(t *testing.T) {
	var buf bytes.Buffer
	f := NewFramer(&buf, &buf)

	sent, _ := NewRequest(json.RawMessage("1"), "execute_script", map[string]string{"script": "1+1"})
	if err := f.WriteMessage(sent); err != nil {
		t.Fatalf("WriteMessage: %v", err)
	}

	got, err := f.ReadMessage()
	if err != nil {
		t.Fatalf("ReadMessage: %v", err)
	}
	if got.Method != "execute_script" {
		t.Errorf("Method = %q, want execute_script", got.Method)
	}
	if !got.IsRequest() {
		t.Errorf("expected IsRequest() true")
	}
}

func TestFramerReadMessageEOF(t *testing.T) {
	f := NewFramer(strings.NewReader(""), io.Discard)
	_, err := f.ReadMessage()
	if err != io.EOF {
		t.Fatalf("got %v, want io.EOF", err)
	}
}

func TestFramerReadMessageSkipsBlankLines(t *testing.T) {
	r := strings.NewReader("\n\n{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}\n")
	f := NewFramer(r, io.Discard)
	got, err := f.ReadMessage()
	if err != nil {
		t.Fatalf("ReadMessage: %v", err)
	}
	if got.Method != "ping" {
		t.Errorf("Method = %q, want ping", got.Method)
	}
}

func TestFramerReadMessageRejectsMalformedJSON(t *testing.T) {
	r := strings.NewReader("not json\n")
	f := NewFramer(r, io.Discard)
	_, err := f.ReadMessage()
	if err == nil {
		t.Fatal("expected error for malformed JSON line")
	}
}

func TestFramerReadMessageMultipleSequential(t *testing.T) {
	var buf bytes.Buffer
	f := NewFramer(&buf, &buf)
	for i := 0; i < 3; i++ {
		m, _ := NewNotification("ping", nil)
		_ = f.WriteMessage(m)
	}
	for i := 0; i < 3; i++ {
		got, err := f.ReadMessage()
		if err != nil {
			t.Fatalf("ReadMessage %d: %v", i, err)
		}
		if got.Method != "ping" {
			t.Errorf("Method = %q", got.Method)
		}
	}
	if _, err := f.ReadMessage(); err != io.EOF {
		t.Fatalf("got %v, want io.EOF after exhausting stream", err)
	}
}

func TestFramerRejectsEmbeddedNewlineNever(t *testing.T) {
	// Sanity check on the framing assumption cited in PRD §05: valid JSON
	// never contains a literal unescaped newline, so a JSON string value
	// containing "\n" must round-trip as the escape sequence, not break
	// the one-object-per-line invariant.
	var buf bytes.Buffer
	f := NewFramer(&buf, &buf)
	m, _ := NewNotification("register", map[string]string{"note": "line one\nline two"})
	if err := f.WriteMessage(m); err != nil {
		t.Fatalf("WriteMessage: %v", err)
	}
	lines := strings.Split(strings.TrimRight(buf.String(), "\n"), "\n")
	if len(lines) != 1 {
		t.Fatalf("embedded \\n leaked into framing: got %d lines: %q", len(lines), buf.String())
	}
}
