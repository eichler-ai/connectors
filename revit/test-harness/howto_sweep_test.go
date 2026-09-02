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

	"github.com/eichler-ai/connectors/revit/test-harness/mcpclient"
)

// The harness is its own module and cannot import the broker's internal
// howto package, so the slice of the document and stamp shapes it needs is
// mirrored here (schema: revit/mcp-server/internal/howto/schema/). The
// broker's own tests validate the files; this reads them.
type howtoDoc struct {
	ID     string `json:"id"`
	Rev    int    `json:"rev"`
	Script string `json:"script"`
	Verify *struct {
		Mutations *struct {
			NetCreated     *int `json:"net_created"`
			NetModified    *int `json:"net_modified"`
			NetDeleted     *int `json:"net_deleted"`
			NetModifiedMin *int `json:"net_modified_min"`
			ByCategory     map[string]struct {
				NetCreated  *int `json:"net_created"`
				NetModified *int `json:"net_modified"`
			} `json:"by_category"`
		} `json:"mutations"`
		Execute          map[string]any `json:"execute"`
		CreatesDocuments bool           `json:"creates_documents"`
	} `json:"verify"`
}

type howtoStamp struct {
	ID           string `json:"id"`
	Rev          int    `json:"rev"`
	ScriptSHA256 string `json:"script_sha256"`
	RevitVersion string `json:"revit_version"`
	Status       string `json:"status"`
	At           string `json:"at"`
	By           string `json:"by"`
	Diagnostic   string `json:"diagnostic,omitempty"`
}

func scriptSHA256(script string) string {
	sum := sha256.Sum256([]byte(script))
	return hex.EncodeToString(sum[:])
}

func listInstances(t *testing.T, c *mcpclient.Client) listInstancesOut {
	t.Helper()
	raw, err := c.CallTool("list_instances", map[string]any{}, 10*time.Second)
	if err != nil {
		t.Fatalf("list_instances: %v", err)
	}
	return decodeToolResult[listInstancesOut](t, raw)
}

var (
	howtoStamps = flag.Bool("howto-stamps", false, "TestHowToSweep: write a harness stamp per document into corpus/verified.jsonl (replacing any earlier stamp for the same id, rev, script hash and Revit version)")
	howtoOnly   = flag.String("howto-only", "", "TestHowToSweep: comma-separated document ids to run (default: every document)")
)

// corpusDir is the shared corpus in this checkout, relative to this module.
const corpusDir = "../mcp-server/internal/howto/corpus"

// TestHowToSweep is the tier-2 verification of the how-to corpus (design note
// §3): every document's script runs, as shipped, against a blank fixture
// document on the connected Revit, and must succeed and produce the net
// mutations its `verify` block promises. With -howto-stamps the result is
// recorded in the sidecar, keyed by the exact script hash, which is what
// describe_howto later shows as "verified on <version>".
//
// The script runs routed at the fixture by document_id, so `Document` inside
// it IS the fixture -- exactly how an agent's own run binds `doc`. Nothing is
// substituted into the script text: the hash stamped is the hash served.
//
// Run ONE sweep at a time against a Revit instance. Executions serialise on
// Revit's UI thread, and the harness's stale-execution recovery cancels an
// in-flight run it did not start.
func TestHowToSweep(t *testing.T) {
	c, instanceID, mainDocumentID := targetDocument(t)
	var revitVersion string
	for _, inst := range listInstances(t, c).Instances {
		if inst.InstanceID == instanceID {
			revitVersion = inst.RevitVersion
		}
	}
	if revitVersion == "" {
		t.Fatalf("instance %s reports no revit_version; a stamp needs one", instanceID)
	}
	only := map[string]bool{}
	for _, id := range strings.Split(*howtoOnly, ",") {
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
		t.Run(d.ID, func(t *testing.T) {
			stamp := howtoStamp{ID: d.ID, Rev: d.Rev, ScriptSHA256: scriptSHA256(d.Script), RevitVersion: revitVersion, At: time.Now().UTC().Format(time.RFC3339), By: "harness"}
			diag := sweepOne(t, c, instanceID, mainDocumentID, &d)
			if diag == "" {
				stamp.Status = "passed"
			} else {
				stamp.Status, stamp.Diagnostic = "failed", truncate(diag, 1000)
				t.Errorf("%s: %s", d.ID, diag)
			}
			results = append(results, stamp)
		})
	}
	if *howtoStamps && len(results) > 0 {
		writeStamps(t, results)
	}
}

// sweepOne runs one document and returns "" on success or the failure
// diagnostic. Runs at a fresh blank fixture so documents cannot see each
// other's elements and every count is the document's own.
func sweepOne(t *testing.T, c *mcpclient.Client, instanceID, mainDocumentID string, d *howtoDoc) string {
	t.Helper()
	if d.Script == "" {
		return "document has no script"
	}
	title := createBlankFixtureDocument(t, c, instanceID, mainDocumentID)
	fixtureID := ""
	for _, inst := range listInstances(t, c).Instances {
		for _, doc := range inst.Documents {
			if inst.InstanceID == instanceID && doc.Title == title {
				fixtureID = doc.DocumentID
			}
		}
	}
	if fixtureID == "" {
		return "fixture document " + title + " not listed by list_instances"
	}
	// No ensureInstanceIdle here, deliberately: it cancels whatever execution
	// it finds in flight, which is right for one session's own stale run and
	// destructive when a second sweep shares the instance (three concurrent
	// sweeps cancelled each other until the instance went unrecoverable).
	// The sweep is serial by design; a busy instance fails the document loudly.
	var extra map[string]any
	if d.Verify != nil {
		extra = d.Verify.Execute
	}
	raw := callExecuteScriptWith(t, c, instanceID, fixtureID, d.Script, extra)
	var env toolResult
	if err := json.Unmarshal(raw, &env); err != nil {
		return "undecodable execute_script result: " + err.Error()
	}
	if env.IsError {
		return "rejected: " + rejectionOf(t, raw).Text
	}
	var out executeScriptOut
	if err := json.Unmarshal(env.StructuredContent, &out); err != nil {
		return "undecodable structured result: " + err.Error()
	}
	if d.Verify != nil && d.Verify.CreatesDocuments {
		registerCreatedDocumentCleanup(t, c, instanceID, mainDocumentID, out.Output)
	}
	if out.Status != "success" {
		return fmt.Sprintf("status=%s %s", out.Status, out.diag())
	}
	// Always log the report: it is the evidence a document's verify block is
	// pinned from, and the only way to see what a passing run actually did.
	if rep, err := json.Marshal(out.Mutations); err == nil {
		t.Logf("%s mutations: %s", d.ID, rep)
	}
	if d.Verify == nil || d.Verify.Mutations == nil {
		return ""
	}
	want := d.Verify.Mutations
	got := struct{ Created, Modified, Deleted int }{}
	byCat := map[string]struct{ Created, Modified int }{}
	if out.Mutations != nil {
		got.Created, got.Modified, got.Deleted = out.Mutations.Created, out.Mutations.Modified, out.Mutations.Deleted
		for k, v := range out.Mutations.ByCategory {
			byCat[k] = struct{ Created, Modified int }{v.Created, v.Modified}
		}
	}
	var problems []string
	check := func(name string, want *int, got int) {
		if want != nil && *want != got {
			problems = append(problems, fmt.Sprintf("%s: want %d, got %d", name, *want, got))
		}
	}
	check("net_created", want.NetCreated, got.Created)
	check("net_modified", want.NetModified, got.Modified)
	check("net_deleted", want.NetDeleted, got.Deleted)
	if want.NetModifiedMin != nil && got.Modified < *want.NetModifiedMin {
		problems = append(problems, fmt.Sprintf("net_modified: want >= %d, got %d", *want.NetModifiedMin, got.Modified))
	}
	for cat, w := range want.ByCategory {
		g, ok := byCat[cat]
		if !ok {
			problems = append(problems, fmt.Sprintf("by_category[%s]: absent from the report (have %v)", cat, keys(byCat)))
			continue
		}
		check("by_category["+cat+"].net_created", w.NetCreated, g.Created)
		check("by_category["+cat+"].net_modified", w.NetModified, g.Modified)
	}
	if len(problems) == 0 {
		return ""
	}
	return "mutations: " + strings.Join(problems, "; ") + " -- " + out.diag()
}

func keys[V any](m map[string]V) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n-1] + "…"
}

// writeStamps merges this run's stamps into the sidecar: an earlier stamp
// for the same id, rev, script hash and Revit version is replaced, anything
// else is kept, and the file is rewritten in id order.
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
	key := func(s howtoStamp) string {
		return s.ID + "|" + fmt.Sprint(s.Rev) + "|" + s.ScriptSHA256 + "|" + s.RevitVersion
	}
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
		return all[i].RevitVersion < all[j].RevitVersion
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
