//go:build harness

package harness_test

import (
	"fmt"
	"strconv"
	"strings"
	"testing"
	"time"
)

// TestPublishFileExchange is the live pin of PRD §09's Publish/files[]
// contract, previously untested at any tier end to end (issue #36): a
// published file comes back as a per-file record; publishing onto an
// existing name FAILS THAT FILE (naming overwrite_output_files) without
// failing the run; and the flag makes the same publish succeed. The three
// calls use the same file name deliberately -- the collision is the point.
func TestPublishFileExchange(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	// The source is written OUTSIDE exports/ (temp), so Publish takes its COPY
	// path -- the one the overwrite_output_files contract governs. Writing the
	// file directly into exports/ instead takes PRD §09's register-in-place
	// path (Publish "recognizes the file is already there and just registers
	// it"), where there is no copy and so no collision to gate -- this test's
	// first live run conflated the two and read register-in-place as a
	// collision-gate failure; the separate pin below keeps them distinct.
	// Unique per run so a leftover from an interrupted earlier run can't turn
	// the first publish into a collision; cleanup below deletes everything.
	fileName := fmt.Sprintf("harness-file-exchange-%d.txt", time.Now().UnixNano())
	publishScript := `
var src = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ` + strconv.Quote(fileName) + `);
System.IO.File.WriteAllText(src, "harness file-exchange probe");
Publish(src);
return "published-call-made";
`
	t.Cleanup(func() {
		cleanup := `
try { System.IO.File.Delete(System.IO.Path.Combine(ExportsDirectory, ` + strconv.Quote(fileName) + `)); } catch {}
try { System.IO.File.Delete(System.IO.Path.Combine(System.IO.Path.GetTempPath(), ` + strconv.Quote(fileName) + `)); } catch {}
return "cleaned";
`
		_ = callExecuteScriptWith(t, c, instanceID, documentID, cleanup, nil)
	})

	// First publish: one files[] entry, status published, with a real path.
	first := runScript(t, c, instanceID, documentID, publishScript)
	if first.Status != "success" {
		t.Fatalf("first publish run failed: status=%q output=%s", first.Status, first.Output)
	}
	if len(first.Files) != 1 {
		t.Fatalf("expected exactly one files[] entry, got %d: %+v", len(first.Files), first.Files)
	}
	if first.Files[0].Status != "published" {
		t.Fatalf("first publish's per-file status = %q, want published: %+v", first.Files[0].Status, first.Files[0])
	}
	if first.Files[0].Path == "" || first.Files[0].Name == "" {
		t.Errorf("published entry must carry a usable name and path: %+v", first.Files[0])
	}

	// Same name again, no overwrite flag: the RUN still succeeds (per-file
	// independence, PRD §09 -- a collision must never abort the batch or the
	// script) but THAT FILE fails, and the message names the flag that would
	// have allowed it -- the §01 remedy an agent keys on.
	second := runScript(t, c, instanceID, documentID, publishScript)
	if second.Status != "success" {
		t.Fatalf("a publish collision must not fail the run itself (PRD §09): status=%q output=%s", second.Status, second.Output)
	}
	if len(second.Files) != 1 {
		t.Fatalf("expected exactly one files[] entry on the collision, got %d: %+v", len(second.Files), second.Files)
	}
	if second.Files[0].Status != "failed" {
		t.Fatalf("publishing onto an existing name without the flag must fail that file, got status %q: %+v", second.Files[0].Status, second.Files[0])
	}
	if !strings.Contains(second.Files[0].Message, "overwrite_output_files") {
		t.Errorf("the per-file failure must name overwrite_output_files so the agent knows the fix; message: %q", second.Files[0].Message)
	}

	// With the flag: the identical publish overwrites and succeeds.
	third := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID, publishScript,
		map[string]any{"overwrite_output_files": true}))
	if third.Status != "success" {
		t.Fatalf("overwriting publish run failed: status=%q output=%s", third.Status, third.Output)
	}
	if len(third.Files) != 1 || third.Files[0].Status != "published" {
		t.Fatalf("with overwrite_output_files the same publish must succeed, got: %+v", third.Files)
	}

	// PRD §09's OTHER publish path, pinned live because this test's first run
	// tripped over it: a script that wrote its file DIRECTLY into exports/
	// and then publishes that same path gets register-in-place -- no copy, no
	// collision gate involved, `published` without any flag even though the
	// name exists. That is documented behavior ("Publish recognizes the file
	// is already there and just registers it"), not a gate bypass: the gate
	// governs Publish's own copy, and here the script itself owned the write.
	inPlace := runScript(t, c, instanceID, documentID, `
var p = System.IO.Path.Combine(ExportsDirectory, `+strconv.Quote(fileName)+`);
System.IO.File.WriteAllText(p, "rewritten in place by the script itself");
Publish(p);
return "registered-in-place";
`)
	if inPlace.Status != "success" {
		t.Fatalf("register-in-place run failed: status=%q output=%s", inPlace.Status, inPlace.Output)
	}
	if len(inPlace.Files) != 1 || inPlace.Files[0].Status != "published" {
		t.Fatalf("a direct-write-then-publish into exports/ must register in place as published (PRD §09), got: %+v", inPlace.Files)
	}
}

// TestExecutionAuditTrail is the live pin of PRD §09's per-execution audit
// trail (issue #13): every run that reached the executor leaves a verbatim
// script copy under scripts/ and an NDJSON log under logs/ in the ROUTED
// document's workspace. The workspace lives on the machine RUNNING REVIT
// (%USERPROFILE%\RevitMCPExchange in local mode), so the assertion runs as a
// FOLLOW-UP SCRIPT reading the directories from inside Revit rather than any
// Mac-side filesystem guess -- the same posture the audit trail's own docs
// take about who can see these files.
func TestExecutionAuditTrail(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	// A uniquely-identifiable run whose audit pair we then go looking for.
	needle := fmt.Sprintf("audit-probe-%d", time.Now().UnixNano())
	probe := runScript(t, c, instanceID, documentID, `return "`+needle+`";`)
	if probe.Status != "success" {
		t.Fatalf("probe run failed: status=%q output=%s", probe.Status, probe.Output)
	}
	if probe.ExecutionID == "" {
		t.Fatal("probe run carried no execution_id to look for")
	}

	// The audit pair is written before the probe's own response is sent, so
	// this follow-up run can read it with no wait. ExportsDirectory's parent
	// is the document workspace root; logs/ and scripts/ are its siblings.
	inspect := runScript(t, c, instanceID, documentID, `
var root = System.IO.Path.GetDirectoryName(ExportsDirectory);
var logs = System.IO.Path.Combine(root, "logs");
var scripts = System.IO.Path.Combine(root, "scripts");
var logMatches = System.IO.Directory.Exists(logs) ? System.IO.Directory.GetFiles(logs, "*`+probe.ExecutionID+`.ndjson") : new string[0];
var scriptMatches = System.IO.Directory.Exists(scripts) ? System.IO.Directory.GetFiles(scripts, "*`+probe.ExecutionID+`.cs") : new string[0];
if (logMatches.Length != 1 || scriptMatches.Length != 1)
    return "MISSING logs=" + logMatches.Length + " scripts=" + scriptMatches.Length;
var scriptText = System.IO.File.ReadAllText(scriptMatches[0]);
var logText = System.IO.File.ReadAllText(logMatches[0]);
if (!scriptText.Contains("`+needle+`")) return "SCRIPT-CONTENT-MISMATCH";
if (!logText.Contains("execution-audit") || !logText.Contains("\"status\":\"success\"")) return "LOG-SHAPE-MISMATCH: " + logText;
return "audit-ok";
`)
	if inspect.Status != "success" || inspect.Output != "audit-ok" {
		t.Fatalf("audit trail verification failed: status=%q output=%s", inspect.Status, inspect.Output)
	}
}
