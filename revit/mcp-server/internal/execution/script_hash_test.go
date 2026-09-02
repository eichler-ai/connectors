package execution

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"testing"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

// SucceededRecently is the evidence behind a how-to's session-level
// verification stamp (submit_howto): the exact script text, run on this
// instance, reached success. It must not match a different script text, a
// different instance, or a run that did not succeed.
func TestSucceededRecentlyMatchesExactScriptOnThisInstance(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		status := StatusSuccess
		if s, _ := p["script"].(string); s == "throw;" {
			status = StatusError
		}
		return Result{Status: status, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	sha := func(s string) string { sum := sha256.Sum256([]byte(s)); return hex.EncodeToString(sum[:]) }
	if _, ok := m.SucceededRecently("inst-1", sha("return 1;")); ok {
		t.Fatal("matched before anything ran")
	}
	if _, drec := m.ExecuteScript(context.Background(), "inst-1", "", "return 1;", 5000, 60000, ScriptOptions{}); drec != nil {
		t.Fatal(drec)
	}
	if _, drec := m.ExecuteScript(context.Background(), "inst-1", "", "throw;", 5000, 60000, ScriptOptions{}); drec != nil {
		t.Fatal(drec)
	}
	if at, ok := m.SucceededRecently("inst-1", sha("return 1;")); !ok || at.IsZero() {
		t.Fatalf("exact successful script not matched: ok=%v at=%v", ok, at)
	}
	if _, ok := m.SucceededRecently("inst-1", sha("return 1; ")); ok {
		t.Fatal("a different script text matched")
	}
	if _, ok := m.SucceededRecently("inst-2", sha("return 1;")); ok {
		t.Fatal("another instance matched")
	}
	if _, ok := m.SucceededRecently("inst-1", sha("throw;")); ok {
		t.Fatal("a failed run matched")
	}
}
