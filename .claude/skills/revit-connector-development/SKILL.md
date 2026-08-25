---
name: revit-connector-development
description: Development process, tooling, testing strategy, and PR review checklist for building the Revit connector (Revit MCP Bridge add-in + Revit MCP Server) in this repo. Use whenever implementing, testing, reviewing, or deploying any part of the Revit connector, or when the process itself needs updating based on what's been learned.
---

# Revit Connector Development

This is the living process doc for building the Revit connector. It's meant to be updated as the process evolves — see "Keeping this skill current" at the bottom. If something here turns out to be wrong or the team starts doing it differently, fix this file in the same PR/session, don't just work around it silently.

## Orientation

- **Design source of truth:** `revit/docs/PRD.md`. Read it (or at least the sections relevant to what you're touching) before implementing anything — this skill covers *how* to build, the PRD covers *what* and *why*.
- **Naming/terminology conventions:** `CONVENTIONS.md` at the repo root. Get the Bridge/Server naming right (see PRD §04) — it's easy to accidentally call the whole connector "MCP Bridge" when you mean the add-in specifically, or vice versa.
- **Repo layout:** `revit/mcp-bridge/` (the add-in, C#/.NET), `revit/mcp-server/` (the broker, Go), `revit/install/` (deployment scripts), `revit/test-harness/` (live integration harness + corpus). Each has its own README with its planned internal layout.
- **Error/log/notice format:** every JSON-RPC `error.data`, every `notices[]` entry, and every NDJSON log line uses the one diagnostic-record shape in PRD §01 (`severity`, `code`, `source`, `message`, `detail`, `remedy`) — see that section before writing anything that reports an error or diagnostic. `source` values are the module names below (`mcp-bridge.core.execution`, `mcp-server.internal.registry`, etc.), not invented per-feature.

## Testing strategy

Two tiers, deliberately — no mocked-broker-integration middle tier for now. If bugs start showing up specifically at the broker↔add-in wire boundary that neither tier catches, reconsider adding one; don't build it preemptively.

### Tier 1 — unit tests

**MCP Server (Go): unit-test everything, always, TDD-first.** It's pure logic with zero Revit dependency — protocol framing (§05 Framing), tool routing, the instance registry/heartbeat state machine (§05), singleton lock-or-proxy (§05), local/remote path translation (§09). Write the failing test first. Go's standard `testing` package, table-driven tests as the default shape. Run with `go test ./...` from `revit/mcp-server/`.

**MCP Bridge (C#): unit-test everything behind the `Core`/`RevitAdapter` seam.** The add-in's actual Revit-API-touching code (raw `ExternalEvent` plumbing, `DialogBoxShowing` registration, `Transaction` wrapping) is not meaningfully unit-testable — Revit API types are mostly sealed/non-constructible outside a live session. Everything that's *decision logic* should live in `MCPBridge.Core` and be written against interfaces defined in `MCPBridge.RevitAdapter`, so it can be tested with fakes:

- Dialog default-answer policy (§07)
- Failures-API resolution policy — warning vs. error handling (§07)
- Document identity computation — the four-state scheme (§09)
- Cancellation state machine — `pending`/`running`/`busy`/`unrecoverable` transitions (§06)
- Discovery logic — `list_functions`/`search_functions`/`describe_function` reflection + pagination (§08). This one is a genuine bonus: reflection runs against the actual `RevitAPI.dll`/`RevitAPI.xml` files, which are static assets, not a live Revit session — so it's fully unit-testable with those files as test fixtures, no fake/adapter needed at all.

xUnit. Run `dotnet test` scoped to `tests/MCPBridge.Core.Tests/` and `tests/MCPBridge.Discovery.Tests/` as part of the normal dev loop — do **not** include `tests/MCPBridge.Integration.Tests/` in that loop, since it needs a live Revit instance and belongs to tier 2.

### Tier 2 — live integration harness

Everything that genuinely needs a running Revit process: `ExternalEvent` actually firing, real transaction commit/rollback, real modal dialogs, the full add-in↔broker↔agent round trip. Lives in `revit/test-harness/`, driven by `prlctl` in the dev environment (PRD §13 Dev-environment automation) or directly on a Windows-native install elsewhere. This is also where the validation corpus (PRD §13) runs — the same harness, just a bigger set of cases.

Not run on every commit by default (it's slow — VM/Revit lifecycle). Run it:
- before merging anything that touches threading (§06), dialog/failure handling (§07), or the discovery reflection path (§08)
- before every corpus regression pass (PRD §13: "re-run the full corpus on every add-in change")
- before cutting a release

## Tools & scripts

- **`prlctl`** — Parallels VM/guest control from the dev Mac. `prlctl start|stop|restart <vm>` for VM lifecycle; `prlctl exec <vm> ...` to launch/kill Revit.exe inside the guest once Parallels Tools are installed. Do not use `prlsrvctl` for this — that configures the Parallels service itself, not individual VMs (PRD §13).
- **`revit/install/`** — installs the built add-in DLL + `.addin` manifest into Revit's per-version Addins folder, and registers the Revit MCP Server with Claude's MCP client config. Use this rather than manually copying files when testing an install end-to-end.
- **`revit/test-harness/runner/`** — orchestrates VM/Revit lifecycle plus the corpus run; this is what "re-run the corpus" actually means mechanically.
- **Shared drive (`Z:` in the dev VM)** — backs remote-mode file exchange and broker discovery (PRD §05, §09). If it stops resolving, check the Parallels shared-folder config before assuming a code bug.

## Per-stage workflow (autonomous)

Each development stage — a roadmap phase (PRD §15) or any other discrete scope of work — runs this pipeline. Once step 1 is resolved, the rest runs without further check-ins; status lives on GitHub (the PR and its review comments), not in a running report back to the user.

1. **Questions up front, once.** Before starting implementation on a new scope of work, batch and ask whatever clarifying questions it actually needs — don't trickle them out mid-implementation. Once answered, proceed through the rest of this pipeline autonomously. The only thing that should interrupt it after this point is a genuine blocker — a decision only the user can make, not a judgment call this skill or the PRD already settles.
2. **Implement, TDD-first**, per the testing strategy above.
3. **Classify the work — groundbreaking or additive — and say so in the PR description:**
   - *Groundbreaking* — introduces a new architectural pattern or subsystem that doesn't already exist in the codebase (the threading/`ExternalEvent` model, the singleton lock-or-proxy, Roslyn ALC isolation, the reflection-based discovery mechanism — most of what roadmap phases 01–03 actually are).
   - *Additive* — extends or reuses an already-established pattern (a new corpus test case, a new discovery command built on the same reflection mechanism, a new `.addin` manifest for another Revit version once the multi-target pattern already exists).
4. **Groundbreaking work only: run `/simplify` on the diff before opening the PR.** Additive work skips this step — the change surface is small enough not to need it.
5. **Open the PR** (`gh pr create`), following this repo's normal git/PR hygiene (see the standing git-safety rules: no force-push, no skipped hooks, no `--amend` on already-pushed commits).
6. **Deploy an independent code-review agent** — a fresh agent, not a fork, with no shared context from the implementation work, reading the PR diff itself rather than being told what's in it:
   - *Groundbreaking* → Opus model, reviewing for correctness and robustness.
   - *Additive* → default model (Sonnet) is sufficient.
   - The agent posts its findings to the PR on GitHub (a review or summary comment) so they're visible without being relayed manually, and reports back with a short summary of what it found.
7. **Merging is not automatic.** This pipeline creates and reviews PRs autonomously — it does not merge them. Merge stays a human decision unless explicitly told otherwise later.

## PR review checklist

This is what the review step above (and any human reviewer) checks the PR against:

- [ ] Unit tests included for any new `Core`/`RevitAdapter` (Bridge) or `internal/*` (Server) logic — written before the implementation if this was done TDD-first, which it should have been.
- [ ] If the change touches threading, dialogs, failures, discovery, or file exchange, was the live harness actually run (not just unit tests)?
- [ ] Does the change match the naming conventions (`CONVENTIONS.md`) — MCP Bridge vs. MCP Server, short vs. full name for the doc's context?
- [ ] Does the change follow the observability-over-silence principle (PRD §01) if it adds any new automatic-resolution behavior?
- [ ] **Every new error/notice/log record uses the shared diagnostic-record shape (PRD §01)** — `message` names concrete identifiers and the actual underlying condition (no generic "an error occurred" wrappers, no swallowed exception detail), `source` matches a real module name, and `remedy` is present wherever there's an actual next step to suggest.
- [ ] **If the change diverges from or refines a decision in `revit/docs/PRD.md`, is the PRD updated in the same PR** — both the markdown file and the published artifact (see below)? A PR that silently drifts from the PRD without updating it is the kind of thing that makes the doc stop being trustworthy.
- [ ] If the change adds a new corpus test case, is it added under the right category (tutorial-sourced vs. competitive-coverage-floor, PRD §13)?

## Keeping key documents updated

- **`revit/docs/PRD.md`** is the source of truth for design decisions. It also exists as a published, designed artifact (built for review/sharing, not just the raw markdown) — when the markdown changes in a way that matters, republish the artifact from the same source too, so the two don't silently diverge. Small wording fixes don't need a republish; resolved gaps, new design decisions, or roadmap changes do.
- **`CONVENTIONS.md`** — update when a naming or process convention changes, or when a second connector is added and something here turns out not to generalize the way it was assumed to.
- **This skill file** — update when the actual development process changes: a new test tier gets added, a tool gets swapped out, the PR checklist grows or a check turns out not to matter. Don't let it go stale while the real process moves on.

## Keeping this skill current

This file is expected to change as the project learns things — that's the point of it being a skill in the repo rather than a one-time PRD section. When you find yourself doing something differently than this doc describes, or discover a better tool/script/check, update this file as part of that same piece of work rather than leaving the doc wrong for the next person (or the next session). Log what changed and why below, briefly — this is a working log, not a formal changelog, so keep entries short.

### Change log

- *(initial version — created alongside the PRD, before any implementation exists yet)*
- Added the shared diagnostic-record shape (PRD §01) and a PR checklist item enforcing it, after realizing the observability principle had no concrete format standard behind it — several ad hoc reporting shapes existed (Failures API list, window-inventory diagnostic) with nothing tying them together.
- Added the autonomous per-stage workflow (questions up front → implement → classify groundbreaking/additive → `/simplify` for groundbreaking → open PR → independent Opus (groundbreaking) or Sonnet (additive) code-review agent → report, no auto-merge). This is meant to run without per-step check-ins once the up-front questions are resolved.
