package diag

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestNewRecordShape(t *testing.T) {
	r := New(SeverityError, "instance_not_found", "mcp-server.internal.execution",
		"instance \"abc-123\" is not registered with the broker").
		WithDetail(map[string]any{"instance_id": "abc-123"}).
		WithRemedy("call list_instances to confirm the instance is connected, then retry")

	if r.Severity != SeverityError {
		t.Errorf("Severity = %q, want %q", r.Severity, SeverityError)
	}
	if r.Code != "instance_not_found" {
		t.Errorf("Code = %q", r.Code)
	}
	if r.Source != "mcp-server.internal.execution" {
		t.Errorf("Source = %q", r.Source)
	}
	if !strings.Contains(r.Message, "abc-123") {
		t.Errorf("Message must name the concrete identifier, got %q", r.Message)
	}
	if r.Detail["instance_id"] != "abc-123" {
		t.Errorf("Detail not set: %+v", r.Detail)
	}
	if len(r.Remedy) != 1 {
		t.Errorf("Remedy not set: %+v", r.Remedy)
	}
}

func TestRecordJSONShape(t *testing.T) {
	r := New(SeverityWarning, "failure_auto_dismissed", "mcp-server.internal.execution", "warning dismissed")
	b, err := json.Marshal(r)
	if err != nil {
		t.Fatalf("Marshal: %v", err)
	}
	var m map[string]any
	if err := json.Unmarshal(b, &m); err != nil {
		t.Fatalf("Unmarshal: %v", err)
	}
	for _, key := range []string{"severity", "code", "source", "message"} {
		if _, ok := m[key]; !ok {
			t.Errorf("missing required field %q in %s", key, b)
		}
	}
	// detail/remedy are omitted when unset, per the shared shape being lean.
	if _, ok := m["detail"]; ok {
		t.Errorf("detail should be omitted when unset, got %s", b)
	}
	if _, ok := m["remedy"]; ok {
		t.Errorf("remedy should be omitted when unset, got %s", b)
	}
}

func TestSeverityConstants(t *testing.T) {
	cases := []struct {
		got  string
		want string
	}{
		{SeverityDebug, "debug"},
		{SeverityInfo, "info"},
		{SeverityWarning, "warning"},
		{SeverityError, "error"},
	}
	for _, c := range cases {
		if c.got != c.want {
			t.Errorf("got %q, want %q", c.got, c.want)
		}
	}
}

func TestWrapPreservesUnderlyingMessage(t *testing.T) {
	underlying := "System.InvalidOperationException: document is not active"
	r := New(SeverityError, "execution_failed", "mcp-server.internal.execution", "execute_script failed for execution_id \"exec-1\": "+underlying)
	if !strings.Contains(r.Message, underlying) {
		t.Errorf("wrapped message must retain the original exception text, got %q", r.Message)
	}
}
