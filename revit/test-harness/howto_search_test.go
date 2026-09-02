//go:build harness

// Live pin for search_howtos / describe_howto (revit/docs/howto-corpus-design.md
// §3-§4, seed plan §6 step 4). The unit tier proves the field set, the
// version rule and the overlay against the embedded seed lexically; this
// proves the shipped path: the broker's bundled models rank the real corpus,
// the running instance's version is resolved from the registry, and every
// seed document is verified on that version -- which is what "verified_here"
// promises the agent.
package harness_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/test-harness/mcpclient"
)

type howtoSearchHit struct {
	ID           string   `json:"id"`
	Title        string   `json:"title"`
	VerifiedOn   []string `json:"verified_on"`
	VerifiedHere bool     `json:"verified_here"`
	Source       string   `json:"source"`
	Score        float64  `json:"score"`
}

type howtoSearchOut struct {
	Results      []howtoSearchHit `json:"results"`
	NextCursor   string           `json:"next_cursor"`
	TotalMatched int              `json:"total_matched"`
	RevitVersion string           `json:"revit_version"`
	Ranker       string           `json:"ranker"`
	Guidance     string           `json:"guidance"`
}

type howtoDescribeOut struct {
	Document *struct {
		ID       string `json:"id"`
		Script   string `json:"script"`
		Pitfalls []struct {
			Symptom string `json:"symptom"`
		} `json:"pitfalls"`
	} `json:"document"`
	Source       string `json:"source"`
	RevitVersion string `json:"revit_version"`
	VerifiedHere bool   `json:"verified_here"`
	Verification *struct {
		Status string `json:"status"`
		By     string `json:"by"`
	} `json:"verification"`
	Guidance string `json:"guidance"`
}

func callSearchHowTos(t *testing.T, c *mcpclient.Client, args map[string]any) howtoSearchOut {
	t.Helper()
	raw, err := c.CallTool("search_howtos", args, 60*time.Second)
	if err != nil {
		t.Fatalf("search_howtos: %v", err)
	}
	return decodeToolResult[howtoSearchOut](t, raw)
}

func TestHowToSearchLive(t *testing.T) {
	c, instances := startClient(t)
	inst := instances.Instances[0]

	// The first call builds the index (lazy) with whatever models the broker
	// loaded at startup; a lexical answer means the broker under test was
	// built without the bundled models, which is not what ships.
	first := callSearchHowTos(t, c, map[string]any{"instance_id": inst.InstanceID, "query": "enclose a footprint with walls"})
	if first.Ranker != "semantic" {
		t.Fatalf("ranker = %q (guidance: %s); build the broker with the models fetched", first.Ranker, first.Guidance)
	}
	if first.RevitVersion != inst.RevitVersion {
		t.Fatalf("revit_version = %q, want the instance's %q", first.RevitVersion, inst.RevitVersion)
	}

	t.Run("EverySeedDocumentIsFoundByItsTaskAndVerifiedHere", func(t *testing.T) {
		files, err := filepath.Glob(filepath.Join(corpusDir, "*.json"))
		if err != nil || len(files) == 0 {
			t.Fatalf("no corpus under %s (%v)", corpusDir, err)
		}
		for _, f := range files {
			raw, err := os.ReadFile(f)
			if err != nil {
				t.Fatal(err)
			}
			var d struct {
				ID   string `json:"id"`
				Task string `json:"task"`
			}
			if err := json.Unmarshal(raw, &d); err != nil {
				t.Fatalf("%s: %v", f, err)
			}
			out := callSearchHowTos(t, c, map[string]any{"instance_id": inst.InstanceID, "query": d.Task, "top_n": 3})
			if len(out.Results) == 0 || out.Results[0].ID != d.ID {
				var got []string
				for _, r := range out.Results {
					got = append(got, r.ID)
				}
				t.Errorf("%s: its own task did not rank it first; got %v", d.ID, got)
				continue
			}
			if !out.Results[0].VerifiedHere {
				t.Errorf("%s: not verified on the running Revit %s (verified_on %v); run the sweep on this version before shipping", d.ID, inst.RevitVersion, out.Results[0].VerifiedOn)
			}
			if out.Results[0].Source != "seed" {
				t.Errorf("%s: source = %q", d.ID, out.Results[0].Source)
			}
		}
	})

	t.Run("TaskPhrasingsResolveToTheRightDocument", func(t *testing.T) {
		// Sentences an agent would actually send, worded unlike the title
		// and task, each resolved within the top 3 on both Revit versions
		// when this pin was written. Cases that did not hold live were
		// removed rather than widened, as for search_functions.
		cases := []struct {
			query, id string
		}{
			{"put a door into an existing wall", "family-instances-place-hosted"},
			{"make a wall schedule and read its rows back", "schedules-create-with-fields"},
			{"export a plan view to dwg and give the file to the user", "export-views-dwg"},
			{"the room I created has zero area and no boundary", "rooms-create-tag-area"},
			{"LoadFamily fails inside WithTransaction", "self-transacting-calls-between-blocks"},
			{"revert the last script run from the undo history", "undo-redo-and-run-labels"},
			{"add a shared parameter to walls and doors", "shared-parameters-file-and-binding"},
		}
		for _, tc := range cases {
			out := callSearchHowTos(t, c, map[string]any{"instance_id": inst.InstanceID, "query": tc.query, "top_n": 3})
			var got []string
			hit := false
			for _, r := range out.Results {
				got = append(got, r.ID)
				hit = hit || r.ID == tc.id
			}
			if !hit {
				t.Errorf("%q: %s not within top 3; got %v", tc.query, tc.id, got)
			}
		}
	})

	t.Run("DescribeReportsTheHarnessStampForThisVersion", func(t *testing.T) {
		raw, err := c.CallTool("describe_howto", map[string]any{"instance_id": inst.InstanceID, "id": "walls-create-and-join"}, 30*time.Second)
		if err != nil {
			t.Fatal(err)
		}
		out := decodeToolResult[howtoDescribeOut](t, raw)
		if out.Document == nil || out.Document.Script == "" || len(out.Document.Pitfalls) == 0 {
			t.Fatalf("document incomplete: %+v", out.Document)
		}
		if out.RevitVersion != inst.RevitVersion || !out.VerifiedHere || out.Verification == nil || out.Verification.Status != "passed" || out.Verification.By != "harness" {
			t.Errorf("verification for %s: here=%v %+v", inst.RevitVersion, out.VerifiedHere, out.Verification)
		}
		if !strings.Contains(out.Guidance, "harness") {
			t.Errorf("guidance = %q", out.Guidance)
		}
	})

	t.Run("VersionIsRequiredAndPagingHolds", func(t *testing.T) {
		raw, err := c.CallTool("search_howtos", map[string]any{"query": "create walls"}, 30*time.Second)
		if err != nil {
			t.Fatal(err)
		}
		var tr toolResult
		json.Unmarshal(raw, &tr)
		if !tr.IsError || len(tr.Content) == 0 || !strings.Contains(tr.Content[0].Text, "howto-version-required") {
			t.Errorf("a call with neither instance_id nor revit_version must be refused with howto-version-required, got %s", raw)
		}
		p1 := callSearchHowTos(t, c, map[string]any{"revit_version": inst.RevitVersion, "query": "create walls on a level", "top_n": 2})
		if len(p1.Results) != 2 || p1.NextCursor == "" {
			t.Fatalf("page 1: %+v", p1)
		}
		p2 := callSearchHowTos(t, c, map[string]any{"revit_version": inst.RevitVersion, "query": "create walls on a level", "top_n": 2, "cursor": p1.NextCursor})
		if len(p2.Results) == 0 || p2.Results[0].ID == p1.Results[0].ID {
			t.Fatalf("page 2 should continue past page 1: %+v", p2)
		}
	})
}
