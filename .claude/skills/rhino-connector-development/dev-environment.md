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
Rhino use `rhinocode command` (never `rhinocode script`, see caveats) and are macOS-only until the
Windows pass grows its own driver.

One case is destructive and opt-in: `TestNonCooperatingScriptGoesUnrecoverable` wedges Rhino's main
thread on purpose, checks the grace-period → `unrecoverable` path, then kills and relaunches Rhino
through the restart helper. Run it alone, after the rest, and expect ~30 s plus a restart:

```sh
MCP_HARNESS_DESTRUCTIVE=1 go test -tags harness ./... -run Unrecoverable -v -broker-exe ../mcp-server/mcp-server-mac
```

Give a freshly relaunched Rhino ~10 s before the next run: a case that starts while the template
chooser is still up fails with an error result instead of `running`.

### Quitting Rhino from a script

`osascript -e 'tell application "Rhino 8" to quit'` blocks on the keep/delete sheet when a document is
unsaved and the AppleEvent times out after two minutes; the restart helper handles the sheet. Set
`doc.Modified = False` from a script first and the sheet still appears for a new untitled document.

---

## Verifying on Windows

Nothing here is a development loop; it is the release gate the plan requires (implementation-plan.md
phase 2 and "Tiers"). The **first Windows live pass ran 2026-09-11** (phase 2) on a Windows 11 **ARM64**
VM running Rhino 8.35 **as x64 under emulation**; the results below are what it found, and the loop it
established. There is no Windows deploy script — the steps are run by hand (or a session driving them),
because there is no VM topology to automate (PRD §05) and the Mac's `deploy-plugin.sh` is macOS-only.

- **Headline result — the `net8.0` plug-in loads on Windows Rhino 8** (the phase-1a §2 open question,
  now closed on Windows). It yak-installs, loads `AtStartup`, binds loopback, and writes
  `instances/<pid>.json` with `platform: "windows"` and a `bridge_version` carrying the source rev.
  Roslyn/C# warms up fine. **Confirm the loaded build is yours** via that `bridge_version`.
- **Build**: `dotnet build rhino\mcp-bridge\Rhino.MCPBridge.sln -c Release`. dotnet 10 SDK builds the
  `net8.0` targets fine (it restores the 8.0 targeting pack). **Kill Rhino before building or
  reinstalling** — a running Rhino holds the plug-in DLLs open, and the build then stalls for minutes
  retrying the output copy (seen: a 1-minute build take 16) while `yak uninstall` fails with
  "Access denied. If Rhino is running, close it and try again."
- **Package + install (the Windows equivalent of `deploy-plugin.sh`)**: copy the Release output's
  `*.rhp *.dll *.deps.json *.xml` flat into a temp dir (assert no `RhinoCommon.dll`), write the same
  `manifest.yml`, then `yak build` → `yak uninstall rhino-mcp-bridge` → `yak install .\*.yak`. Installs
  to `%AppData%\McNeel\Rhinoceros\packages\8.0\rhino-mcp-bridge\<version>\` with yak's `manifest.txt`
  marker (Rhino scans it at startup only). `yak`'s two warnings ("Content version/name doesn't match
  manifest") are cosmetic — the `.rhp`'s assembly identity vs. the package name. `_-PlugInManager _Load
  <path>` also works for a one-off load, but yak matches the release install path.
- **Launch a document, not the chooser**: `Rhino.exe /nosplash /runscript="_-New _None _Enter"` opens a
  blank document with no template chooser and without opening a template *file* (which would make saves
  overwrite the template). A fresh Rhino at the chooser runs nothing and `rhinocode` can't see it.
- **ARM64/emulation timing**: cold start ≈ 2 min; Roslyn warm-up ≈ 6–45 s; CPython deploy ≈ 30–100 s.
  Every poll (instance file, warm-up, `rhinocode list`) needs a generous deadline — 180 s+, not seconds.
- **Verify from files** (independent of GUI): `%LOCALAPPDATA%\Connectors\Rhino\instances\<pid>.json`,
  `connection.log` ("listening on 127.0.0.1:<port>", "roslyn warm-up done", "python warm-up done"),
  `startup-errors.log` (absent = clean `OnLoad`). The instance file now carries an **owner-only ACL on
  Windows** (`InstanceFile.OwnerOnlyDacl`, phase 2): `Get-Acl` shows `AreAccessRulesProtected: True` and
  a single full-control ACE for the current user — the Windows counterpart of the Unix `0600`.

- **Python on Windows — fixed by force-loading RhinoCode (#287).** The root cause: `RhinoCodePlugin`
  (McNeel's Python 3 / ScriptEditor host, GUID `c9cba87a-…`) is **demand-loaded** on Windows (registry
  `LoadMode=2`, the lone `WhenNeeded` among plug-ins that are all `AtStartup`), so it never loads at
  startup and Python 3 never registers — the plug-in's warm-up polled `QueryLatest` until the 180 s
  timeout. Before the fix, only opening the ScriptEditor loaded it (and even that was unreliable: a
  fast/cached warm-up could leave `scriptcontext` off `sys.path`, and the runner imported it
  unconditionally, hard-failing every run). **The fix:** `OnLoad` force-loads `RhinoCodePlugin` on the
  first `RhinoApp.Idle` tick (`PlugIn.LoadPlugIn(guid)`, main thread; deferred off `OnLoad` to avoid
  reentrancy), and the runner's `scriptcontext` import is best-effort (try/except). **Verified** on a
  launch with NO ScriptEditor: `force-load RhinoCodePlugin (…): True`, then `python warm-up done in
  ~11 s`. C# execution never needed any of this.
- **Driving Rhino / `rhinocode`**: `C:\Program Files\Rhino 8\System\rhinocode.exe`. It discovers Rhino
  through the RhinoCode remote-pipe server, which the #287 force-load now brings up at plug-in load, so
  `rhinocode list` sees the instance with no ScriptEditor. The harness's `rhinocodePath()` returns this
  path on Windows. The System Events pieces of the Mac restart helper have no Windows equivalent;
  kill+relaunch manually (`Stop-Process`, then the launch line above).
- **Documents**: one per process (PRD §05). `TestDocumentEventsRefreshTheRegistry` (opens a second
  document) skips with a reason on Windows; `TestOmittedDocumentIdIsActive` runs on both.
- **⚠️ Programmatic launch may leave no open document (#289).** On this VM, `Rhino.exe` launched with a
  runscript (`_-New _None`) or a `.3dm` file arg **intermittently ends with zero open documents** — the
  connector then correctly reports 0 documents (verified: `RhinoDoc.OpenDocuments()` is empty and
  `rhinocode`'s DOC column blank; the connector's registry is faithful, so it is a launch-procedure
  issue, not a connector bug). Doc-dependent cases (all script execution) need a document. **Deterministic
  doc-open step:** after launch, run any `rhinocode script` once — executing a script materialises an
  untitled document if none is open (observer effect, used deliberately). Post-#287, `rhinocode` works
  with no ScriptEditor, so this is a clean step. **`deploy-plugin-windows.ps1` does this for you** and
  confirms an active document; tracked as #289.
- **Live pass — one command**: `powershell -ExecutionPolicy Bypass -File
  rhino\dev-tooling\deploy-plugin-windows.ps1` builds, kills Rhino (DLL lock), yak-reinstalls, restarts
  (plain, **no ScriptEditor** — the #287 force-load brings RhinoCode up), waits for `python warm-up done`
  (which follows that force-load), and materialises a document (#289). Then build the
  server and run the harness: `cd rhino\mcp-server && go build -o mcp-server.exe ./cmd/mcp-server`
  (native Go; winget `GoLang.Go` is windows/arm64; the cold build pulls the shared/ML deps once) → `cd
  ..\test-harness && go test -tags harness ./... -v -broker-exe ..\mcp-server\mcp-server.exe`. **Result
  2026-09-11 (with #287): 29 pass / 2 skip / 0 fail**, the full Python suite included, no ScriptEditor.
  The 2 skips are the one-document case (`TestDocumentEventsRefreshTheRegistry`, PRD §05) and the
  destructive opt-in. This is the plan's phase-2 exit — the phase-1 suite green on Windows.

---

## Scripts

| Script | Does |
|---|---|
| `rhino/dev-tooling/deploy-plugin.sh [--no-restart]` | Build, package, install, restart (Mac). |
| `rhino/dev-tooling/deploy-plugin-windows.ps1 [-NoRestart]` | Build, package, yak-install, restart, and open a document (Windows). Kills Rhino first (DLL lock), waits for the #287 RhinoCode force-load + python warm-up, then materialises a document via `rhinocode` (#289 — a programmatic launch may open none). |
| `rhino/docs/spikes/phase-1a/rhino-restart.sh` | Quit-discard-relaunch-new-model (Mac). |
| `rhino/docs/spikes/phase-1a/*.py` | The CLI probes from the spikes; templates for a new probe. |
