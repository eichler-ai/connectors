//go:build harness

package harness_test

import (
	"strings"
	"testing"
	"time"
)

type pluginOut struct {
	Name    string `json:"name"`
	Version string `json:"version"`
}

type searchPluginsOut struct {
	Packages []pluginOut `json:"packages"`
	Guidance string      `json:"guidance"`
}

type listPluginsOut struct {
	PackageDirectory string      `json:"package_directory"`
	Packages         []pluginOut `json:"packages"`
}

type pluginActionOut struct {
	Status  string `json:"status"`
	Name    string `json:"name"`
	Version string `json:"version"`
	Output  string `json:"output"`
	Notice  string `json:"notice"`
}

// TestPluginsLive exercises the Yak-backed package tools end to end through the MCP server against the
// real yak CLI (PRD §10). Read-only and gating only: it never installs or uninstalls, so it does not
// mutate the user's Rhino package folder. Needs no Rhino instance -- the tools shell out to yak, which
// operates on the per-user package folder independent of a running Rhino.
func TestPluginsLive(t *testing.T) {
	c := startServer(t)

	// list_plugins: the connector's own dev package is installed, so there is at least a directory.
	listRaw, err := c.CallTool("list_plugins", map[string]any{}, 30*time.Second)
	if err != nil {
		t.Fatalf("list_plugins: %v", err)
	}
	list := decodeToolResult[listPluginsOut](t, listRaw)
	t.Logf("list_plugins: dir=%q count=%d", list.PackageDirectory, len(list.Packages))
	if list.PackageDirectory == "" {
		t.Errorf("list_plugins should report the package directory")
	}

	// search_plugins: a well-known package resolves with a version.
	searchRaw, err := c.CallTool("search_plugins", map[string]any{"query": "lunchbox"}, 30*time.Second)
	if err != nil {
		t.Fatalf("search_plugins: %v", err)
	}
	search := decodeToolResult[searchPluginsOut](t, searchRaw)
	t.Logf("search 'lunchbox': %d result(s)", len(search.Packages))
	found := false
	for _, p := range search.Packages {
		if strings.EqualFold(p.Name, "LunchBox") && p.Version != "" {
			found = true
		}
	}
	if !found {
		t.Errorf("search_plugins should find LunchBox with a version, got %+v", search.Packages)
	}

	// install_plugin without confirm_lifecycle_actions must preview and install NOTHING.
	prevRaw, err := c.CallTool("install_plugin", map[string]any{"name": "LunchBox"}, 30*time.Second)
	if err != nil {
		t.Fatalf("install_plugin (preview): %v", err)
	}
	prev := decodeToolResult[pluginActionOut](t, prevRaw)
	t.Logf("install preview: status=%s notice=%q", prev.Status, prev.Notice)
	if prev.Status != "preview" {
		t.Errorf("install_plugin without confirm should return status=preview, got %q", prev.Status)
	}
	if !strings.Contains(prev.Notice, "Restart Rhino") {
		t.Errorf("install preview should warn that Rhino must restart to load, got %q", prev.Notice)
	}
}
