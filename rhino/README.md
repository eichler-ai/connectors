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

The connector ships as one cross-platform **yak** package — the plug-in plus both platforms' MCP server binaries — and registers with **both Claude Code and Claude Desktop** (PRD §15).

**Windows (recommended) — one line in PowerShell:**

```powershell
irm https://raw.githubusercontent.com/eichler-ai/connectors/main/rhino/install.ps1 | iex
```

It downloads the latest release, installs the plug-in with Rhino's yak CLI, and registers the MCP server with Claude Code **and** Claude Desktop (handling the Store/MSIX config path). Then restart Rhino to load the plug-in, and restart your Claude client to pick up the server. To remove it, run the downloaded script with `-Uninstall` (`powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall`), or uninstall from `_PackageManager` inside Rhino.

**macOS, or a manual Windows install.** Download the `.yak` from the [latest release](https://github.com/eichler-ai/connectors/releases), install it with the yak CLI that ships with Rhino (or drag the `.yak` onto the Rhino window), then run **`MCPBridgeRegister`** in Rhino's command line to register with both Claude clients:

```sh
# macOS ("C:\Program Files\Rhino 8\System\yak.exe" on Windows)
"/Applications/Rhino 8.app/Contents/Resources/bin/yak" install ~/Downloads/rhino-mcp-bridge-*.yak
```

`MCPBridgeStatus` shows the registration state per client. *(A macOS one-liner, and publishing to the public package server so `_PackageManager` finds it by search — plus Rhino's own auto-update — are later steps, PRD §15; for now the package is install-from-file.)*

**From source** (for development, or before a release exists):

```sh
# macOS
brew install dotnet@8                          # once; the deploy script sets DOTNET_ROOT itself
rhino/dev-tooling/deploy-plugin.sh             # build + yak install + restart Rhino (discards unsaved work)
cd rhino/mcp-server && go build -o mcp-server-mac ./cmd/mcp-server
claude mcp add rhino -- "$(pwd)/mcp-server-mac"   # register the server with Claude
```
```powershell
# Windows (Rhino 8)
powershell -ExecutionPolicy Bypass -File rhino\dev-tooling\deploy-plugin-windows.ps1   # build + yak install + restart
cd rhino\mcp-server; go build -o mcp-server.exe ./cmd/mcp-server
claude mcp add rhino -- (Resolve-Path .\mcp-server.exe).Path
```

## Releasing

Maintainers cut a release with the **`/release-rhino-plugin`** Claude command (`major` | `minor` | `patch`): it builds the cross-platform `.yak` on demand ([`.github/workflows/rhino-package.yml`](../.github/workflows/rhino-package.yml)), live-verifies it against a real Rhino, tags `rhino-vX.Y.Z`, and publishes a GitHub Release with the `.yak` attached. The process is spelled out in [`.claude/commands/release-rhino-plugin.md`](../.claude/commands/release-rhino-plugin.md). The package builds only on demand (never on push), so trigger it via that command or `gh workflow run rhino-package.yml -f version=<X.Y.Z>`.

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
