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
// (an update installs files and swaps the server image the client will start
// next) stays opt-in behind the same two-flag shape execute_script uses for
// lifecycle actions, and the read-only default is what an agent reaches for
// first.
//
// One add-in layout (docs/self-update-architecture.md §4, §6.2): Revit loads a
// stable shim that loads the add-in named by addin\current.json. An add-in
// update writes a new version folder and flips that pointer -- nothing is
// closed, and each running Revit keeps its add-in until it is next restarted.
// Every note and notice below is written for that model; there is no path in
// which the installer asks Revit to close for an update.
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
	ConfirmLifecycleActions bool `json:"confirm_lifecycle_actions,omitempty" jsonschema:"set true to confirm the user agreed to the update. Nothing is closed: the new add-in is installed beside the running one and each Revit loads it at its next restart; the running server keeps serving until the MCP client reconnects. Refused without it."`
}

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
// (deployed / skipped because the release shipped no payload for it).
// AddinInstalled is what a Revit of that version loads at its next start --
// the pointer's version. Note carries the one-line consequence for a connected
// Revit when there is one -- "restart it to load" once an update is available
// or was just applied (never in steady state) -- because the registry does not
// carry the add-in version each instance is running, so the per-instance
// running-vs-installed comparison lives in that Revit's own Status window (the
// shim's whole point is that the two can differ).
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
	Latest  string              `json:"latest,omitempty"`
	Server  UpdateServerStatus  `json:"server"`
	Revit   []UpdateRevitStatus `json:"revit"`
	Applied bool                `json:"applied"`
	Notices []*diag.Record      `json:"notices,omitempty"`
	Error   *diag.Record        `json:"error,omitempty"`
}

// InstalledMarker mirrors the fields of install.ps1's installed-version.json
// this tool reads (the per-component hashes are not needed here).
type InstalledMarker struct {
	Version  string   `json:"version"`
	Deployed []string `json:"deployed"`
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
	// (the shim's pointer, self-update-architecture.md §4.1), or "" when it
	// cannot be read -- in which case the marker's version stands in for it.
	// Production: readAddinPointer. Nil is treated as "".
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
		Description: "Check GitHub now for a newer connector release and report, per component, what is running, what is installed and what is available: the MCP Server, and the add-in for each installed Revit version (with how many instances of each are connected). " +
			"Read-only by default. With apply: true AND confirm_lifecycle_actions: true it also starts the installed updater, which installs the files and closes nothing — each running Revit keeps its add-in until the user restarts it, and the running server keeps serving the old version until the MCP client reconnects. Local mode only. Tell the user what to restart afterwards; never restart Revit for them. Use this instead of waiting for the server's 6-hourly check or restarting the client.",
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
		out.Error = diag.New(diag.SeverityError, "update-requires-confirmation", updateSource,
			"apply: true installs the release; nothing is closed and each running Revit loads the new add-in at its next restart; pass confirm_lifecycle_actions: true to confirm the user agreed").
			WithRemedy("ask the user first, then call update_connector again with apply: true and confirm_lifecycle_actions: true", "or call with no arguments for the read-only check")
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
	// The launch just happened: every connected Revit of a deployed version is now (about to be)
	// behind the pointer, whatever the pre-apply comparison said.
	for i := range out.Revit {
		if out.Revit[i].State == "deployed" && out.Revit[i].ConnectedInstances > 0 {
			out.Revit[i].Note = "installed on disk; a Revit that was already open when it was installed keeps the previous add-in until it is restarted (its Status window shows installed vs running)"
		}
	}
	// §6.2: apply and load are two user-controlled steps. The tool installs; the user restarts.
	out.Notices = append(out.Notices, diag.New(diag.SeverityInfo, "update-started", updateSource,
		"the updater is running and closes nothing: the new add-in is installed beside the running one and each running Revit keeps its current add-in until it is restarted. Once the new server is on disk this process steps aside on its own within about a minute, and the client's next call starts the installed release.").
		WithRemedy("tell the user: update installed; restart Revit when convenient to load the new add-in, and reconnect the revit MCP server (or restart the client) if the server changed", "call update_connector again in a minute or two to confirm every component reports the new version"))
	if deps.Logger != nil {
		deps.Logger.Printf("update_connector: started %s -Update -Silent -Scope %s (running %s, latest %s)", script, scope, deps.Version, latest)
	}
	return out
}

// revitStatuses builds one entry per Revit version the installer tracks, plus
// any connected version it does not know about (a dev deploy by hand). pointer
// is current.json's version, "" when it could not be read.
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
			// What a Revit of this version loads at its next start: the pointer is the add-in's own
			// "apply" record; the marker's version stands in only when the pointer cannot be read.
			installed := marker.Version
			if pointer != "" {
				installed = pointer
			}
			behind := latest != "" && versionBehind(installed, latest)
			note := ""
			if connected[v] > 0 && behind {
				// The case #209 is about: once the update is applied, a Revit that is open at that
				// moment keeps running the previous add-in. Said only when there is a delta a restart
				// could resolve (review of #219: in steady state -- installed == latest, nothing
				// applied -- the note told users to restart for no reason). The post-apply variant is
				// stamped by updateConnector once the launch has happened. The registry does not know
				// which add-in version an instance runs, so both are statements about the layout, not
				// measurements -- that Revit's Status window has the measured line.
				note = "an add-in update is available; a Revit that is open when it is applied keeps its current add-in until it is restarted, so tell the user to restart Revit after applying"
			}
			add(v, "deployed", installed, behind, note)
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

// readAddinPointer reads the shim's <appDir>\addin\current.json
// ({"version":"v0.1.5"}, self-update-architecture.md §4.1) and returns its
// version, or "" when there is no pointer or it does not parse -- the caller
// then falls back to the marker's version rather than inventing one.
// BOM-tolerant for the same reason as readMarker (the installer writes it
// BOM-less itself, the shim tolerates one).
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
// add-in pointer, and may swap this very executable underneath the running
// process, which Windows allows).
func launchInstaller(script, scope string) error {
	cmd := exec.Command("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Update", "-Silent", "-Scope", scope)
	if err := cmd.Start(); err != nil {
		return err
	}
	return cmd.Process.Release()
}
