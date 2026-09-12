//go:build harness

package harness_test

import (
	"strings"
	"testing"
	"time"
)

type packageOut struct {
	Name    string `json:"name"`
	Version string `json:"version"`
}

type searchPackagesOut struct {
	Packages []packageOut `json:"packages"`
	Guidance string       `json:"guidance"`
}

type listPackagesOut struct {
	PackageDirectory string       `json:"package_directory"`
	Packages         []packageOut `json:"packages"`
}

type packageActionOut struct {
	Status  string `json:"status"`
	Name    string `json:"name"`
	Version string `json:"version"`
	Output  string `json:"output"`
	Notice  string `json:"notice"`
}

// TestPackagesLive exercises the Yak-backed package tools end to end through the MCP server against the
// real yak CLI (PRD §10). Read-only and gating only: it never installs or uninstalls, so it does not
// mutate the user's Rhino package folder. Needs no Rhino instance -- the tools shell out to yak, which
// operates on the per-user package folder independent of a running Rhino.
func TestPackagesLive(t *testing.T) {
	c := startServer(t)

	// list_packages: the connector's own dev package is installed, so there is at least a directory.
	listRaw, err := c.CallTool("list_packages", map[string]any{}, 30*time.Second)
	if err != nil {
		t.Fatalf("list_packages: %v", err)
	}
	list := decodeToolResult[listPackagesOut](t, listRaw)
	t.Logf("list_packages: dir=%q count=%d", list.PackageDirectory, len(list.Packages))
	if list.PackageDirectory == "" {
		t.Errorf("list_packages should report the package directory")
	}

	// search_packages: a well-known package resolves with a version.
	searchRaw, err := c.CallTool("search_packages", map[string]any{"query": "lunchbox"}, 30*time.Second)
	if err != nil {
		t.Fatalf("search_packages: %v", err)
	}
	search := decodeToolResult[searchPackagesOut](t, searchRaw)
	t.Logf("search 'lunchbox': %d result(s)", len(search.Packages))
	found := false
	for _, p := range search.Packages {
		if strings.EqualFold(p.Name, "LunchBox") && p.Version != "" {
			found = true
		}
	}
	if !found {
		t.Errorf("search_packages should find LunchBox with a version, got %+v", search.Packages)
	}

	// install_package without confirm_lifecycle_actions must preview and install NOTHING.
	prevRaw, err := c.CallTool("install_package", map[string]any{"name": "LunchBox"}, 30*time.Second)
	if err != nil {
		t.Fatalf("install_package (preview): %v", err)
	}
	prev := decodeToolResult[packageActionOut](t, prevRaw)
	t.Logf("install preview: status=%s notice=%q", prev.Status, prev.Notice)
	if prev.Status != "preview" {
		t.Errorf("install_package without confirm should return status=preview, got %q", prev.Status)
	}
	if !strings.Contains(prev.Notice, "Restart Rhino") {
		t.Errorf("install preview should warn that Rhino must restart to load, got %q", prev.Notice)
	}
}
