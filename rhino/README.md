# Rhino Connector

Lets Claude and other agents run Python 3 and C# against live Rhino 8 documents and Grasshopper definitions, and learn the Rhino API on demand, in the same mold as the [Revit connector](../revit/README.md).

Two components (`CONVENTIONS.md` "Bridge + Server"):

- [`mcp-bridge/`](./mcp-bridge/) — the Rhino MCP Bridge, a Rhino plug-in (.NET 8, `Rhino.MCPBridge.*`). Listens on loopback and publishes `instances/<pid>.json`.
- [`mcp-server/`](./mcp-server/) — the Rhino MCP Server, a Go process that speaks MCP over stdio and dials in to every running Rhino. No singleton: each server process is independent.

Plus [`test-harness/`](./test-harness/) (live tier-2 suite) and [`dev-tooling/`](./dev-tooling/): `deploy-plugin.sh` / `deploy-plugin-windows.ps1` build and install into the local Rhino for the dev loop, and `package-yak.sh` builds the distributable cross-platform yak package (the plug-in plus both platforms' server binaries).

## What an agent can do

- **Run code against the live document** — `execute_script` in **Python 3** or **C#**, with `poll_execution` / `cancel_execution`; every run is one Undo entry and a failed run is rolled back.
- **Reach every instance** — `list_instances` shows every running Rhino, its open documents, and its Grasshopper definitions; each call is addressed by `{instance_id, document_id}`, and several agents can drive at once.
- **Learn the API on demand** — `list_functions` / `search_functions` / `describe_function` over RhinoCommon, `rhinoscriptsyntax` and Grasshopper, in both call shapes; plus a version-verified how-to corpus (`search_howtos` / `describe_howto`).
- **Drive Grasshopper** — open, inspect, edit, wire (`Connector.Grasshopper`), set inputs, solve, and read a component's outputs and errors; `inspect_gh_definition` and the component catalog make a definition discoverable.
- **See the result** — `capture_view` returns a viewport or the Grasshopper canvas inline as an image (JPEG by default) (and `frame_canvas` frames it).
- **Manage plug-ins** — Yak-backed `search_plugins` / `install_plugin` / `uninstall_plugin`, and `restart_rhino` to load a freshly installed one.

## Install

**Not yet distributed** — a one-line install is the current phase (PRD §18 phase 7; the Windows end to end was just proven, [`docs/spikes/phase-7-windows-distribution.md`](./docs/spikes/phase-7-windows-distribution.md)). Until a **yak** package ships, build and install from source.

**macOS:**

```sh
brew install dotnet@8                          # once; the deploy script sets DOTNET_ROOT itself
rhino/dev-tooling/deploy-plugin.sh             # build + yak install + restart Rhino (discards unsaved work)
cd rhino/mcp-server && go build -o mcp-server-mac ./cmd/mcp-server
claude mcp add rhino -- "$(pwd)/mcp-server-mac"   # register the server with Claude
```

**Windows** (Rhino 8):

```powershell
powershell -ExecutionPolicy Bypass -File rhino\dev-tooling\deploy-plugin-windows.ps1   # build + yak install + restart
cd rhino\mcp-server; go build -o mcp-server.exe ./cmd/mcp-server
claude mcp add rhino -- (Resolve-Path .\mcp-server.exe).Path
```

When distribution ships this collapses to a single **Package Manager** (`_PackageManager`) install plus one register command, with updates handled by Rhino's own package auto-update (PRD §15).

## Design & docs

- [`docs/PRD.md`](./docs/PRD.md) — the source of truth; per-phase status in its Phased Roadmap (§18).
- [`docs/implementation-plan.md`](./docs/implementation-plan.md) — the phase plan and test tiers.
- [`docs/spikes/`](./docs/spikes/) — what was verified live against a real Rhino before each design was committed.
- Day-to-day build / test / review process: the `rhino-connector-development` skill (`.claude/skills/rhino-connector-development/` at the repo root).

## Status

Built and live-verified against Rhino 8 on macOS (per PR) and Windows (release gate): the core execution loop (Python 3 + C#), undo/rollback and the confirmation gate, multi-instance addressing with `list_instances`, viewport capture, API discovery, first-class **Grasshopper** (open / edit / wire / drive / read / observe, canvas capture, Yak-backed plug-in management), and the **how-to corpus** (`search_howtos` / `describe_howto`, seeded from the harness and version-stamped). **Distribution** (phase 7) is the current phase — the Windows yak-package path is proven ([`docs/spikes/phase-7-windows-distribution.md`](./docs/spikes/phase-7-windows-distribution.md)) but a one-line install is not shipped yet. See the PRD's Phased Roadmap (§18) for authoritative per-phase status.

## Tests

Tier 1: `dotnet test rhino/mcp-bridge` and `go test -race ./...` in `rhino/mcp-server` — both run in CI on every push. Tier 2 (the live harness) needs an unlocked session and a Rhino with a model open, and skips — never fails — when no instance is connected:

```sh
cd rhino/test-harness && go test -tags harness ./... -v -broker-exe ../mcp-server/mcp-server-mac
```
