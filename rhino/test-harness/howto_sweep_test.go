//go:build harness

package harness_test

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/rhino/test-harness/mcpclient"
)

// The harness is its own module and cannot import the broker's internal howto
// package, so the slice of the document and stamp shapes it needs is mirrored
// here (schema: rhino/mcp-server/internal/howto/schema/). The broker's own unit
// tests validate the files; this reads them.
type howtoDoc struct {
	ID         string `json:"id"`
	Rev        int    `json:"rev"`
	Script     string `json:"script"`
	ScriptLang string `json:"script_language"`
	Verify     *struct {
		ExpectObjectDelta    *int           `json:"expect_object_delta"`
		ExpectReturnContains string         `json:"expect_return_contains"`
		Execute              map[string]any `json:"execute"`
	} `json:"verify"`
}

type howtoStamp struct {
	ID           string `json:"id"`
	Rev          int    `json:"rev"`
	ScriptSHA256 string `json:"script_sha256"`
	RhinoVersion string `json:"rhino_version"`
	Status       string `json:"status"`
	At           string `json:"at"`
	By           string `json:"by"`
	Diagnostic   string `json:"diagnostic,omitempty"`
}

func scriptSHA256(script string) string {
	sum := sha256.Sum256([]byte(script))
	return hex.EncodeToString(sum[:])
}

// majorVersion reduces a reported Rhino version ("8.35.26251") to its major
// ("8"), which is the stamp/rank granularity (Rhino's API-compat boundary).
func majorVersion(v string) string {
	if i := strings.IndexByte(v, '.'); i >= 0 {
		return v[:i]
	}
	return v
}

var (
	howtoStampsFlag = flag.Bool("howto-stamps", false, "TestRhinoHowToSweep: write a harness stamp per document into corpus/verified.jsonl (replacing any earlier stamp for the same id and Rhino major version)")
	howtoOnlyFlag   = flag.String("howto-only", "", "TestRhinoHowToSweep: comma-separated document ids to run (default: every document)")
)

// corpusDir is the shared corpus in this checkout, relative to this module.
const corpusDir = "../mcp-server/internal/howto/corpus"

// TestRhinoHowToSweep is the tier-2 verification of the Rhino how-to corpus:
// every document's script runs, as shipped, against the connected Rhino, and
// must succeed, return its expected substring, and produce the net
// active-object delta its `verify` block promises. With -howto-stamps the
// result is recorded in the sidecar, keyed by the exact script hash, which is
// what describe_howto later shows as "verified on Rhino <major>".
//
// Nothing is substituted into the script text: the hash stamped is the hash
// served. The script runs against the connected document (there is no Rhino
// "blank fixture" helper), so run the sweep against a SCRATCH document — it
// leaves its test geometry behind. Run ONE sweep at a time: executions
// serialise on Rhino's UI thread.
//
// NOTE: the object delta is measured with an explicit active-normal enumerator,
// NOT RhinoDoc.Objects.Count, which counts recently-deleted (undo-buffered)
// objects.
func TestRhinoHowToSweep(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	doc := docID(inst)
	if doc == "" {
		t.Skip("no document open in the connected Rhino; open a scratch document and rerun")
	}
	rhinoMajor := majorVersion(inst.RhinoVersion)
	if rhinoMajor == "" {
		t.Fatalf("instance %s reports no rhino_version; a stamp needs one", inst.InstanceID)
	}

	only := map[string]bool{}
	for _, id := range strings.Split(*howtoOnlyFlag, ",") {
		if id = strings.TrimSpace(id); id != "" {
			only[id] = true
		}
	}
	files, err := filepath.Glob(filepath.Join(corpusDir, "*.json"))
	if err != nil || len(files) == 0 {
		t.Fatalf("no corpus documents under %s (%v)", corpusDir, err)
	}
	sort.Strings(files)

	var results []howtoStamp
	seen := map[string]bool{}
	for _, file := range files {
		raw, err := os.ReadFile(file)
		if err != nil {
			t.Fatal(err)
		}
		var d howtoDoc
		if err := json.Unmarshal(raw, &d); err != nil || d.ID == "" {
			t.Fatalf("%s: %v", file, err)
		}
		if len(only) > 0 && !only[d.ID] {
			continue
		}
		seen[d.ID] = true
		t.Run(d.ID, func(t *testing.T) {
			stamp := howtoStamp{ID: d.ID, Rev: d.Rev, ScriptSHA256: scriptSHA256(d.Script), RhinoVersion: rhinoMajor, At: time.Now().UTC().Format(time.RFC3339), By: "harness"}
			diag := sweepOne(t, c, inst.InstanceID, doc, &d)
			if diag == "" {
				stamp.Status = "passed"
			} else {
				stamp.Status, stamp.Diagnostic = "failed", truncate(diag, 1000)
				t.Errorf("%s: %s", d.ID, diag)
			}
			results = append(results, stamp)
		})
	}
	for id := range only {
		if !seen[id] {
			t.Errorf("-howto-only: no document with id %q under %s", id, corpusDir)
		}
	}
	if *howtoStampsFlag && len(results) > 0 {
		writeStamps(t, results)
	}
}

// activeObjectCount reads the number of active, normal (non-deleted) objects in
// the document via an explicit enumerator — the count the object delta is
// measured against.
func activeObjectCount(t *testing.T, c *mcpclient.Client, instanceID, doc string) int {
	t.Helper()
	const script = `import Rhino
import scriptcontext as sc
s = Rhino.DocObjects.ObjectEnumeratorSettings()
s.DeletedObjects = False
s.NormalObjects = True
result = str(len(list(sc.doc.Objects.GetObjectList(s))))`
	out := callExecute(t, c, map[string]any{"instance_id": instanceID, "document_id": doc, "language": "python", "script": script}, 30*time.Second)
	if out.Status != "success" {
		t.Fatalf("active-object count probe failed: status=%s err=%+v", out.Status, out.Error)
	}
	var n int
	if _, err := fmt.Sscanf(strings.Trim(out.ReturnValue, "\" "), "%d", &n); err != nil {
		t.Fatalf("active-object count %q not an int: %v", out.ReturnValue, err)
	}
	return n
}

// sweepOne runs one document and returns "" on success or the failure
// diagnostic.
func sweepOne(t *testing.T, c *mcpclient.Client, instanceID, doc string, d *howtoDoc) string {
	t.Helper()
	if d.Script == "" {
		return "document has no script"
	}
	lang := d.ScriptLang
	if lang == "csharp-script" {
		lang = "csharp"
	}

	before := activeObjectCount(t, c, instanceID, doc)

	args := map[string]any{"instance_id": instanceID, "document_id": doc, "language": lang, "script": d.Script}
	if d.Verify != nil {
		for k, v := range d.Verify.Execute {
			args[k] = v
		}
	}
	out := callExecute(t, c, args, 60*time.Second)
	// A how-to that saves or is slow can outlive execute_script's wait; poll it
	// to a terminal state, as an agent would.
	for polls := 0; (out.Status == "pending" || out.Status == "running") && polls < 8; polls++ {
		out = poll(t, c, out.ExecutionID, 30000)
	}
	if out.Status != "success" {
		msg := out.Status
		if out.Error != nil {
			msg += ": " + out.Error.Code + " " + out.Error.Message
		}
		return "run " + msg
	}

	var problems []string
	if d.Verify != nil && d.Verify.ExpectReturnContains != "" && !strings.Contains(out.ReturnValue, d.Verify.ExpectReturnContains) {
		problems = append(problems, fmt.Sprintf("return %q does not contain %q", out.ReturnValue, d.Verify.ExpectReturnContains))
	}
	if d.Verify != nil && d.Verify.ExpectObjectDelta != nil {
		after := activeObjectCount(t, c, instanceID, doc)
		delta := after - before
		// A creating run's object is committed once the run reports terminal
		// success, but the very first run after a fresh connect has once raced a
		// follow-up read. If the delta is off, settle briefly and re-read once:
		// a genuine mismatch persists, a timing artefact resolves.
		if delta != *d.Verify.ExpectObjectDelta {
			time.Sleep(500 * time.Millisecond)
			after = activeObjectCount(t, c, instanceID, doc)
			delta = after - before
		}
		if delta != *d.Verify.ExpectObjectDelta {
			problems = append(problems, fmt.Sprintf("active-object delta = %d (before=%d after=%d), want %d", delta, before, after, *d.Verify.ExpectObjectDelta))
		}
	}
	t.Logf("%s: status=success return=%q", d.ID, out.ReturnValue)
	if len(problems) > 0 {
		return strings.Join(problems, "; ")
	}
	return ""
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	r := []rune(s)
	for len(r) > 0 && len(string(r)) > n-len("…") {
		r = r[:len(r)-1]
	}
	return string(r) + "…"
}

// writeStamps merges this run's stamps into the sidecar. For every id this run
// swept, earlier stamps for the SAME Rhino major version are replaced whatever
// their rev or hash (an edited script's old stamp is stale and must not survive
// a rerun — the broker's unit test fails CI on it); stamps for ids not run, and
// for other versions, are kept. Rewritten in id order.
func writeStamps(t *testing.T, fresh []howtoStamp) {
	t.Helper()
	path := filepath.Join(corpusDir, "verified.jsonl")
	var kept []howtoStamp
	if raw, err := os.ReadFile(path); err == nil && len(bytes.TrimSpace(raw)) > 0 {
		for _, line := range bytes.Split(raw, []byte("\n")) {
			if len(bytes.TrimSpace(line)) == 0 {
				continue
			}
			var s howtoStamp
			if err := json.Unmarshal(line, &s); err != nil {
				t.Fatalf("existing sidecar: %v", err)
			}
			kept = append(kept, s)
		}
	}
	key := func(s howtoStamp) string { return s.ID + "|" + s.RhinoVersion }
	replaced := map[string]bool{}
	for _, s := range fresh {
		replaced[key(s)] = true
	}
	var all []howtoStamp
	for _, s := range kept {
		if !replaced[key(s)] {
			all = append(all, s)
		}
	}
	all = append(all, fresh...)
	sort.SliceStable(all, func(i, j int) bool {
		if all[i].ID != all[j].ID {
			return all[i].ID < all[j].ID
		}
		return all[i].RhinoVersion < all[j].RhinoVersion
	})
	var buf bytes.Buffer
	for _, s := range all {
		line, err := json.Marshal(s)
		if err != nil {
			t.Fatal(err)
		}
		buf.Write(line)
		buf.WriteByte('\n')
	}
	if err := os.WriteFile(path, buf.Bytes(), 0o644); err != nil {
		t.Fatal(err)
	}
	t.Logf("wrote %d stamp(s) to %s", len(all), path)
}
