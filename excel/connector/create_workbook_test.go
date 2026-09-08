package connector

import (
	"context"
	"encoding/json"
	"strings"
	"testing"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub"
)

// The full Graph wiring (a real hub.Host with a store, an encryption key
// and a fake Graph server) is exercised in hub/create_workbook_test.go, not
// here: this package cannot import hub/internal/store or hub/internal/graph
// (Go's internal rule — see connector_test.go's fakeFiles comment), so it
// can only reach a Host with no Graph configured. What belongs here is the
// tool's own logic: building the .xlsx, the unique filename, the doc_key
// construction, and how a Host error surfaces through the MCP call.

func TestBuildWorkbookBlankWhenNoData(t *testing.T) {
	b, err := buildWorkbook(nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(b) == 0 {
		t.Fatal("blank workbook is empty")
	}
}

func TestBuildWorkbookFromData(t *testing.T) {
	b, err := buildWorkbook([]CreateWorkbookSheet{
		{Name: "Data", Rows: [][]any{{"Item", "Qty"}, {"Bolt", 4.0}}},
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(b) == 0 {
		t.Fatal("from-data workbook is empty")
	}
}

func TestUniqueWorkbookName(t *testing.T) {
	n1, err := uniqueWorkbookName("Budget")
	if err != nil {
		t.Fatal(err)
	}
	n2, err := uniqueWorkbookName("Budget")
	if err != nil {
		t.Fatal(err)
	}
	if n1 == n2 {
		t.Fatalf("two calls produced the same name: %q", n1)
	}
	if !strings.HasPrefix(n1, "Budget-") || !strings.HasSuffix(n1, ".xlsx") {
		t.Fatalf("name shape: %q", n1)
	}
	// A hint that already carries .xlsx isn't doubled.
	if strings.Count(n1, ".xlsx") != 1 {
		t.Fatalf("extension doubled: %q", n1)
	}
	empty, err := uniqueWorkbookName("")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(empty, "Workbook-") {
		t.Fatalf("default hint: %q", empty)
	}
}

// TestDocKeyConstruction covers the live finding (2026-09-08): a file's
// doc_key includes its OneDrive folder path when it has one — the RFC §3.3
// spike's original folderless result only held for a root-level file.
func TestDocKeyConstruction(t *testing.T) {
	cases := []struct {
		name string
		item hub.DriveItem
		want string
	}{
		{
			name: "in a folder",
			item: hub.DriveItem{ID: "953169F03C1B112C!123", Name: "Spike Test-1ec63325.xlsx", WebURL: "https://onedrive.live.com/x", DriveID: "953169F03C1B112C", Folder: "Eichler Connectors"},
			want: "https://d.docs.live.net/953169F03C1B112C/Eichler Connectors/Spike Test-1ec63325.xlsx",
		},
		{
			name: "root file, no folder segment",
			item: hub.DriveItem{ID: "953169F03C1B112C!456", Name: "Budget-ab12cd.xlsx", DriveID: "953169F03C1B112C", Folder: ""},
			want: "https://d.docs.live.net/953169F03C1B112C/Budget-ab12cd.xlsx",
		},
		{
			name: "nested folder",
			item: hub.DriveItem{DriveID: "CID", Folder: "A/B", Name: "n.xlsx"},
			want: "https://d.docs.live.net/CID/A/B/n.xlsx",
		},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := docKeyFor(c.item); got != c.want {
				t.Errorf("docKeyFor() = %q, want %q", got, c.want)
			}
		})
	}
}

// TestCreateWorkbookGraphUnconfigured drives the tool end to end over MCP
// with the package's ordinary fixture (no Graph wired — see newFixture in
// connector_test.go), checking the Host's graph-unconfigured diagnostic
// reaches the tool's error output unchanged.
func TestCreateWorkbookGraphUnconfigured(t *testing.T) {
	f := newFixture(t)
	res, err := f.cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "create_workbook", Arguments: map[string]any{"name": "Budget"}})
	if err != nil {
		t.Fatal(err)
	}
	if !res.IsError {
		t.Fatalf("want an error result with no Graph configured, got %+v", res)
	}
	var out CreateWorkbookOut
	b, _ := json.Marshal(res.StructuredContent)
	if err := json.Unmarshal(b, &out); err != nil {
		t.Fatalf("structured output: %v (%s)", err, b)
	}
	if out.Error == nil || out.Error.Code != "graph-unconfigured" {
		t.Fatalf("error: %+v", out.Error)
	}
}
