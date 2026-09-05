# Connectors

MCP-based connectors that let Claude and other agents drive desktop host applications
directly — starting with Revit.

Each connector follows the same two-component pattern (see [`CONVENTIONS.md`](./CONVENTIONS.md)):
an in-process **Bridge** (a plugin/add-in inside the host app) plus a standalone, agent-facing
**MCP Server** that speaks MCP and fans out to however many live app instances exist.

## Connectors

- [`revit/`](./revit/) — **Revit connector** (Revit MCP Bridge + Revit MCP Server). Rather
  than a fixed catalog of pre-built tools, its primary surface is `execute_script` — dynamic
  C# compiled and run against the live document — plus API-discovery tools that let an agent
  read Revit's own API documentation on demand, and a searchable, version-verified how-to
  corpus of worked examples that grows from agent submissions. Working today against Revit
  2025 and 2027: the core execution loop, dialog suppression, multi-instance addressing, API
  discovery, file exchange and the how-to corpus are shipped and live-validated; releases are
  built and self-signed by the release pipeline (a CA certificate is deferred). **Install with one
  line** (see [Install](#install) below), or build from source — the
  [quickstart](./revit/docs/quickstart.md). Design:
  [`revit/docs/PRD.md`](./revit/docs/PRD.md); per-phase status: PRD §15.

## Install

**Revit connector, on Windows** with Revit 2025 and/or 2027 installed. In PowerShell:

```powershell
irm https://raw.githubusercontent.com/eichler-ai/connectors/main/revit/install.ps1 | iex
```

This downloads the latest signed release and installs the add-in for whichever supported Revit
versions are present, then registers the MCP server with Claude if the `claude` CLI is on `PATH`.

**Uninstalling.** Quit Claude Desktop fully (tray icon → Quit; closing the window leaves its server
running) and close Revit, then either remove **Revit MCP Bridge** from Windows **Apps & features**, or
run the installed copy of the installer from PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LocalAppData\Programs\MCPBridge\install.ps1" -Uninstall -Scope User
```

Use `-Scope AllUsers` and the `%ProgramFiles%\MCPBridge` copy for an all-users install. The
`-ExecutionPolicy Bypass` is needed because Windows blocks saved scripts by default (the one-liner
avoids that by piping into `iex`). It removes the add-in from every Revit version, the server and its
data under `%LocalAppData%`, the Apps & features entry, and the `revit` registration in Claude Code
and Claude Desktop. If a Revit is still running it is asked to close first; anything it keeps locked
is reported and cleaned up by a second run.

**Updating.** Two ways, same result:

- **From Revit.** The MCP Server checks GitHub for a newer release shortly after it starts and every
  six hours after that. When one exists, **Add-Ins → MCP Bridge → Status** shows
  `Update available (vX.Y.Z)` with an **Update Now** button. After you confirm, it runs the installed
  updater silently. Nothing is closed: the new add-in is installed beside the one Revit is running,
  and Revit loads it the next time you start it — until then Status reads
  `MCP Bridge (add-in): vX.Y.Z installed · running vX.Y.W — restart Revit to load it`.
  If only the MCP Server changed, the update takes effect the next time your MCP client starts
  the server. Running server processes notice the new release on disk and step aside on their own
  within about a minute, so the next call from any client starts the new one; if a Status check still
  shows the old server after that, reconnect the `revit` server (e.g. `/mcp` in Claude Code). Until
  then the Status window says the new version is installed but the running server is still the old one.
- **From Claude.** Ask whether the connector is up to date: the `update_connector` tool checks GitHub
  right away (no waiting for the server's six-hourly check) and reports the server and each Revit
  version's add-in. Ask it to apply the update and it starts the same updater as Update Now, after
  confirming with you, then tells you what to restart (Revit, to load the new add-in; the `revit`
  server, if it changed) — it never restarts Revit for you.
- **From PowerShell.** Re-run the install one-liner above. It deploys only the components whose
  content changed and ends with the same "restart Revit to load the new add-in" line when one applies.

Releases are **self-signed** for now, so Windows shows an "Unknown Publisher" prompt on first run;
a CA-issued certificate is a later step (PRD §12). More detail, including the Mac + Parallels dev
topology, is in [`revit/install.md`](./revit/install.md); installing an unreleased local build is in
the [quickstart](./revit/docs/quickstart.md).

## Contributing & security

See [`CONTRIBUTING.md`](./CONTRIBUTING.md) and [`SECURITY.md`](./SECURITY.md) — the latter
matters here more than for most projects: these connectors execute agent-authored code inside
host applications by design, and its trust model is worth understanding before running or
extending them.

## License

Apache License 2.0 — see [`LICENSE`](./LICENSE) and [`NOTICE`](./NOTICE).
