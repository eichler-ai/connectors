package connector

import (
	"context"
	"encoding/json"
	"fmt"
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

func TestDocKeyConstruction(t *testing.T) {
	item := hub.DriveItem{ID: "953169F03C1B112C!123", Name: "Budget-ab12cd.xlsx", WebURL: "https://onedrive.live.com/x", DriveID: "953169F03C1B112C"}
	got := fmt.Sprintf(dDocsLiveTemplate, item.DriveID, item.Name)
	want := "https://d.docs.live.net/953169F03C1B112C/Budget-ab12cd.xlsx"
	if got != want {
		t.Fatalf("doc_key = %q, want %q", got, want)
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
