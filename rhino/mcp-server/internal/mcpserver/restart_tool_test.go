package mcpserver

import (
	"reflect"
	"testing"

	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
)

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
