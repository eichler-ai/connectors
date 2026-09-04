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
// Why one tool with a gated half rather than two: the dangerous action (an
// update asks every running Revit, of every installed version, to close) stays
// opt-in behind the same two-flag shape execute_script uses for lifecycle
// actions, and the read-only default is what an agent reaches for first.
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
	ConfirmLifecycleActions bool `json:"confirm_lifecycle_actions,omitempty" jsonschema:"set true to confirm that applying the update may close Revit: every running Revit, of every installed version, is asked to close (Revit prompts to save unsaved work; a Revit kept open is updated when it is next closed). Refused without it."`
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
// (deployed / deferred while that Revit was running / skipped because the
// release shipped no payload for it), and an update asks every running one to
// close.
type UpdateRevitStatus struct {
	Version            string `json:"version"`
	AddinInstalled     string `json:"addin_installed,omitempty"`
	State              string `json:"state"`
	ConnectedInstances int    `json:"connected_instances"`
	UpdateAvailable    bool   `json:"update_available"`
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
		ReadMarker: readMarkerBesideExecutable,
		Launch:     launchInstaller,
		Logger:     logger,
	}
}

// RegisterUpdate adds update_connector to s.
func RegisterUpdate(s *mcp.Server, deps UpdateDeps) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "update_connector",
		Description: "Check GitHub now for a newer connector release and report, per component, what is running, what is installed and what is available: the MCP Server, and the add-in for each installed Revit version (with how many instances of each are connected). " +
			"Read-only by default. With apply: true AND confirm_lifecycle_actions: true it also starts the installed updater — every running Revit is asked to close (Revit prompts to save), the running server keeps serving the old version until the MCP client reconnects; local mode only. Use this instead of waiting for the server's 6-hourly check or restarting the client.",
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
			"apply: true asks every running Revit, of every installed version, to close so the add-in can be replaced; pass confirm_lifecycle_actions: true to confirm that is intended").
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

	latest, cerr := deps.CheckNow(ctx)
	if cerr != nil {
		out.Error = diag.New(diag.SeverityError, "update-check-failed", updateCheckSource,
			"could not determine the latest release: "+cerr.Error()).
			WithRemedy("retry in a few minutes (GitHub may be unreachable or rate-limiting this network)")
		out.Revit = revitStatuses(marker, deps.Registry, "")
		return out
	}
	out.Latest = latest
	out.Server.UpdateAvailable = versionBehind(deps.Version, latest)
	out.Revit = revitStatuses(marker, deps.Registry, latest)

	if marker != nil && !versionBehind(marker.Version, latest) && versionBehind(deps.Version, latest) {
		out.Notices = append(out.Notices, diag.New(diag.SeverityInfo, "server-restart-pending", updateSource,
			latest+" is installed on disk but this server process is still "+deps.Version+"; it takes effect when the MCP client next starts the server").
			WithRemedy("reconnect the revit MCP server in the client, or quit the client fully and start it again (closing its window may leave the server running)"))
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
	out.Notices = append(out.Notices, diag.New(diag.SeverityInfo, "update-started", updateSource,
		"the updater is running: every running Revit is asked to close (Revit prompts to save unsaved work; a Revit kept open is updated when it is next closed) and nothing is relaunched. This server keeps serving "+deps.Version+" until the MCP client reconnects.").
		WithRemedy("tell the user to reopen Revit once it has closed, and to reconnect the revit MCP server (or quit and restart the client) to load the new server"))
	if deps.Logger != nil {
		deps.Logger.Printf("update_connector: started %s -Update -Silent -Scope %s (running %s, latest %s)", script, scope, deps.Version, latest)
	}
	return out
}

// revitStatuses builds one entry per Revit version the installer tracks, plus
// any connected version it does not know about (a dev deploy by hand).
func revitStatuses(marker *InstalledMarker, reg *registry.Registry, latest string) []UpdateRevitStatus {
	connected := map[string]int{}
	if reg != nil {
		for _, inst := range reg.List() {
			connected[inst.RevitVersion]++
		}
	}
	seen := map[string]bool{}
	out := []UpdateRevitStatus{}
	add := func(version, state, installed string, behind bool) {
		seen[version] = true
		out = append(out, UpdateRevitStatus{
			Version:            version,
			AddinInstalled:     installed,
			State:              state,
			ConnectedInstances: connected[version],
			UpdateAvailable:    behind,
		})
	}
	if marker != nil {
		for _, v := range marker.Deployed {
			add(v, "deployed", marker.Version, latest != "" && versionBehind(marker.Version, latest))
		}
		for _, v := range marker.Deferred {
			// Deferred: the release in marker.Version is parked for this version until its Revit exits,
			// so it is behind whenever a latest is known (review of #200: not asserted blindly on a failed check).
			add(v, "deferred", "", latest != "")
		}
		for _, v := range marker.Skipped {
			add(v, "skipped", "", false)
		}
	}
	for v := range connected {
		if !seen[v] {
			add(v, "unknown", "", false)
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

// launchInstaller starts install.ps1 -Update -Silent detached, the same
// invocation UpdateTrigger.cs uses for the ribbon's Update Now, and does not
// wait: the installer outlives this call (it downloads ~120 MB, asks Revit to
// close, and may swap this very executable underneath the running process,
// which Windows allows).
func launchInstaller(script, scope string) error {
	cmd := exec.Command("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Update", "-Silent", "-Scope", scope)
	if err := cmd.Start(); err != nil {
		return err
	}
	return cmd.Process.Release()
}
