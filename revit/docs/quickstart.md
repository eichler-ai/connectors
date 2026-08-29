# Quickstart — build, install, run a first script

There is no packaged release yet (the release pipeline is PRD §12's own known gap), so today's
honest install path is **build from source, then feed the installer a locally built package**
via its `-LocalPackagePath` escape hatch. Everything below is the path that actually works
now; when GitHub Releases exist, step 3 collapses to the one-liner in
[`install.md`](../install.md).

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

The installer consumes a zip with `addin-<year>/` and `server/` folders at its root — the
same layout a real release will use. Each `addin-<year>` folder holds the `.addin` manifest
next to the matching TFM's build output (the manifest references `MCPBridge.AddIn.dll`
relatively, so they must sit side by side):

```powershell
$stage = New-Item -ItemType Directory -Force "$env:TEMP\mcpbridge-package"
# Revit 2027 payload (net10.0-windows). Repeat with net8.0-windows + addin-2025 if you have 2025.
$payload = New-Item -ItemType Directory -Force "$($stage.FullName)\addin-2027"
Copy-Item revit\mcp-bridge\src\MCPBridge.AddIn\bin\Release\net10.0-windows\* $payload -Recurse
Copy-Item revit\mcp-bridge\src\MCPBridge.AddIn\MCPBridge.addin $payload
# Broker payload
$server = New-Item -ItemType Directory -Force "$($stage.FullName)\server"
Copy-Item revit\mcp-server\mcp-server.exe $server
Compress-Archive "$($stage.FullName)\*" "$env:TEMP\mcpbridge-release.zip" -Force
```

## 4. Install

```powershell
.\revit\install.ps1 -LocalPackagePath "$env:TEMP\mcpbridge-release.zip"
```

This deploys the add-in to every detected supported Revit version's Addins folder, puts the
broker in `%LOCALAPPDATA%\Programs\MCPBridge\`, registers `revit` with Claude Code
(`claude mcp add revit -- ...\mcp-server.exe --mode local`), and writes a Programs & Features
uninstall entry. Re-running it is safe — though note the installer's "already up to date"
no-op short-circuit applies to the release-download path only; with `-LocalPackagePath` every
run redeploys, by design (there is no version to compare a local zip against). `-Uninstall`
removes it.

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
Revit (shows connection state and which broker it's dialing), and the add-in's
`connection.log` under `%LOCALAPPDATA%\Connectors\Revit\`.

## Mac + Parallels variant (this project's own dev topology)

There is no macOS Revit, so the Mac side only runs the broker (PRD §05 "remote mode"):

1. Inside the Windows VM: steps 1–4 above (skip the broker registration — the VM's Revit
   will connect to the Mac-side broker instead).
2. On the Mac: `revit/install-mac.sh` — builds the broker from source, auto-detects the
   Parallels shared-network IP, and registers it with Claude Code in `-mode remote`.
3. Launch Revit in the VM with `MCPBRIDGE_BROKER_MODE=remote` and
   `MCPBRIDGE_SHARED_ROOT=<UNC path of the shared folder>` set for the Revit process, so the
   add-in reads the Mac-side broker's `broker.json` from the shared drive.

`revit/dev-tooling/` automates the VM-side loop for this repo's own development; see its
README before reusing any of it.
