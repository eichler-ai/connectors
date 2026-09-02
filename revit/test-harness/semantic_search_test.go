//go:build harness

// Live pin for the broker-side search_functions ranker (issue #107,
// revit/docs/search-ranking-redesign.md §10). The unit tiers prove the
// pipeline against fixtures and the POC corpus snapshot; this proves the
// whole path against a running add-in: dump_members pages the real corpus,
// the broker builds its index in the seconds after the instance attaches,
// and task-style sentences -- the input the redesign exists for -- resolve
// to the members that answer them.
package harness_test

import (
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/test-harness/mcpclient"
)

type semanticSearchResult struct {
	Results []struct {
		MemberID      string  `json:"member_id"`
		Namespace     string  `json:"namespace"`
		DeclaringType string  `json:"declaring_type"`
		Name          string  `json:"name"`
		Summary       string  `json:"summary"`
		Score         float64 `json:"score"`
	} `json:"results"`
	NextCursor   string `json:"next_cursor"`
	TotalMatched int    `json:"total_matched"`
	Ranker       string `json:"ranker"`
	Guidance     string `json:"guidance"`
	RevitVersion string `json:"revit_version"`
}

func callSearchFunctions(t *testing.T, c *mcpclient.Client, args map[string]any) semanticSearchResult {
	t.Helper()
	raw, err := c.CallTool("search_functions", args, 30*time.Second)
	if err != nil {
		t.Fatalf("search_functions: %v", err)
	}
	return decodeToolResult[semanticSearchResult](t, raw)
}

// waitForSemanticRanker polls until the broker answers from its own index.
// The build starts when the instance registers and takes a few seconds for
// the real corpus (~16 dump_members pages, then embedding); 90s is generous.
func waitForSemanticRanker(t *testing.T, c *mcpclient.Client, instanceID string) semanticSearchResult {
	t.Helper()
	deadline := time.Now().Add(90 * time.Second)
	var last semanticSearchResult
	for time.Now().Before(deadline) {
		last = callSearchFunctions(t, c, map[string]any{"instance_id": instanceID, "query": "delete an element from the document", "top_n": 5})
		switch last.Ranker {
		case "semantic":
			return last
		case "lexical":
			t.Fatalf("broker index is lexical-only: the broker under test was built without the bundled models (guidance: %s)", last.Guidance)
		}
		time.Sleep(2 * time.Second)
	}
	t.Fatalf("broker index never became ready; last ranker=%q guidance=%q", last.Ranker, last.Guidance)
	return last
}

func TestSemanticSearchAnswersTaskSentences(t *testing.T) {
	c, instances := startClient(t)
	instanceID := instances.Instances[0].InstanceID

	first := waitForSemanticRanker(t, c, instanceID)
	if !strings.Contains(first.Guidance, "cross-encoder") {
		t.Errorf("semantic guidance should name the mechanism, got %q", first.Guidance)
	}

	// Each case: a task sentence and the member that answers it, all labelled
	// in the POC set (scratchpad labels_big.json) and resolved by the full
	// pipeline there at rank <= 3. "within" rather than "rank 1" because the
	// live corpus includes whatever add-ins this Revit has loaded.
	cases := []struct {
		query  string
		member string // Type.Member suffix of member_id
		within int
	}{
		{"move an element to a new location", "ElementTransformUtils.MoveElement", 3},
		{"delete an element from the document", "Document.Delete", 3},
		{"get the parameter of an element by its name", "Element.LookupParameter", 5},
		{"find every element of a given class in the document", "FilteredElementCollector.OfClass", 5},
	}
	for _, tc := range cases {
		t.Run(tc.member, func(t *testing.T) {
			out := callSearchFunctions(t, c, map[string]any{"instance_id": instanceID, "query": tc.query, "top_n": tc.within})
			if out.Ranker != "semantic" {
				t.Fatalf("ranker = %q", out.Ranker)
			}
			var got []string
			for _, r := range out.Results {
				got = append(got, r.DeclaringType+"."+r.Name)
				if strings.HasSuffix(r.DeclaringType+"."+r.Name, tc.member) {
					return
				}
			}
			t.Errorf("%q: %s not within top %d; got %v", tc.query, tc.member, tc.within, got)
		})
	}

	t.Run("NamespaceScopeAndPagingHoldLive", func(t *testing.T) {
		page1 := callSearchFunctions(t, c, map[string]any{"instance_id": instanceID, "query": "show elements in the active view", "namespace": "Autodesk.Revit.UI", "top_n": 3})
		if len(page1.Results) == 0 || page1.NextCursor == "" {
			t.Fatalf("expected a scoped first page with more to come, got %+v", page1)
		}
		for _, r := range page1.Results {
			if r.Namespace != "Autodesk.Revit.UI" {
				t.Errorf("namespace mask leaked %s.%s", r.Namespace, r.Name)
			}
		}
		page2 := callSearchFunctions(t, c, map[string]any{"instance_id": instanceID, "query": "show elements in the active view", "namespace": "Autodesk.Revit.UI", "top_n": 3, "cursor": page1.NextCursor})
		if len(page2.Results) == 0 || page2.Results[0].MemberID == page1.Results[0].MemberID {
			t.Fatalf("page 2 should continue past page 1, got %+v", page2)
		}
	})

	t.Run("JunkEnumsAreMaskedFromSearch", func(t *testing.T) {
		out := callSearchFunctions(t, c, map[string]any{"instance_id": instanceID, "query": "walls category", "top_n": 20})
		for _, r := range out.Results {
			if strings.HasSuffix(r.DeclaringType, ".BuiltInCategory") || strings.HasSuffix(r.DeclaringType, ".BuiltInParameter") {
				t.Errorf("junk member surfaced: %s", r.MemberID)
			}
		}
		_ = json.Valid
	})
}
