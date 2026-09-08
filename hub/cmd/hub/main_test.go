package main

import "testing"

// version prefers the link-time buildVersion (set by Cloud Build, which has
// no VCS stamp to fall back on) over debug.ReadBuildInfo.
func TestVersionPrefersBuildVersion(t *testing.T) {
	t.Cleanup(func() { buildVersion = "" })

	buildVersion = "abc123def456"
	if got := version(); got != "abc123def456" {
		t.Errorf("version() = %q, want the linked buildVersion", got)
	}
}
