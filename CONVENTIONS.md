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

This includes **advertised-but-unimplemented interface dimensions**: if a tool schema accepts a parameter the implementation doesn't honor yet, the implementation must error loudly on a mismatch, never silently fall back to a different target. The Revit connector's `document_id` shipped as accepted-but-ignored — and the sharper lesson is that this wasn't an oversight: the gap was found in review and the silent active-document fallback was *kept as a deliberate choice* ("single-document instances work correctly either way"), which quietly turned into a standing hazard as the agent-facing docs continued to advertise real addressing. A known gap that fails loudly stays a known gap; one that silently succeeds gets forgotten and then trusted. Wire the loud mismatch guard the moment the schema advertises the dimension, even when real support comes later.

## Engineering invariant: the acting connection's identity travels with the action

In any broker that maps stable logical identities (an instance id, a session id) onto transient connections, **no action keyed by the logical identity alone may mutate lifecycle state** — attach, detach, deregister, settle, escalate. The acting connection's identity (a connection pointer, a registration epoch/fencing token) must be checked at the point of mutation, because a stale connection's deferred effects can land arbitrarily late: a half-open socket's teardown may run minutes after the peer has already redialed and re-registered the same logical identity, and an identity-keyed teardown then destroys the live replacement.

Established the hard way in the Revit MCP Server, where one root cause expressed itself four independent ways before being named (a teardown race deregistering live instances, a prune split-brain, a per-drop socket/goroutine leak, and a phantom execution falsely latching a healthy instance unrecoverable). The broker's `DetachInstance(id, conn)` / `Registry.RemoveIfEpoch(id, epoch)` shapes are the reference implementations.

## Engineering invariant: every retained record and buffer has a stated bound

Any per-request record, replay buffer, or accumulation a long-running process retains must carry an explicit eviction story — a count cap, an age, or a documented reason it cannot grow (and "the process restarts eventually" is not one). State the bound in a comment at the declaration, mirroring it across components that hold the same data on both ends of a wire (the Revit broker's settled-execution cache deliberately mirrors the add-in's replay ring buffer's last-N/last-10-minutes shape). The v1 review's unbounded-growth findings fit one pattern: the record was added for a feature, and no one owned the question of when it leaves.

## Origin

Established while designing and building the Revit connector — see `revit/docs/PRD.md` for the full reasoning behind each of these, and `.claude/skills/revit-connector-development/` for the day-to-day development process built around them. Extract further conventions here as more connectors are built.
