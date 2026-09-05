package mcpserver

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
)

func updateDepsForTest(t *testing.T, mode string, marker *InstalledMarker, latest string, checkErr error) (UpdateDeps, *[]string) {
	t.Helper()
	dir := t.TempDir()
	if marker != nil {
		// A real install.ps1 self-copy must exist for apply; the marker itself is injected.
		if err := os.WriteFile(filepath.Join(dir, "install.ps1"), []byte("# installer"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	launches := &[]string{}
	return UpdateDeps{
		Mode:    mode,
		Version: "v0.1.2",
		CheckNow: func(context.Context) (string, error) {
			if checkErr != nil {
				return "", checkErr
			}
			return latest, nil
		},
		ReadMarker: func() (*InstalledMarker, string, error) { return marker, dir, nil },
		Launch: func(script, scope string) error {
			*launches = append(*launches, script+" "+scope)
			return nil
		},
	}, launches
}

func TestUpdateConnectorCheckReportsEveryRevitVersionSeparately(t *testing.T) {
	reg := registry.New()
	reg.Register(&registry.Instance{InstanceID: "a", RevitVersion: "2027", PID: 1}, time.Now())
	reg.Register(&registry.Instance{InstanceID: "b", RevitVersion: "2027", PID: 2}, time.Now())
	marker := &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2025", "2027"}, Skipped: []string{"2026"}}
	deps, launches := updateDepsForTest(t, "local", marker, "v0.1.3", nil)
	deps.Registry = reg

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{})

	if out.Error != nil {
		t.Fatalf("unexpected error: %+v", out.Error)
	}
	if out.Latest != "v0.1.3" || !out.Server.UpdateAvailable || out.Server.Running != "v0.1.2" || out.Server.Installed != "v0.1.2" {
		t.Fatalf("server status wrong: %+v latest=%q", out.Server, out.Latest)
	}
	byVersion := map[string]UpdateRevitStatus{}
	for _, r := range out.Revit {
		byVersion[r.Version] = r
	}
	if len(byVersion) != 3 {
		t.Fatalf("expected 2025, 2026 and 2027, got %+v", out.Revit)
	}
	if r := byVersion["2027"]; r.State != "deployed" || r.ConnectedInstances != 2 || !r.UpdateAvailable || r.AddinInstalled != "v0.1.2" {
		t.Errorf("2027: %+v", r)
	}
	if r := byVersion["2025"]; r.State != "deployed" || r.ConnectedInstances != 0 || !r.UpdateAvailable {
		t.Errorf("2025: %+v", r)
	}
	if r := byVersion["2026"]; r.State != "skipped" || r.UpdateAvailable {
		t.Errorf("2026 (no payload shipped for it): %+v", r)
	}
	if out.Applied || len(*launches) != 0 {
		t.Errorf("a check must not launch anything: applied=%v launches=%v", out.Applied, *launches)
	}
}

func TestUpdateConnectorCheckIsCurrentWhenVersionsMatch(t *testing.T) {
	marker := &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2027"}}
	deps, _ := updateDepsForTest(t, "local", marker, "v0.1.2", nil)

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{})

	if out.Server.UpdateAvailable || out.Revit[0].UpdateAvailable || out.Error != nil {
		t.Fatalf("expected everything current: %+v", out)
	}
}

func TestUpdateConnectorReportsAConnectedVersionTheMarkerDoesNotKnow(t *testing.T) {
	reg := registry.New()
	reg.Register(&registry.Instance{InstanceID: "a", RevitVersion: "2026", PID: 1}, time.Now())
	deps, _ := updateDepsForTest(t, "local", &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2027"}}, "v0.1.2", nil)
	deps.Registry = reg

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{})

	var found bool
	for _, r := range out.Revit {
		if r.Version == "2026" {
			found = r.State == "unknown" && r.ConnectedInstances == 1
		}
	}
	if !found {
		t.Fatalf("2026 (connected, not in the marker) should appear as unknown: %+v", out.Revit)
	}
}

func TestUpdateConnectorInstalledButServerStillOldGetsARestartNotice(t *testing.T) {
	// The v0.1.1 -> v0.1.2 live shape: marker already at latest, this process older.
	deps, _ := updateDepsForTest(t, "local", &InstalledMarker{Version: "v0.1.3", Deployed: []string{"2027"}}, "v0.1.3", nil)

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{})

	if !out.Server.UpdateAvailable || out.Revit[0].UpdateAvailable {
		t.Fatalf("server behind, add-in current expected: %+v", out)
	}
	if len(out.Notices) != 1 || out.Notices[0].Code != "server-restart-pending" {
		t.Fatalf("expected server-restart-pending notice, got %+v", out.Notices)
	}
}

func TestUpdateConnectorApplyRequiresConfirmationBeforeAnyWork(t *testing.T) {
	checked := false
	deps, launches := updateDepsForTest(t, "local", &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2027"}}, "v0.1.3", nil)
	deps.CheckNow = func(context.Context) (string, error) { checked = true; return "v0.1.3", nil }

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{Apply: true})

	if out.Error == nil || out.Error.Code != "update-requires-confirmation" {
		t.Fatalf("expected update-requires-confirmation, got %+v", out.Error)
	}
	if checked || len(*launches) != 0 || out.Applied {
		t.Fatalf("a refused apply must do no work: checked=%v launches=%v", checked, *launches)
	}
}

func TestUpdateConnectorApplyIsRefusedInRemoteMode(t *testing.T) {
	deps, launches := updateDepsForTest(t, "remote", &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2027"}}, "v0.1.3", nil)
	checked := 0
	deps.CheckNow = func(context.Context) (string, error) { checked++; return "v0.1.3", nil }

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{Apply: true, ConfirmLifecycleActions: true})

	if out.Error == nil || out.Error.Code != "update-not-available-in-remote-mode" || len(*launches) != 0 {
		t.Fatalf("expected remote-mode refusal: %+v launches=%v", out.Error, *launches)
	}
	if checked != 0 {
		t.Fatal("a refused apply must not have run the check (no broker.json write before the gate)")
	}
	// The read-only check still works in remote mode.
	check := updateConnector(context.Background(), deps, UpdateConnectorIn{})
	if check.Error != nil || check.Latest != "v0.1.3" {
		t.Fatalf("remote-mode check should work: %+v", check)
	}
}

func TestUpdateConnectorApplyLaunchesTheInstalledUpdaterWithScope(t *testing.T) {
	deps, launches := updateDepsForTest(t, "local", &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2025", "2027"}}, "v0.1.3", nil)

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{Apply: true, ConfirmLifecycleActions: true})

	if out.Error != nil || !out.Applied {
		t.Fatalf("expected applied: %+v", out)
	}
	if len(*launches) != 1 || filepath.Base((*launches)[0][:len((*launches)[0])-len(" User")]) != "install.ps1" || (*launches)[0][len((*launches)[0])-4:] != "User" {
		t.Fatalf("expected one launch of install.ps1 with scope User, got %v", *launches)
	}
	if len(out.Notices) != 1 || out.Notices[0].Code != "update-started" {
		t.Fatalf("expected update-started notice, got %+v", out.Notices)
	}
}

func TestUpdateConnectorApplyDoesNothingWhenAlreadyCurrent(t *testing.T) {
	deps, launches := updateDepsForTest(t, "local", &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2027"}}, "v0.1.2", nil)

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{Apply: true, ConfirmLifecycleActions: true})

	if out.Applied || len(*launches) != 0 || out.Error != nil {
		t.Fatalf("nothing should run when current: %+v launches=%v", out, *launches)
	}
	if len(out.Notices) != 1 || out.Notices[0].Code != "already-current" {
		t.Fatalf("expected already-current, got %+v", out.Notices)
	}
}

func TestUpdateConnectorApplyWithoutASelfCopyNamesTheOneLiner(t *testing.T) {
	deps, launches := updateDepsForTest(t, "local", &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2027"}}, "v0.1.3", nil)
	dir := t.TempDir() // no install.ps1 here
	deps.ReadMarker = func() (*InstalledMarker, string, error) {
		return &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2027"}}, dir, nil
	}

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{Apply: true, ConfirmLifecycleActions: true})

	if out.Error == nil || out.Error.Code != "installer-not-found" || len(*launches) != 0 {
		t.Fatalf("expected installer-not-found: %+v", out.Error)
	}
}

func TestUpdateConnectorCheckFailureStillReportsWhatIsKnown(t *testing.T) {
	deps, _ := updateDepsForTest(t, "local", &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2027"}, Skipped: []string{"2025"}}, "", errors.New("dial tcp: no route"))

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{})

	if out.Error == nil || out.Error.Code != "update-check-failed" || out.Error.Source != "mcp-server.internal.updatecheck" {
		t.Fatalf("expected update-check-failed from the updatecheck source, got %+v", out.Error)
	}
	if out.Server.Installed != "v0.1.2" || len(out.Revit) != 2 {
		t.Fatalf("marker-derived facts should still be reported: %+v", out)
	}
	for _, r := range out.Revit {
		// With no latest known, nothing can honestly be called "behind" (review of #200: it used to
		// be asserted unconditionally).
		if r.UpdateAvailable {
			t.Errorf("%s: update_available must be false when the check failed: %+v", r.Version, r)
		}
	}
}

func TestUpdateConnectorToolDecisionsCarryTheToolsOwnSource(t *testing.T) {
	deps, _ := updateDepsForTest(t, "local", &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2027"}}, "v0.1.3", nil)

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{Apply: true})

	if out.Error.Source != "mcp-server.internal.mcpserver.update" || len(out.Error.Remedy) == 0 {
		t.Fatalf("a gating record must name this package and carry a remedy: %+v", out.Error)
	}
}

func TestVersionBehind(t *testing.T) {
	cases := []struct {
		running, latest string
		want            bool
	}{
		{"v0.1.2", "v0.1.3", true},
		{"0.1.2", "v0.1.3", true},
		{"v0.1.3", "v0.1.3", false},
		{"V0.1.3", "v0.1.3", false},
		{"dev", "v0.1.3", false},
		{"", "v0.1.3", false},
		{"v0.1.2", "", false},
	}
	for _, c := range cases {
		if got := versionBehind(c.running, c.latest); got != c.want {
			t.Errorf("versionBehind(%q,%q)=%v want %v", c.running, c.latest, got, c.want)
		}
	}
}

func TestReadMarkerParsesInstallPs1Shape(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "installed-version.json")
	os.WriteFile(path, []byte(`{"components":{"server":"abc"},"version":"v0.1.2","deployed":["2025","2027"],"skipped":[],"howto_corpus":{"documents":23}}`), 0o644)

	m := readMarker(path)

	if m == nil || m.Version != "v0.1.2" || len(m.Deployed) != 2 || len(m.Skipped) != 0 {
		t.Fatalf("marker parsed wrong: %+v", m)
	}
	if readMarker(filepath.Join(dir, "missing.json")) != nil {
		t.Fatal("missing marker must read as nil")
	}
	os.WriteFile(path, []byte("{not json"), 0o644)
	if readMarker(path) != nil {
		t.Fatal("corrupt marker must read as nil")
	}
}

// The shim (self-update-architecture.md §4, §6.2): addin\current.json names what a Revit loads at
// its next start, and a Revit already open keeps the previous add-in until restarted.
func TestUpdateConnectorReportsThePointerAndARestartNoteOnlyWhenARestartResolvesSomething(t *testing.T) {
	byVersion := func(out UpdateConnectorOut) map[string]UpdateRevitStatus {
		m := map[string]UpdateRevitStatus{}
		for _, r := range out.Revit {
			m[r.Version] = r
		}
		return m
	}
	newDeps := func(t *testing.T, latest string) UpdateDeps {
		reg := registry.New()
		reg.Register(&registry.Instance{InstanceID: "a", RevitVersion: "2027", PID: 1}, time.Now())
		marker := &InstalledMarker{Version: "v0.1.3", Deployed: []string{"2025", "2027"}}
		deps, _ := updateDepsForTest(t, "local", marker, latest, nil)
		deps.Registry = reg
		deps.ReadAddinPointer = func(string) string { return "v0.1.3" }
		return deps
	}

	t.Run("steady state: installed == latest, nothing applied -> no note (review of #219: it used to tell users to restart for nothing)", func(t *testing.T) {
		out := updateConnector(context.Background(), newDeps(t, "v0.1.3"), UpdateConnectorIn{})

		if r := byVersion(out)["2027"]; r.AddinInstalled != "v0.1.3" || r.UpdateAvailable || r.Note != "" {
			t.Errorf("current, connected: installed from the pointer and NO note; got %+v", r)
		}
	})

	t.Run("an update is available -> the connected version gets the restart-after-applying note, the unconnected one none", func(t *testing.T) {
		out := updateConnector(context.Background(), newDeps(t, "v0.1.4"), UpdateConnectorIn{})

		r := byVersion(out)
		if x := r["2027"]; !x.UpdateAvailable || !strings.Contains(x.Note, "restart") || strings.Contains(x.Note, "close") {
			t.Errorf("behind + connected: a restart note and no talk of closing; got %+v", x)
		}
		if x := r["2025"]; x.Note != "" {
			t.Errorf("no connected Revit 2025, so nothing to restart: %+v", x)
		}
	})

	t.Run("just applied -> the connected version's note says installed, restart to load", func(t *testing.T) {
		out := updateConnector(context.Background(), newDeps(t, "v0.1.4"), UpdateConnectorIn{Apply: true, ConfirmLifecycleActions: true})

		if !out.Applied {
			t.Fatalf("expected applied: %+v", out)
		}
		r := byVersion(out)
		if x := r["2027"]; !strings.Contains(x.Note, "installed on disk") || !strings.Contains(x.Note, "restarted") {
			t.Errorf("applied + connected: installed-on-disk restart note; got %+v", x)
		}
		if x := r["2025"]; x.Note != "" {
			t.Errorf("no connected Revit 2025: %+v", x)
		}
	})
}

func TestUpdateConnectorShimPointerIsWhatANewRevitLoads(t *testing.T) {
	// The pointer, not the marker, is what a Revit started now loads, and the behind-ness follows it
	// (the marker is the whole install's record; the pointer is the add-in's own apply step).
	marker := &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2027"}}
	deps, _ := updateDepsForTest(t, "local", marker, "v0.1.3", nil)
	deps.ReadAddinPointer = func(string) string { return "v0.1.3" }

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{})

	if r := out.Revit[0]; r.AddinInstalled != "v0.1.3" || r.UpdateAvailable {
		t.Fatalf("pointer at latest must read as current: %+v", r)
	}
}

func TestUpdateConnectorApplyWordingNeverAsksRevitToClose(t *testing.T) {
	// The shim is the only add-in layout: an apply flips a pointer and closes nothing, and the wording
	// says so whether or not the pointer could be read (an unreadable pointer is a degraded read, not a
	// different layout with a different promise).
	for _, pointer := range []string{"v0.1.2", ""} {
		t.Run("pointer="+pointer, func(t *testing.T) {
			deps, launches := updateDepsForTest(t, "local", &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2027"}}, "v0.1.3", nil)
			deps.ReadAddinPointer = func(string) string { return pointer }

			out := updateConnector(context.Background(), deps, UpdateConnectorIn{Apply: true, ConfirmLifecycleActions: true})

			if out.Error != nil || !out.Applied || len(*launches) != 1 {
				t.Fatalf("expected one launch: %+v launches=%v", out, *launches)
			}
			if len(out.Notices) != 1 || out.Notices[0].Code != "update-started" {
				t.Fatalf("expected update-started, got %+v", out.Notices)
			}
			text := out.Notices[0].Message + " " + strings.Join(out.Notices[0].Remedy, " ")
			for _, s := range []string{"closes nothing", "restart"} {
				if !strings.Contains(text, s) {
					t.Errorf("update-started must say %q: %s", s, text)
				}
			}
			for _, s := range []string{"asked to close", "flat", "deferred"} {
				if strings.Contains(text, s) {
					t.Errorf("update-started must not say %q: %s", s, text)
				}
			}
		})
	}
}

func TestUpdateConnectorConfirmationGateSaysNothingIsClosed(t *testing.T) {
	deps, _ := updateDepsForTest(t, "local", &InstalledMarker{Version: "v0.1.2", Deployed: []string{"2027"}}, "v0.1.3", nil)

	out := updateConnector(context.Background(), deps, UpdateConnectorIn{Apply: true})

	for _, s := range []string{"nothing is closed", "next restart"} {
		if !strings.Contains(out.Error.Message, s) {
			t.Errorf("gate must mention %q: %s", s, out.Error.Message)
		}
	}
	for _, s := range []string{"asked to close", "flat"} {
		if strings.Contains(out.Error.Message, s) {
			t.Errorf("gate must not mention %q: %s", s, out.Error.Message)
		}
	}
}

func TestReadAddinPointerParsesTheShimPointer(t *testing.T) {
	dir := t.TempDir()
	if readAddinPointer(dir) != "" {
		t.Fatal("no addin\\current.json must read as empty (the caller falls back to the marker)")
	}
	if err := os.MkdirAll(filepath.Join(dir, "addin"), 0o755); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(dir, "addin", "current.json")
	os.WriteFile(path, []byte(`{"version":"v0.1.5","previous":"v0.1.4"}`), 0o644)
	if got := readAddinPointer(dir); got != "v0.1.5" {
		t.Fatalf("pointer version: got %q", got)
	}
	os.WriteFile(path, append([]byte{0xEF, 0xBB, 0xBF}, []byte(`{"version":" v0.1.6 "}`)...), 0o644)
	if got := readAddinPointer(dir); got != "v0.1.6" {
		t.Fatalf("BOM-prefixed, padded pointer must parse: got %q", got)
	}
	os.WriteFile(path, []byte("{not json"), 0o644)
	if readAddinPointer(dir) != "" {
		t.Fatal("a corrupt pointer must read as empty, not crash or invent a version")
	}
}

func TestReadMarkerAcceptsTheUtf8BomWindowsPowerShellWrites(t *testing.T) {
	// Found live: install.ps1's Out-File -Encoding utf8 (Windows PowerShell 5.1) prepends a BOM, and
	// the first update_connector call from Claude Desktop reported the marker as absent.
	dir := t.TempDir()
	path := filepath.Join(dir, "installed-version.json")
	os.WriteFile(path, append([]byte{0xEF, 0xBB, 0xBF}, []byte(`{"version":"v0.1.2","deployed":["2027"]}`)...), 0o644)

	m := readMarker(path)

	if m == nil || m.Version != "v0.1.2" {
		t.Fatalf("BOM-prefixed marker must parse: %+v", m)
	}
}
