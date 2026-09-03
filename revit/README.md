# Revit Connector

Lets Claude and other agents execute dynamic C# against a live, open Revit document, and progressively learn the Revit API instead of relying on a fixed tool catalog.

Two components:

- [`mcp-bridge/`](./mcp-bridge/) — the Revit MCP Bridge, a Revit add-in (.NET)
- [`mcp-server/`](./mcp-server/) — the Revit MCP Server, a standalone agent-facing process (Go)

Plus:

- [`install.ps1`](./install.ps1) / [`install.md`](./install.md) — Windows installer (add-in + broker, MCP registration via the `claude` CLI)
- [`install-mac.sh`](./install-mac.sh) — Mac-side setup for this project's own Mac+Parallels dev topology (PRD §12 "Mac + Parallels")
- [`test-harness/`](./test-harness/) — live MCP test harness (see its own README)

## Install

On Windows with Revit 2025 and/or 2027 installed, run this in PowerShell:

```powershell
irm https://raw.githubusercontent.com/eichler-ai/connectors/main/revit/install.ps1 | iex
```

It downloads the latest signed release, installs the add-in for the Revit versions present, and
registers the MCP server with Claude if the `claude` CLI is on `PATH`. To update, either re-run it, or
click **Update Now** in Revit's **Add-Ins → MCP Bridge → Status** window when it shows
`Update available` (the MCP Server checks GitHub for releases; to apply an add-in change Revit asks
to close, prompting to save unsaved work first, and a Revit you keep open is updated when you next
close it). Uninstall from Windows **Apps & features** or with
`install.ps1 -Uninstall`. Releases are
**self-signed** for now, so Windows shows an "Unknown Publisher" prompt (a CA certificate is
deferred — PRD §12). Full detail: [`install.md`](./install.md). The Mac + Parallels dev topology
(broker on the Mac, add-in in the VM) uses [`install-mac.sh`](./install-mac.sh) instead.

## Getting started

- [`docs/quickstart.md`](./docs/quickstart.md) — build from source, install, run a first script
- [`docs/tools.md`](./docs/tools.md) — the MCP tools, script globals, and error codes
- [`docs/howto-corpus-design.md`](./docs/howto-corpus-design.md) / [`docs/howto-seed-plan.md`](./docs/howto-seed-plan.md) — the how-to corpus: design rationale, and how it is seeded, verified and shipped

## Design

Full design doc: [`docs/PRD.md`](./docs/PRD.md).

## Development process

See the `revit-connector-development` skill (`.claude/skills/revit-connector-development/` at the repo root) for the day-to-day build/test/review process.

## Status

Shipped and merged: core execution loop, dialog suppression + multi-instance, API discovery +
file exchange, Revit 2025 alongside 2027 (Phase 6, partial — 2026 and Marketplace submission
remain open), the release pipeline with self-signed builds and a per-component installer
(Phase 5; a CA certificate is deferred), and the how-to corpus (Phase 8: `search_howtos` /
`describe_howto` / `submit_howto`, seeded from the harness and verified per Revit version).
The validation corpus (Phase 4) is in progress. See the PRD's Phased Roadmap (§15) for the
authoritative per-phase status.
