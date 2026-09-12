package mcpserver

import (
	"reflect"
	"testing"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
)

// cleanResnap returns the same states (nothing changed in the gap).
func cleanResnap(s []execution.DocSaveState) func() ([]execution.DocSaveState, *diag.Record) {
	return func() ([]execution.DocSaveState, *diag.Record) { return s, nil }
}

func neverResnap(t *testing.T) func() ([]execution.DocSaveState, *diag.Record) {
	return func() ([]execution.DocSaveState, *diag.Record) {
		t.Fatal("re-snapshot should not be called here")
		return nil, nil
	}
}

func TestApplyRestartDecision_PreviewNeverRestarts(t *testing.T) {
	restarted := false
	out, isErr := applyRestartDecision(1, states(), false, false, neverResnap(t),
		func(int, []string) (string, error) { restarted = true; return "", nil })
	if out.Status != "preview" || isErr {
		t.Fatalf("preview: status=%q isErr=%v", out.Status, isErr)
	}
	if restarted {
		t.Fatal("preview must not restart")
	}
}

func TestApplyRestartDecision_BlockedNeverRestarts(t *testing.T) {
	s := states()
	s[0].Modified = true
	restarted := false
	out, isErr := applyRestartDecision(1, s, true, false, neverResnap(t),
		func(int, []string) (string, error) { restarted = true; return "", nil })
	if out.Status != "blocked" || !isErr || restarted {
		t.Fatalf("blocked: status=%q isErr=%v restarted=%v", out.Status, isErr, restarted)
	}
}

func TestApplyRestartDecision_ProceedRestartsWithReopen(t *testing.T) {
	restarted := false
	var gotReopen []string
	out, isErr := applyRestartDecision(1, states(), true, false, cleanResnap(states()),
		func(_ int, reopen []string) (string, error) { restarted = true; gotReopen = reopen; return "ok", nil })
	if out.Status != "restarted" || isErr || !restarted {
		t.Fatalf("proceed: status=%q isErr=%v restarted=%v", out.Status, isErr, restarted)
	}
	if !reflect.DeepEqual(gotReopen, []string{"/a.3dm"}) {
		t.Fatalf("reopen passed to restart = %#v", gotReopen)
	}
}

func TestApplyRestartDecision_ReSnapshotBlocksWorkModifiedInTheGap(t *testing.T) {
	dirty := states()
	dirty[0].Modified = true // became modified after the first snapshot
	restarted := false
	out, isErr := applyRestartDecision(1, states(), true, false, cleanResnap(dirty),
		func(int, []string) (string, error) { restarted = true; return "", nil })
	if out.Status != "blocked" || !isErr {
		t.Fatalf("a re-snapshot showing new unsaved work should block, got status=%q", out.Status)
	}
	if restarted {
		t.Fatal("must NOT restart when work was modified during the snapshot->kill gap")
	}
}

func TestApplyRestartDecision_DiscardSkipsReSnapshotAndRestarts(t *testing.T) {
	s := states()
	s[0].Modified = true
	restarted := false
	out, _ := applyRestartDecision(1, s, true, true, neverResnap(t),
		func(int, []string) (string, error) { restarted = true; return "ok", nil })
	if out.Status != "restarted" || !restarted {
		t.Fatalf("discard should skip the re-snapshot and restart, got status=%q restarted=%v", out.Status, restarted)
	}
}

func states() []execution.DocSaveState {
	return []execution.DocSaveState{
		{Kind: "rhino", Title: "A", Path: "/a.3dm", Modified: false},
		{Kind: "rhino", Title: "Untitled", Path: "", Modified: false},
		{Kind: "grasshopper", Title: "Def", Path: "/d.gh", Modified: false},
	}
}

func TestPlanRestart_PreviewWithoutConfirm(t *testing.T) {
	status, reopen, unsaved := planRestart(states(), false, false)
	if status != "preview" {
		t.Fatalf("status = %q, want preview", status)
	}
	if !reflect.DeepEqual(reopen, []string{"/a.3dm"}) {
		t.Fatalf("reopen = %#v, want just the saved Rhino doc", reopen)
	}
	if len(unsaved) != 0 {
		t.Fatalf("nothing is modified; unsaved = %#v", unsaved)
	}
}

func TestPlanRestart_BlocksOnUnsavedWhenConfirmedWithoutDiscard(t *testing.T) {
	s := states()
	s[2].Modified = true // an unsaved Grasshopper definition
	status, _, unsaved := planRestart(s, true, false)
	if status != "blocked" {
		t.Fatalf("status = %q, want blocked", status)
	}
	if len(unsaved) != 1 || unsaved[0] != `grasshopper "Def"` {
		t.Fatalf("unsaved = %#v", unsaved)
	}
}

func TestPlanRestart_ProceedsWithDiscard(t *testing.T) {
	s := states()
	s[0].Modified = true
	status, reopen, _ := planRestart(s, true, true)
	if status != "proceed" {
		t.Fatalf("status = %q, want proceed", status)
	}
	if !reflect.DeepEqual(reopen, []string{"/a.3dm"}) {
		t.Fatalf("reopen = %#v", reopen)
	}
}

func TestPlanRestart_ProceedsWhenCleanAndConfirmed(t *testing.T) {
	status, reopen, unsaved := planRestart(states(), true, false)
	if status != "proceed" || len(unsaved) != 0 {
		t.Fatalf("clean+confirmed should proceed, got status=%q unsaved=%#v", status, unsaved)
	}
	if !reflect.DeepEqual(reopen, []string{"/a.3dm"}) {
		t.Fatalf("reopen = %#v", reopen)
	}
}

func TestPlanRestart_OnlyRhinoSavedDocsReopen(t *testing.T) {
	// A saved Grasshopper definition (Path set) is NOT in the reopen list.
	_, reopen, _ := planRestart(states(), true, false)
	for _, p := range reopen {
		if p == "/d.gh" {
			t.Fatal("Grasshopper definitions must not be auto-reopened")
		}
	}
}
