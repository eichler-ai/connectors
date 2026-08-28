# Connectors Repo — Conventions

This repo hosts MCP-based connectors that let Claude and other agents drive desktop host applications directly. The Revit connector is the first; this file captures the patterns established while building it, meant to generalize to the next one.

## Naming pattern: Bridge + Server

Each connector to a desktop host application is expected to ship two components:

- **`<App> MCP Bridge`** — an in-process add-in/plugin loaded by the host application. Speaks a private TCP/JSON-RPC protocol, never MCP directly. Owns everything that requires being inside the host process (API access, UI-thread constraints, live reflection over the host's SDK).
- **`<App> MCP Server`** — a standalone process that speaks MCP (stdio) to Claude/agents, and is the only thing that talks to the Bridge's TCP protocol. Ships as a single self-contained binary per platform.

This split is the general pattern for any host app with the same constraints (single-threaded UI API, needs an in-process executor) — not a Revit-specific choice.

### Short vs. full names

| Context | Add-in | Agent-facing server |
|---|---|---|
| Inside a connector's own directory/docs | `MCP Bridge` | `MCP Server` |
| Outside that context — repo root, cross-connector docs, anywhere "MCP Server" alone is ambiguous | `<App> MCP Bridge` | `<App> MCP Server` |
| Inside the host app's own UI | `MCP Bridge` (no app-name prefix — redundant when already inside the app) | n/a |
| Inside Claude's MCP client config | n/a | the app's lowercase slug, e.g. `"revit"` |

There is no umbrella product name distinct from the connector itself. Refer to the whole system as "the `<App>` connector" or "this connector" — never as "MCP Bridge" alone, which stays reserved for the add-in specifically.

"The broker" and "the add-in" are acceptable internal/engineering shorthand for the MCP Server and MCP Bridge respectively, used freely in code comments and design docs once the full names are established — they refer to the same components, not sub-parts of them.

## App-data layout

Every connector's local state lives under a per-connector namespaced root, never a shared unnamespaced one:

```
%LOCALAPPDATA%\Connectors\<App>\
```

(macOS/Linux equivalents follow the platform's own app-data convention, same `Connectors/<App>/` suffix.) This exists specifically so future connectors don't collide with each other's state.

## Testing philosophy

- **MCP Server: unit-test everything, always.** It's pure logic (protocol, routing, state) with no host-app dependency, so there's no excuse not to.
- **MCP Bridge: unit-test everything behind a `Core`/adapter seam.** Put host-API calls behind thin interfaces so decision logic (policy, identity, discovery) is testable without a live host app. Only the adapters themselves, and true host-API integration, need a live integration harness.
- **No mocked-integration middle tier by default.** Start with unit tests + a live end-to-end harness; add a mocked-peer integration tier only if bugs are actually showing up at the component seams, not preemptively.

## Design principle: observability over silence

Anywhere a connector automatically resolves something on the agent's behalf (a suppressed dialog, an auto-dismissed warning, a cancelled execution), that resolution gets reported back in the result — never handled invisibly. The agent needs to detect when something was papered over, not just receive a clean-looking success that hides what actually happened. Established for the Revit connector; applies to any connector that does automatic resolution of host-app UI/state on the agent's behalf.

## Origin

Established while designing and building the Revit connector — see `revit/docs/PRD.md` for the full reasoning behind each of these, and `.claude/skills/revit-connector-development/` for the day-to-day development process built around them. Extract further conventions here as more connectors are built.
