# Rhino connector — development environment

Two platforms, two roles, kept apart on purpose:

| | **Mac** | **Windows** |
|---|---|---|
| Role | The development platform. Build, deploy, iterate, run the live harness. | A release target. Build in CI, verify live before a release. |
| Rhino | Rhino 8 for Mac, native. Many documents per process. | Rhino 8 for Windows. One document per process (PRD §05). |
| Plug-in build | Homebrew `dotnet@8` SDK, `net8.0`, RhinoCommon from NuGet. | Same csproj; built on the `windows-latest` CI runner. |
| Server build | `go build` (arm64). | `GOOS=windows GOARCH=amd64 go build`, cross-compiled from anywhere. |
| Live tests | The harness, per PR, natively. | A live pass on a Windows machine before each release; no VM in this project. |
| App data | `~/Library/Application Support/Connectors/Rhino/` | `%LOCALAPPDATA%\Connectors\Rhino\` |

There is **no remote mode and no Parallels topology** for this connector (PRD §05): Rhino runs where
the agent runs. The Revit skill's `prlctl`, launcher-agent and shared-folder material does not apply.

---

## Developing on the Mac

### Toolchain

- **.NET 8 SDK**: `brew install dotnet@8`. Keg-only, so every `dotnet` invocation needs
  `DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec` and `/opt/homebrew/opt/dotnet@8/bin` on `PATH`.
  `deploy-plugin.sh` sets both; set them yourself for a bare `dotnet build`/`dotnet test`.
- **Go**: the repo's toolchain (`go 1.26.5` in every `go.mod`). `internal/servercore` is consumed
  through a `replace` directive, so building `rhino/mcp-server` needs no network once the module
  cache is warm. The first `go mod tidy` pulled ~200 MB of ML dependencies and took ten minutes
  on this network; that is a one-time cost.
- **Rhino 8** (currently 8.35). The CLIs under
  `/Applications/Rhino 8.app/Contents/Resources/bin/`: `rhinocode` (run scripts/commands in the
  running Rhino), `yak` (package manager), `rhinoscriptcompiler`.
- **RhinoCommon reference assemblies**: NuGet `RhinoCommon` (pinned in
  `rhino/mcp-bridge/Directory.Build.props`), `lib/net7.0`, consumed by `net8.0` with
  `ExcludeAssets="runtime"` so nothing is copied beside the plug-in. Rhino's own copy loads at runtime.
  The bundled assemblies and XML docs are under
  `/Applications/Rhino 8.app/Contents/Frameworks/RhCore.framework/Resources/` (`RhinoCommon.dll`,
  `RhinoCommon.xml`, `Rhino.Runtime.Code.dll`) and `…/ManagedPlugIns/GrasshopperPlugin.rhp/`
  (`Grasshopper.dll`, `Grasshopper.xml`, `GH_IO.xml`).

### Build, deploy, run

```sh
rhino/dev-tooling/deploy-plugin.sh              # build Release, yak build + install, restart Rhino
rhino/dev-tooling/deploy-plugin.sh --no-restart # install only; Rhino picks it up at its next start
cd rhino/mcp-server && go build -o mcp-server-mac ./cmd/mcp-server
./mcp-server-mac -version                       # version + source revision
```

What the deploy script does, so its failures are legible:

1. `dotnet build Rhino.MCPBridge.sln -c Release`.
2. Copies `Rhino.MCPBridge.PlugIn.rhp`, the two product DLLs and the `.deps.json` into a temp dir
   with a `manifest.yml`, runs `yak build`, `yak uninstall rhino-mcp-bridge` (ignoring "not
   installed"), `yak install ./<file>.yak`. The package lands under
   `~/Library/Application Support/McNeel/Rhinoceros/packages/8.0/rhino-mcp-bridge/<version>/` with
   yak's `manifest.txt` marker; **Rhino scans that folder only at startup**, and ignores a folder
   without the marker (spikes §5).
3. Runs `rhino/docs/spikes/phase-1a/rhino-restart.sh`: quits Rhino (clicking `Delete` on the
   keep/delete sheet for an unsaved document, found by walking the sheet's `entire contents` — the
   button is not addressable by name), relaunches, waits for `rhinocode list` to show an instance,
   clicks **New Model** in the template chooser. **A fresh Rhino sits at the chooser with no
   document**, and commands run against nothing until one is open.

Manual install for the record: `yak install <file>.yak`; uninstall: `yak uninstall rhino-mcp-bridge`.
`_-PlugInManager` on the Mac has no load-from-path option (it opens the Plug-ins window).

### Registering with Claude Code

```sh
claude mcp add --scope local rhino -- /absolute/path/to/rhino/mcp-server/mcp-server-mac
```

The server takes no flags in normal use; `-app-data-dir` overrides the connector root for tests.

### Observing the plug-in

- `MCPBridgeStatus` in Rhino's command line: port, connection count, instance id, instance file path,
  bridge version (the version string carries the source revision — use it to confirm the loaded build).
- `~/Library/Application Support/Connectors/Rhino/instances/<pid>.json` — the file every server scans.
  A file for a pid that has exited is deleted by the next server scan.
- `~/Library/Application Support/Connectors/Rhino/connection.log` — auth, register, session ends;
  `startup-errors.log` — a failed `OnLoad`. Both size-capped.
- `~/.rhinocode/logs/rhinocode_<pid>` — RhinoCode's own log (language loading, script requests).
  Its timestamps are ahead of the system clock; never `sleep`-wait on them.

### Driving Rhino from a shell or a test

- `rhinocode script <path.py>` runs a Python 3 file in the running Rhino on the main thread;
  `rhinocode command <name>` runs a command. **Neither relays stdout**: a script writes a file and
  the caller reads it. The first script run on a machine deploys the CPython runtime to
  `~/.rhinocode/py39-rh8/` (~35 s, once).
- Rhino's command history: `Rhino.RhinoApp.CommandHistoryWindowText` from a script.
- System Events reaches Rhino's windows (`osascript`); `screencapture -x` after
  `tell application "Rhino 8" to activate` shows what Rhino is actually displaying when automation
  stalls.
- **A locked screen stops scripts and automation silently.** Check
  `ioreg -n Root -d1 -a | grep -A1 CGSSessionScreenIsLocked` before a live run; the harness does.

### Live harness

```sh
cd rhino/test-harness && go test -tags harness ./... -v -broker-exe ../mcp-server/mcp-server-mac
```

Skips (never fails) when no Rhino is connected; fails loudly on a locked screen. Cases that drive
Rhino use `rhinocode` and are macOS-only until the Windows pass grows its own driver.

### Quitting Rhino from a script

`osascript -e 'tell application "Rhino 8" to quit'` blocks on the keep/delete sheet when a document is
unsaved and the AppleEvent times out after two minutes; the restart helper handles the sheet. Set
`doc.Modified = False` from a script first and the sheet still appears for a new untitled document.

---

## Verifying on Windows

Nothing here is a development loop; it is the release gate the plan requires (implementation-plan.md
phase 2 and "Tiers"). No Windows machine is part of this project's dev environment today; the first
Windows pass is phase 2's deliverable and should extend this section with what it finds.

- **Build**: CI's `Rhino MCP Bridge (C#) tier-1 tests` job runs the tier-1 suite and builds the plug-in
  on `windows-latest`; `Rhino MCP Server (Go)` cross-compiles `GOOS=windows`. A release artifact for
  Windows comes from those, not from a Mac.
- **Runtime**: Rhino 8 for Windows hosts .NET Core by default (with an opt-in .NET Framework mode this
  connector does not support). The `net8.0` TFM verified on 8.35 for Mac is expected to load; the
  McNeel forum records `net7.0`/`net8.0` plug-ins failing on one 8.x service release, so the first
  Windows load is a real check, not a formality.
- **Install**: `yak install` works the same way; `%AppData%\McNeel\Rhinoceros\packages\8.0\`.
  `_-PlugInManager _Load <path>` also exists on Windows.
- **App data**: `%LOCALAPPDATA%\Connectors\Rhino\instances\<pid>.json`; the plug-in writes the file
  without a special ACL until phase 2 adds the owner-only one.
- **Documents**: one per process. `TestDocumentEventsRefreshTheRegistry` opens a second document and
  must be adapted (a second *instance*) or skipped with a reason on Windows; every case that assumes
  several documents in one instance needs the same review.
- **Driving Rhino**: `rhinocode.exe` ships with Rhino for Windows too; the System Events pieces of the
  restart helper have no Windows equivalent yet.
- **Live pass**: the same harness binary, built with `GOOS=windows`, run on the Windows machine with a
  Rhino open. Paste its output into the release PR.

---

## Scripts

| Script | Does |
|---|---|
| `rhino/dev-tooling/deploy-plugin.sh [--no-restart]` | Build, package, install, restart (Mac). |
| `rhino/docs/spikes/phase-1a/rhino-restart.sh` | Quit-discard-relaunch-new-model (Mac). |
| `rhino/docs/spikes/phase-1a/*.py` | The CLI probes from the spikes; templates for a new probe. |
