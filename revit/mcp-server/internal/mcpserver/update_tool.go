// update_connector (issue #199): check GitHub for a newer connector release
// on demand and, when asked to with explicit confirmation, start the installed
// updater — the agent-side counterpart of the Revit ribbon's Update Now.
//
// Why a tool at all: the server checks GitHub at startup and every 6 h and
// records the result in broker.json, which the add-in's Status window only
// re-reads. Nothing could ask for a check NOW; live, making a fresh release
// visible meant restarting the MCP client, and a client restart turned out not
// to reliably restart its server (closing Claude Desktop's window leaves the
// process running; the singleton primary may belong to another client). The
// check half of this tool fixes that with no new add-in↔server protocol: it
// runs the same code path the timer does and writes the same file.
//
// Why one tool with a gated half rather than two: the consequential action
// (an update installs files, and on a machine not yet on the shim add-in
// layout still asks every running Revit to close) stays opt-in behind the same
// two-flag shape execute_script uses for lifecycle actions, and the read-only
// default is what an agent reaches for first.
//
// Two add-in layouts, one tool (docs/self-update-architecture.md §4, §6.2):
// under the shim (addin\current.json present) an add-in update writes a new
// version folder and flips the pointer -- nothing is closed, and each running
// Revit keeps its add-in until it is next restarted. On the legacy flat layout
// (no pointer yet) the loaded DLL itself has to be replaced, so the installer
// asks each running Revit of an affected version to close and defers one that
// stays open; that is also how the one-time migration onto the shim goes. The
// wording below is chosen per layout so it is true for both.
package mcpserver

import (
	"bytes"
	"context"
	"encoding/json"
	"log"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/updatecheck"
)

// Two sources, per §01 "source is a real module name": records about the tool's own decisions
// (gating, launching, what the marker says) come from this package; the one record that
// reports the GitHub check itself failing comes from the package that made the request.
const (
	updateSource      = "mcp-server.internal.mcpserver.update"
	updateCheckSource = "mcp-server.internal.updatecheck"
)

// UpdateConnectorIn is update_connector's input. With neither flag it is a
// read-only check; both flags together apply the update.
type UpdateConnectorIn struct {
	Apply                   bool `json:"apply,omitempty" jsonschema:"set true to install the newer release after the check, by starting the installed updater (install.ps1 -Update -Silent) — exactly what the Revit ribbon's Update Now does. Requires confirm_lifecycle_actions: true as well. Local mode only."`
	ConfirmLifecycleActions bool `json:"confirm_lifecycle_actions,omitempty" jsonschema:"set true to confirm the user agreed to the update. On the shim add-in layout (addin_layout: shim) nothing is closed: the new add-in is installed beside the running one and each Revit loads it at its next restart. On the legacy flat layout (addin_layout: flat) every running Revit of an affected version is asked to close (Revit prompts to save unsaved work; a Revit kept open is updated when it is next closed). Refused without it."`
}

// Add-in layouts reported in UpdateConnectorOut.AddinLayout.
const (
	// AddinLayoutShim: Revit loads MCPBridge.Shim.dll, which loads the add-in
	// named by <app dir>\addin\current.json. Updates flip the pointer; nothing
	// closes; a running Revit keeps its add-in until restarted.
	AddinLayoutShim = "shim"
	// AddinLayoutFlat: the add-in DLL sits directly in Revit's Addins folder
	// and must be replaced in place, so an update asks a running Revit to
	// close (or defers until it exits). Also the state before the one-time
	// migration onto the shim.
	AddinLayoutFlat = "flat"
)

// UpdateServerStatus is the MCP Server half of the result. Running is the
// version of the process answering this call; Installed is what the installer's
// version marker says is on disk — the two differ after a staged swap until the
// MCP client reconnects and starts the new exe.
type UpdateServerStatus struct {
	Running         string `json:"running"`
	Installed       string `json:"installed,omitempty"`
	UpdateAvailable bool   `json:"update_available"`
}

// UpdateRevitStatus is one Revit version's add-in status. Multiple Revit
// versions are first-class here: the installer tracks each one separately
// (deployed / deferred while that Revit was running on the flat layout /
// skipped because the release shipped no payload for it). AddinInstalled is
// what a Revit of that version loads at its next start: the pointer's version
// on the shim layout, the marker's on the flat one. Note carries the one-line
// consequence for a connected Revit -- "restart it to load" on the shim
// layout, "applied when it exits" for a deferred flat one -- because the
// registry does not carry the add-in version each instance is running, so the
// per-instance running-vs-installed comparison lives in that Revit's own
// Status window (the shim's whole point is that the two can differ).
type UpdateRevitStatus struct {
	Version            string `json:"version"`
	AddinInstalled     string `json:"addin_installed,omitempty"`
	State              string `json:"state"`
	ConnectedInstances int    `json:"connected_instances"`
	UpdateAvailable    bool   `json:"update_available"`
	Note               string `json:"note,omitempty"`
}

// UpdateConnectorOut is update_connector's result, for the check and apply forms alike.
type UpdateConnectorOut struct {
	Latest string             `json:"latest,omitempty"`
	Server UpdateServerStatus `json:"server"`
	// AddinLayout is AddinLayoutShim or AddinLayoutFlat -- what an apply will
	// do to a running Revit (nothing, or ask it to close) follows from it.
	AddinLayout string              `json:"addin_layout,omitempty"`
	Revit       []UpdateRevitStatus `json:"revit"`
	Applied     bool                `json:"applied"`
	Notices     []*diag.Record      `json:"notices,omitempty"`
	Error       *diag.Record        `json:"error,omitempty"`
}

// InstalledMarker mirrors the fields of install.ps1's installed-version.json
// this tool reads (the file is written by New-VersionMarker there; the
// per-component hashes are not needed here).
type InstalledMarker struct {
	Version  string   `json:"version"`
	Deployed []string `json:"deployed"`
	Deferred []string `json:"deferred"`
	Skipped  []string `json:"skipped"`
}

// UpdateDeps is everything update_connector needs, injectable so the result
// shaping and gating are unit-testable without GitHub, a marker file, or a
// process launch.
type UpdateDeps struct {
	// Mode is "local" or "remote" (apply is local-only: the installer lives on
	// the Revit machine, which in remote mode is not where this server runs).
	Mode string
	// Version is this running server's own version ("dev" for a local build).
	Version string
	// Registry supplies connected instances per Revit version; may be nil.
	Registry *registry.Registry
	// CheckNow performs the GitHub check and records it in broker.json,
	// returning the latest tag. Production: updatecheck.CheckNow bound to the
	// rendezvous root.
	CheckNow func(ctx context.Context) (string, error)
	// ReadMarker returns the installer's version marker and the install dir it
	// was read from; (nil, dir, nil) when there is no marker.
	ReadMarker func() (*InstalledMarker, string, error)
	// ReadAddinPointer returns the version named by <appDir>\addin\current.json
	// (the shim layout's pointer, self-update-architecture.md §4.1), or "" when
	// there is none -- which is what says "legacy flat layout". Production:
	// readAddinPointer. Nil is treated as "" so older test wiring keeps working.
	ReadAddinPointer func(appDir string) string
	// Launch starts the installer detached: script is the self-copy path,
	// scope "User" or "AllUsers". Production: launchInstaller.
	Launch func(script, scope string) error
	Logger *log.Logger
}

// NewUpdateDeps wires the production dependencies: the check against the
// rendezvous root's broker.json, the marker beside this executable, and a
// detached PowerShell launch of the installer's self-copy.
func NewUpdateDeps(mode, rendezvousRoot, version string, reg *registry.Registry, client *http.Client, logger *log.Logger) UpdateDeps {
	return UpdateDeps{
		Mode:     mode,
		Version:  version,
		Registry: reg,
		CheckNow: func(ctx context.Context) (string, error) {
			return updatecheck.CheckNow(ctx, client, rendezvousRoot, version, logger)
		},
		ReadMarker:       readMarkerBesideExecutable,
		ReadAddinPointer: readAddinPointer,
		Launch:           launchInstaller,
		Logger:           logger,
	}
}

// RegisterUpdate adds update_connector to s.
func RegisterUpdate(s *mcp.Server, deps UpdateDeps) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "update_connector",
		Description: "Check GitHub now for a newer connector release and report, per component, what is running, what is installed and what is available: the MCP Server, and the add-in for each installed Revit version (with how many instances of each are connected, and addin_layout: shim or flat). " +
			"Read-only by default. With apply: true AND confirm_lifecycle_actions: true it also starts the installed updater, which installs the files and closes nothing on the shim add-in layout — each running Revit keeps its add-in until the user restarts it, and the running server keeps serving the old version until the MCP client reconnects; on the legacy flat layout each running Revit is asked to close instead (Revit prompts to save). Local mode only. Tell the user what to restart afterwards; never restart Revit for them. Use this instead of waiting for the server's 6-hourly check or restarting the client.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in UpdateConnectorIn) (*mcp.CallToolResult, UpdateConnectorOut, error) {
		return nil, updateConnector(ctx, deps, in), nil
	})
}

func updateConnector(ctx context.Context, deps UpdateDeps, in UpdateConnectorIn) UpdateConnectorOut {
	out := UpdateConnectorOut{Revit: []UpdateRevitStatus{}}
	out.Server.Running = deps.Version

	// Gate BEFORE any network or filesystem work: a refused apply should not
	// have already changed broker.json, so the caller can retry with the flag
	// and see the same picture.
	if in.Apply && !in.ConfirmLifecycleActions {
		// Both layouts described, deliberately: this gate runs before the pointer is read, so it cannot
		// yet say which one applies (the read-only call's addin_layout does).
		out.Error = diag.New(diag.SeverityError, "update-requires-confirmation", updateSource,
			"apply: true installs the release; on the shim add-in layout nothing is closed and each running Revit loads the new add-in at its next restart, on the legacy flat layout every running Revit of an affected version is asked to close so the add-in can be replaced; pass confirm_lifecycle_actions: true to confirm the user agreed").
			WithRemedy("ask the user first, then call update_connector again with apply: true and confirm_lifecycle_actions: true", "or call with no arguments for the read-only check (its addin_layout says which case applies)")
		return out
	}
	if in.Apply && deps.Mode != "local" {
		out.Error = diag.New(diag.SeverityError, "update-not-available-in-remote-mode", updateSource,
			"this server runs in remote mode, on a different machine from Revit and its installer; apply can only run where install.ps1 is installed").
			WithRemedy("run install.ps1 (or the ribbon's Update Now) on the Revit machine", "the read-only check still works from here")
		return out
	}

	marker, appDir, merr := deps.ReadMarker()
	if merr != nil {
		out.Notices = append(out.Notices, diag.New(diag.SeverityWarning, "installed-version-unreadable", updateSource,
			"the installer's version marker could not be read: "+merr.Error()).
			WithDetail(map[string]any{"install_dir": appDir}))
	}
	if marker != nil {
		out.Server.Installed = marker.Version
	}
	pointer := ""
	if deps.ReadAddinPointer != nil && appDir != "" {
		pointer = deps.ReadAddinPointer(appDir)
	}
	out.AddinLayout = AddinLayoutFlat
	if pointer != "" {
		out.AddinLayout = AddinLayoutShim
	}

	latest, cerr := deps.CheckNow(ctx)
	if cerr != nil {
		out.Error = diag.New(diag.SeverityError, "update-check-failed", updateCheckSource,
			"could not determine the latest release: "+cerr.Error()).
			WithRemedy("retry in a few minutes (GitHub may be unreachable or rate-limiting this network)")
		out.Revit = revitStatuses(marker, pointer, deps.Registry, "")
		return out
	}
	out.Latest = latest
	out.Server.UpdateAvailable = versionBehind(deps.Version, latest)
	out.Revit = revitStatuses(marker, pointer, deps.Registry, latest)

	if marker != nil && !versionBehind(marker.Version, latest) && versionBehind(deps.Version, latest) {
		out.Notices = append(out.Notices, diag.New(diag.SeverityInfo, "server-restart-pending", updateSource,
			latest+" is installed on disk but this server process is still "+deps.Version+"; it steps aside on its own within about a minute (issue #201) and the client's next call starts the installed release").
			WithRemedy("wait a minute and call again; if the version still lags, reconnect the revit MCP server in the client"))
	}

	if !in.Apply {
		return out
	}

	anyBehind := (marker == nil && out.Server.UpdateAvailable) || (marker != nil && versionBehind(marker.Version, latest))
	for _, r := range out.Revit {
		if r.UpdateAvailable {
			anyBehind = true
		}
	}
	if !anyBehind {
		out.Notices = append(out.Notices, diag.New(diag.SeverityInfo, "already-current", updateSource,
			"nothing to apply: the installed connector is already "+latest))
		return out
	}

	script := filepath.Join(appDir, "install.ps1")
	if _, err := os.Stat(script); err != nil {
		out.Error = diag.New(diag.SeverityError, "installer-not-found", updateSource,
			"no installed copy of install.ps1 at "+script+"; the connector was not installed by install.ps1, or an older installer left only a stub").
			WithRemedy("run the install one-liner once: irm https://raw.githubusercontent.com/eichler-ai/connectors/main/revit/install.ps1 | iex")
		return out
	}
	scope := "User"
	if strings.HasPrefix(strings.ToLower(appDir), strings.ToLower(`C:\Program Files\`)) {
		scope = "AllUsers"
	}
	if err := deps.Launch(script, scope); err != nil {
		out.Error = diag.New(diag.SeverityError, "installer-launch-failed", updateSource,
			"starting the installer failed: "+err.Error()).
			WithDetail(map[string]any{"script": script, "scope": scope}).
			WithRemedy("run the ribbon's Update Now, or the install one-liner, on the Revit machine")
		return out
	}
	out.Applied = true
	if out.AddinLayout == AddinLayoutShim {
		// §6.2: apply and load are two user-controlled steps. The tool installs; the user restarts.
		out.Notices = append(out.Notices, diag.New(diag.SeverityInfo, "update-started", updateSource,
			"the updater is running and closes nothing: the new add-in is installed beside the running one and each running Revit keeps its current add-in until it is restarted. Once the new server is on disk this process steps aside on its own within about a minute, and the client's next call starts the installed release.").
			WithRemedy("tell the user: update installed; restart Revit when convenient to load the new add-in, and reconnect the revit MCP server (or restart the client) if the server changed", "call update_connector again in a minute or two to confirm every component reports the new version"))
	} else {
		out.Notices = append(out.Notices, diag.New(diag.SeverityInfo, "update-started", updateSource,
			"the updater is running on the legacy flat add-in layout: every running Revit of an affected version is asked to close (Revit prompts to save unsaved work; a Revit kept open is updated when it is next closed) and nothing is relaunched. Once the new server is on disk this process steps aside on its own within about a minute, and the client's next call starts the installed release.").
			WithRemedy("tell the user to reopen Revit once it has closed; call update_connector again in a minute or two to confirm every component reports the new version"))
	}
	if deps.Logger != nil {
		deps.Logger.Printf("update_connector: started %s -Update -Silent -Scope %s (running %s, latest %s)", script, scope, deps.Version, latest)
	}
	return out
}

// revitStatuses builds one entry per Revit version the installer tracks, plus
// any connected version it does not know about (a dev deploy by hand). pointer
// is the shim layout's current.json version, "" on the flat layout.
func revitStatuses(marker *InstalledMarker, pointer string, reg *registry.Registry, latest string) []UpdateRevitStatus {
	connected := map[string]int{}
	if reg != nil {
		for _, inst := range reg.List() {
			connected[inst.RevitVersion]++
		}
	}
	seen := map[string]bool{}
	out := []UpdateRevitStatus{}
	add := func(version, state, installed string, behind bool, note string) {
		seen[version] = true
		out = append(out, UpdateRevitStatus{
			Version:            version,
			AddinInstalled:     installed,
			State:              state,
			ConnectedInstances: connected[version],
			UpdateAvailable:    behind,
			Note:               note,
		})
	}
	if marker != nil {
		for _, v := range marker.Deployed {
			// What a Revit of this version loads at its next start: the pointer, when there is one,
			// is the add-in's own "apply" record and can run ahead of the marker (a deferred shim
			// migration writes the payload and pointer first); otherwise the marker.
			installed := marker.Version
			if pointer != "" {
				installed = pointer
			}
			note := ""
			if pointer != "" && connected[v] > 0 {
				// The shim case #209 is about: installed on disk, but a Revit that was already open
				// when it was installed is still running the previous add-in. The registry does not
				// know which add-in version an instance runs, so this is a statement about the
				// layout, not a measurement -- that Revit's Status window has the measured line.
				note = "installed on disk; a Revit that was already open when it was installed keeps the previous add-in until it is restarted (its Status window shows installed vs running)"
			}
			add(v, "deployed", installed, latest != "" && versionBehind(installed, latest), note)
		}
		for _, v := range marker.Deferred {
			// Deferred: the release in marker.Version is parked for this version until its Revit exits,
			// so it is behind whenever a latest is known (review of #200: not asserted blindly on a failed check).
			// Flat-layout only (the legacy deploy, or the one-time migration onto the shim): under the
			// shim an add-in update is never deferred because it never needs Revit closed.
			add(v, "deferred", "", latest != "",
				"deferred: this Revit was running and the flat add-in layout needs it closed to replace the loaded add-in; the update is applied automatically when that Revit exits, and nothing else is closed for it")
		}
		for _, v := range marker.Skipped {
			add(v, "skipped", "", false, "")
		}
	}
	for v := range connected {
		if !seen[v] {
			add(v, "unknown", "", false, "")
		}
	}
	return out
}

// versionBehind reports whether running is an older release than latest.
// Tags are compared after trimming a leading "v", case-insensitively; a
// "dev" build is never "behind" (it is not a release at all), and an empty
// side means "unknown" — never a false positive.
func versionBehind(running, latest string) bool {
	r := normalizeTag(running)
	l := normalizeTag(latest)
	if r == "" || l == "" || r == "dev" {
		return false
	}
	return r != l
}

func normalizeTag(tag string) string {
	t := strings.ToLower(strings.TrimSpace(tag))
	return strings.TrimPrefix(t, "v")
}

// readMarkerBesideExecutable reads installed-version.json from the directory
// this server runs from. install.ps1 puts mcp-server.exe, the marker and its
// own self-copy in one app dir (Get-AppDir), and a server started from a
// parked .old-* image is still in that same dir.
func readMarkerBesideExecutable() (*InstalledMarker, string, error) {
	exe, err := os.Executable()
	if err != nil {
		return nil, "", err
	}
	appDir := filepath.Dir(exe)
	return readMarker(filepath.Join(appDir, "installed-version.json")), appDir, nil
}

func readMarker(path string) *InstalledMarker {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	// install.ps1 writes the marker with Windows PowerShell's Out-File -Encoding utf8, which prepends a
	// UTF-8 BOM that encoding/json rejects -- found live: the first update_connector call from Claude
	// Desktop reported the connected Revit as state "unknown" with no installed version.
	b = bytes.TrimPrefix(b, []byte{0xEF, 0xBB, 0xBF})
	var m InstalledMarker
	if err := json.Unmarshal(b, &m); err != nil {
		return nil
	}
	return &m
}

// readAddinPointer reads the shim layout's <appDir>\addin\current.json
// ({"version":"v0.1.5"}, self-update-architecture.md §4.1) and returns its
// version, or "" when there is no pointer or it does not parse -- "" is the
// legacy flat layout's signal, so a corrupt pointer degrades to the wording
// that asks more of the user, never less. BOM-tolerant for the same reason as
// readMarker (the installer writes it BOM-less itself, the shim tolerates one).
func readAddinPointer(appDir string) string {
	b, err := os.ReadFile(filepath.Join(appDir, "addin", "current.json"))
	if err != nil {
		return ""
	}
	b = bytes.TrimPrefix(b, []byte{0xEF, 0xBB, 0xBF})
	var p struct {
		Version string `json:"version"`
	}
	if err := json.Unmarshal(b, &p); err != nil {
		return ""
	}
	return strings.TrimSpace(p.Version)
}

// launchInstaller starts install.ps1 -Update -Silent detached, the same
// invocation UpdateTrigger.cs uses for the ribbon's Update Now, and does not
// wait: the installer outlives this call (it downloads ~120 MB, flips the
// add-in pointer or -- on the flat layout -- asks Revit to close, and may swap
// this very executable underneath the running process, which Windows allows).
func launchInstaller(script, scope string) error {
	cmd := exec.Command("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Update", "-Silent", "-Scope", scope)
	if err := cmd.Start(); err != nil {
		return err
	}
	return cmd.Process.Release()
}
