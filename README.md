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
To remove it, use Windows **Apps & features** (the installer registers an uninstall entry) or re-run
`install.ps1 -Uninstall`.

**Updating.** Two ways, same result:

- **From Revit.** The MCP Server checks GitHub for a newer release shortly after it starts and every
  six hours after that. When one exists, **Add-Ins → MCP Bridge → Status** shows
  `Update available (vX.Y.Z)` with an **Update Now** button. After you confirm, it runs the installed
  updater silently. If the add-in changed, every open Revit window of that version is asked to close:
  Revit prompts you to save unsaved work first, and if you cancel, that Revit keeps running and is
  updated automatically the next time you close it. Reopen Revit yourself afterwards. If only the MCP
  Server changed, Revit stays open and the update takes effect the next time your MCP client starts
  the server (reconnect the `revit` server, e.g. `/mcp` in Claude Code). The Status window keeps
  showing the update as available until then.
- **From PowerShell.** Re-run the install one-liner above. It deploys only the components whose
  content changed and closes Revit only if the add-in is one of them.

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
