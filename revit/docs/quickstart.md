# Quickstart — build, install, run a first script

This is the from-source path: **build, then feed the installer a locally built package** via
its `-LocalPackagePath` escape hatch. It is what a developer runs to try an unreleased build;
a user installs a published release with the one-liner in [`install.md`](../install.md).

## What you need

| | |
|---|---|
| Windows machine (or VM) | with **Revit 2025 and/or 2027** installed — these are the two supported versions (PRD §11) |
| .NET 10 SDK | builds both TFMs; the 2025 leg cross-targets `net8.0-windows` with no second SDK |
| Go (1.25+) | builds the MCP Server (broker) |
| [Claude Code](https://claude.com/claude-code) CLI | or any MCP client; the installer registers the server with `claude mcp add` if `claude` is on `PATH` |

All commands below run from the **repo root**, in PowerShell.

## 1. Build the add-in (Windows, needs Revit installed)

The projects reference `RevitAPI.dll` from `C:\Program Files\Autodesk\Revit <version>` —
they only build on a machine with Revit. They multi-target `net10.0-windows` (built against
Revit 2027's install) and `net8.0-windows` (Revit 2025's), so the plain solution build
needs **both** versions installed — a missing version's leg fails with compile errors, not
a warning:

```powershell
dotnet build revit\mcp-bridge\MCPBridge.sln -c Release
```

With only one Revit version installed, build just its leg by overriding the target-framework
list (`-f` isn't accepted for solution builds):

```powershell
# Revit 2027 only:
dotnet build revit\mcp-bridge\MCPBridge.sln -c Release -p:TargetFrameworks=net10.0-windows
# Revit 2025 only:
dotnet build revit\mcp-bridge\MCPBridge.sln -c Release -p:TargetFrameworks=net8.0-windows
```

## 2. Build the broker

```powershell
go build -C revit\mcp-server -o mcp-server.exe ./cmd/mcp-server
```

## 3. Assemble the local package zip

The installer consumes a zip with `addin-<year>/`, `shim-<year>/` and `server/` folders at its
root — the same layout a real release uses. Each `addin-<year>` folder holds the real add-in
(the matching TFM's build output); each `shim-<year>` holds `MCPBridge.Shim.dll` and the shim's
`.addin` manifest. The installer puts the shim in `Addins\<year>` and the real add-in under
`%LOCALAPPDATA%\Programs\MCPBridge\addin\<version>\<year>\`, pointed to by `addin\current.json`
(see `docs/self-update-architecture.md` §4). This is the only layout: a zip with an `addin-<year>/`
but no matching `shim-<year>/` is refused as a packaging error. The add-in's own `MCPBridge.addin`
still travels in `addin-<year>/` (it is the build output, verbatim; Revit reads the shim's manifest):

```powershell
$stage = New-Item -ItemType Directory -Force "$env:TEMP\mcpbridge-package"
# Revit 2027 payload (net10.0-windows). Repeat with net8.0-windows + addin-2025/shim-2025 if you have 2025.
$payload = New-Item -ItemType Directory -Force "$($stage.FullName)\addin-2027"
Copy-Item revit\mcp-bridge\src\MCPBridge.AddIn\bin\Release\net10.0-windows\* $payload -Recurse
Copy-Item revit\mcp-bridge\src\MCPBridge.AddIn\MCPBridge.addin $payload
$shim = New-Item -ItemType Directory -Force "$($stage.FullName)\shim-2027"
Copy-Item revit\mcp-bridge\src\MCPBridge.Shim\bin\Release\net10.0-windows\MCPBridge.Shim.dll $shim
Copy-Item revit\mcp-bridge\src\MCPBridge.Shim\MCPBridge.addin $shim
# Broker payload
$server = New-Item -ItemType Directory -Force "$($stage.FullName)\server"
Copy-Item revit\mcp-server\mcp-server.exe $server
# Optional: a manifest lets a re-run skip components that did not change (and leave a running
# Revit open). Without it every run redeploys everything.
. .\revit\install.ps1 -LoadFunctionsOnly
$info = (& revit\mcp-server\mcp-server.exe -build-info) | ConvertFrom-Json
New-PackageManifest $stage.FullName 'local-dev' $info.howto_corpus | ConvertTo-Json -Depth 5 | Out-File "$($stage.FullName)\manifest.json" -Encoding utf8
Compress-Archive "$($stage.FullName)\*" "$env:TEMP\mcpbridge-release.zip" -Force
```

## 4. Install

```powershell
.\revit\install.ps1 -LocalPackagePath "$env:TEMP\mcpbridge-release.zip"
```

This deploys the shim to every detected supported Revit version's Addins folder and the real
add-in to `%LOCALAPPDATA%\Programs\MCPBridge\addin\<version>\<year>\`, puts the
broker in `%LOCALAPPDATA%\Programs\MCPBridge\`, registers `revit` with Claude Code
(`claude mcp add revit -- ...\mcp-server.exe --mode local`), and writes a Programs & Features
uninstall entry. Re-running it is safe — though note the installer's "already up to date"
no-op short-circuit applies to the release-download path only; with `-LocalPackagePath` every
run re-enters the deploy loop, by design (there is no release to compare a local zip against) —
though with a `manifest.json` in the zip, components whose hash the last run recorded are skipped.
`-Uninstall` removes it.

## 5. Run a first script

Start Revit and open (or create) a document — the add-in dials the broker automatically and
retries on a backoff, so start order doesn't matter. Then, in a `claude` session:

> Use the revit tools: call `list_instances`, then run a script on the open document that
> returns `Document.Title`.

You should see your Revit instance with the document listed, then a
`{"status":"success", "output":"<your document's title>"}` result. From there, see the
[tool and script-globals reference](tools.md) — and note the connector's own built-in agent
guide (`get_skills`) covers the same ground for the agent automatically.

If nothing shows up in `list_instances`: check **Add-Ins → MCP Bridge → Status** inside
Revit (shows connection state and, on its **MCP Server** line, which `broker.json` it's
dialing — a Revit registered with a *different* broker than your client's looks healthy from
both sides and shows up in neither), **Reconnect** to force a fresh `broker.json` read after
restarting the broker, and the add-in's
`connection.log` under `%LOCALAPPDATA%\Connectors\Revit\`. It rotates at roughly 5MB, keeping
one previous generation as `connection.log.old` — so if the live file starts mid-stream, look
there for the earlier history. Only that one generation is kept; the next rotation overwrites it.

## Mac + Parallels variant (this project's own dev topology)

There is no macOS Revit, so the Mac side only runs the broker (PRD §05 "remote mode"):

1. Inside the Windows VM: steps 1–4 above (skip the broker registration — the VM's Revit
   will connect to the Mac-side broker instead).
2. On the Mac: `revit/install-mac.sh` — builds the broker from source, auto-detects the
   Parallels shared-network IP, and registers it with Claude Code in `-mode remote`.
3. Point the add-in at the Mac-side broker. Either click **Add-Ins → MCP Bridge → MCP Server: Local**
   inside Revit and enter the shared folder's UNC path (`\\Mac\connectors`); the add-in reconnects
   to the remote broker at once, the button reads **MCP Server: REMOTE**, and the choice is saved to
   `%LOCALAPPDATA%\Connectors\Revit\bridge-config.json` so it survives restarts. Or launch Revit
   with `MCPBRIDGE_BROKER_MODE=remote` and `MCPBRIDGE_SHARED_ROOT=<UNC path of the shared folder>`
   set for the Revit process (the older mechanism; a saved `bridge-config.json` takes precedence
   over it). Click the button again to go back to the local broker.

`revit/dev-tooling/` automates the VM-side loop for this repo's own development; see its
README before reusing any of it.
