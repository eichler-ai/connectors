# Contributing

Thanks for your interest. This repo is early-stage and moving fast; the ground rules below
are what keep it reviewable.

## Orientation

- **Design source of truth:** [`revit/docs/PRD.md`](revit/docs/PRD.md). Read the sections
  relevant to what you're touching before implementing — PRs that contradict the PRD without
  updating it (or saying why) will be asked to reconcile.
- **Naming conventions:** [`CONVENTIONS.md`](CONVENTIONS.md) — in particular the MCP
  Bridge (the Revit add-in) vs. MCP Server (the agent-facing broker) split.
- **Getting a working setup:** [`revit/docs/quickstart.md`](revit/docs/quickstart.md).
- **Day-to-day process detail** (dev loop, live-verification tooling, review checklist):
  `.claude/skills/revit-connector-development/SKILL.md`. It is written for agent-driven
  development but the process applies to human contributors identically.

## Testing — two tiers, deliberately

There is no mocked-broker integration tier in between; don't add one.

1. **Tier 1 — unit tests.** The Go broker (`revit/mcp-server`) is pure logic and is
   unit-tested everything, TDD-first (`go test ./...`). The C# add-in's decision logic lives
   in `MCPBridge.Core` behind the `MCPBridge.RevitAdapter` seam and is tested with fakes
   (`dotnet test` on `MCPBridge.Core.Tests` / `MCPBridge.Discovery.Tests`) — note these
   need a machine with Revit installed to build (see issue #39 for CI plans).
2. **Tier 2 — live harness.** Anything that genuinely needs a running Revit
   (`ExternalEvent` firing, real transactions, real dialogs, script-globals behavior) lives
   in `revit/test-harness/` — `go test -tags harness` against a real Revit + broker stack.
   Assertions about what a script actually gets at runtime belong here *by construction*
   (Revit API types can't be loaded outside Revit's own process).

**PR expectations:**

- Behavior changes come with tests at the tier the behavior lives in.
- Anything touching the Revit seam (threading, dialogs, transactions, script globals,
  discovery reflection) needs live verification against a real Revit session before merge —
  say in the PR what was verified and how.
- CI must pass (`gofmt`, `go vet`, `go test -race` for the broker; the harness must
  type-check under its build tag).
- When a test run exits 0, confirm the executed-test *count* — this repo has seen
  `dotnet test` exit 0 while silently skipping an entire assembly.

## Security-sensitive surface

In `MCPBridge.Core` and `MCPBridge.RevitAdapter`, **`public` means "reachable from an agent
script"** — scripts compile against every assembly loaded in the Revit process. Default new
types there to `internal`; never let a public type be, return, or yield transaction/adapter
machinery, directly or through a caller-supplied callback. See [`SECURITY.md`](SECURITY.md)
and PRD §14 for the history behind this rule.

## Docs stay in sync

If your change makes the PRD, a README, or the broker's embedded agent guide
(`revit/mcp-server/internal/mcpserver/skill.md`) wrong, fix that in the same PR.
