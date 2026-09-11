# Rhino Connector — Implementation Plan

Companion to [`PRD.md`](PRD.md). The PRD says what and why; this says in what order, in what size PRs, and what test proves each step. It follows the Revit connector's process (`.claude/skills/revit-connector-development/`) and `CONVENTIONS.md`'s testing philosophy — two tiers, no mocked middle tier — with the differences Rhino makes called out where they apply.

- **Drafted:** 2026-09-09, against PRD.md at the same commit
- **Status:** plan; nothing built

## Principles carried over, and two that change

From the Revit skill, unchanged: a green run is not evidence, assert on the test count; a test that cannot fail is not coverage, revert the fix and watch it fail; `public` means script-reachable in the bridge's assemblies; the acting connection's identity travels with the action; every retained buffer states its bound; a specification is not behaviour, cite the code or a run; verify a claimed impossibility live before writing it into `skill.md`.

Two things are better than Revit's, and the plan exploits both:

1. **C# tier-1 runs in CI.** `RhinoCommon.dll` is a managed assembly from NuGet, not a mixed-mode C++/CLI one, so a test assembly can reference it and still load under `dotnet test` on Linux and macOS. Revit's tier 1 never ran in CI (issue #39). Rhino's runs on every push. The constraint that remains: most RhinoCommon *calls* P/Invoke into Rhino's native core and throw without a live Rhino, so tier 1 references RhinoCommon for types and compilation only, and every behaviour under test sits behind the `Core`/`RhinoAdapter` seam with fakes — exactly the discipline Revit's seam already enforces, now checked by CI instead of by convention.
2. **Tier 2 runs on the dev machine.** The live harness launches against a Mac Rhino with no VM. That makes the harness cheap enough to run per PR for the phases that need it, not only before releases.

## Tiers

| Tier | What | Where it runs | Gate |
|---|---|---|---|
| 1 — Go unit | Every server package; table-driven; `go test -race ./...` | CI, every push | PR merge |
| 1 — C# unit | `MCPBridge.Core.Tests` (xUnit) behind the adapter seam; `MCPBridge.Discovery.Tests` against the real `RhinoCommon.dll`/`Grasshopper.dll` metadata and XML | CI, every push, macOS + Windows runners | PR merge |
| 1 — Python guard | The Python AST denylist walk, tested as a pure C# unit over script text (no interpreter) | CI | PR merge |
| 2 — live harness | `rhino/test-harness/` (Go, `-tags harness`): spawns the real server, speaks MCP over stdio, against a running Rhino | Dev Mac per PR for phases marked *harness-gated*; Windows machine before each release | PR merge for gated phases; release for Windows |
| 2 — how-to sweep | `TestHowToSweep` / `TestHowToEndToEnd`, per version × platform × language | Before any release carrying a corpus change | Release |

Type-check only in CI for tier 2 (`go vet -tags harness`), as Revit does. **What CI cannot run is stated in the PR**, every time, with the harness output pasted — the Revit rule.

## Phase 0 — Shared server packages

**Goal.** One copy of the app-agnostic Go code, imported by both servers.

**Work.** Move `transport`, `diag`, `registry`, `semsearch` (+ `manager`, `models`, `crossenc`, `staticembed`), `howto`, `howtosearch`, `updatecheck`, `selfcheck`, `buildinfo` from `revit/mcp-server/internal/` to a repo-level `internal/connectors/` module. `execution`, `discovery`, `broker`, `singleton`, `mcpserver` stay Revit-specific for now; the Rhino server will re-derive what it needs from them rather than share code whose shape is the singleton's.

**Tests.** No new tests. The existing Go suites move with the packages and must pass unchanged; the Revit harness is run once after the move as the proof that nothing observable changed.

**PRs.** One per package group, mechanical, reviewable by diff-stat: (a) `transport` + `diag` + `registry`; (b) `semsearch` tree + `howto` + `howtosearch`; (c) `updatecheck` + `selfcheck` + `buildinfo`. Each ends with `go build ./... && go test -race ./...` in both `revit/mcp-server` and the new module, and the Revit CI job pointed at both.

**Exit.** Revit server builds and its harness passes with zero Rhino code present.

## Phase 1a — Spikes (verification before design commits)

The PRD's §17 items 1–3 block phase 1. Each is a throwaway harness-shaped Go test plus a minimal plug-in, run on the Mac, whose *finding* is committed as a note in `rhino/docs/spikes/` and whose code is not.

1. **CPython host.** From a plug-in command, `Rhino.Runtime.Code.RhinoCode.RunScript` with `#! python 3`: confirm real CPython 3, stdout capture, `Outputs` binding, behaviour when invoked off the main thread, and whether a `while True` script can be interrupted. The answer decides §06's cancellation contract for Python.
2. **Plug-in TFM.** Which single TFM loads on the current 8.x SR on both platforms.
3. **Undo records.** `BeginUndoRecord` → changes → `EndUndoRecord` → `Undo()`: does it revert exactly the record; is an empty record elided; what happens to `Save` mid-record.

**Exit.** Three notes with pasted output; PRD §06/§07/§13 amended where the findings differ.

## Phase 1 — Core loop, Rhino 8, macOS *(harness-gated)*

**Goal.** PRD §18 phase 1's acceptance.

**Plug-in structure**, mirroring Revit's: `MCPBridge.Core` (all decision logic), `MCPBridge.RhinoAdapter` (thin interfaces + real adapters, `internal` types), `MCPBridge.PlugIn` (`PlugIn` subclass, listener thread, commands), `Eichler.Connectors.Rhino` (the one public `Connector` type). Ported from Revit with the transaction machinery removed: `NdjsonLineBuffer`, `JsonRpcRequest`, `DiagnosticRecord`, `ExecutionManager` + `ExecutionRingBuffer`, `RequestDispatcher`, `RoslynScriptRunner` + `ScriptApiDenylist` + collectible ALC, `ReturnValueFormatter`, `ScriptConsoleCapture`. New: `InstanceListener` + `InstanceFile` (§05), `PythonScriptRunner` over `Rhino.Runtime.Code`, `PythonScriptGuard` (AST walk), `UndoRecordScriptExecutor` (§07) with `MutationTracker` over `RhinoDoc` events, `MainThreadDispatcher` over `RhinoApp.InvokeOnUiThread`.

**Server structure.** `rhino/mcp-server/` importing the shared module; new `internal/dialer` (directory scan, per-instance connection, backoff, stale-file cleanup), `internal/execution` (routing only — no busy latch, no grace escalation; those are answered by the plug-in), `internal/mcpserver` (tools with `language`, `last_run`).

**Tier-1 tests to write** (each named for the shape it pins, per the skill):

- Go: `dialer` — scan finds live files, ignores dead PIDs and deletes them, reconnects on drop with backoff, two dialers to one fake listener; `execution` — execution-id namespacing across servers, routing by instance, wire-failure passthrough; `mcpserver` — schema fingerprint, `language` required, tool result shapes, error `IsError` mapping; the skill.md token-budget test.
- C#: `InstanceFile` written `0600` with all fields; `ExecutionManager` state machine (pending/running/busy/cancel/grace → unrecoverable) ported with its tests; `PythonScriptGuard` and `ScriptApiDenylist` — every shape from Revit's test list plus the interactive-getter block (`RhinoGet.*`, `GetObject`, `rs.Get*`, aliased imports, `getattr` strings for Python), the undo-record members, `RunScript` literal command tokens for both hard-blocked and gated tiers; `UndoRecordScriptExecutor` against fakes — record opened before run, closed after, `Undo()` called on throw only when the top entry is ours, `script-rollback-skipped` otherwise, `Settle` semantics; `MutationTracker` netting; `DiagnosticRecord` shape.
- The mutation test for every guard: revert, watch it fail, restore.

**Tier-2 cases** (`rhino/test-harness/`, one file per concern as Revit): `TestCreateObjects` both languages; `TestFailedScriptIsRolledBack`; `TestCancelResolvesCancelled`; `TestNonCooperatingScriptGoesUnrecoverable` (Python and C#, the Python one asserting whatever spike 1 found); `TestTwoServersOneInstance` (the harness spawns two servers, runs from A, polls from B); `TestServerRestartKeepsPollable`; `TestInteractiveGetterIsRefused`; `TestDocumentIdRouting` on a Mac with two documents; `TestLastRunIsReported`; `TestBridgeCapabilitiesAreNotReachable` (the round-1/2/3 bypass shapes, both languages).

**Viewport capture (PRD §11) lands in this phase, early.** It is the agent's eyes for every phase after it, and it is the developer's too: a harness case that captures the viewport after each step is the cheapest way to see what a failing geometry test actually produced. `IViewCapture` in the adapter seam; `ViewCaptureService` in `Core` owns the bounds (default 1280 px, cap 2048), the temporary zoom/isolate conduit with its restore-and-report, and the `all` fan-out; the `capture_view` tool in the server maps the PNG to MCP image content and the full-resolution file to `files[]`.

- Tier 1: bounds and downscale math; option validation (`display_mode` names, `zoom` variants, mutually exclusive `width`/`height`/`scale`); the restore-after-capture path including the failure notice, against a fake capture that records the conduit's lifetime; the server's image-content mapping and its size guard.
- Tier 2: `TestCaptureViewReturnsImage` (decode the PNG, assert dimensions and that it is not blank), `TestCaptureIsolateLeavesNoUndoEntry`, `TestCaptureAllViewports`, `TestCaptureWhileBusyReportsBusy`; and a harness helper, `captureForDiagnostics(t)`, that every geometry case calls on failure so the PNG lands beside the test output.

**PRs.** (1) plug-in skeleton + listener + instance file + auth, with the dialer and `list_instances` — first harness green light; (2) C# execution end to end (Roslyn port, undo executor, denylist); (3) `capture_view` for viewports, plus the harness diagnostic helper; (4) Python execution (host, guard, capture); (5) cancellation, grace, ring buffer, `last_run`; (6) `skill.md` + `get_skills`. Each harness-gated with output pasted.

**Exit.** PRD §18 phase 1 acceptance, from two concurrent Claude Code sessions, on a Mac.

## Phase 2 — Windows

**Work.** Windows CI job building the plug-in; a live harness pass on a Windows machine; `Win32WindowInventory` (`EnumWindows`) and `MacWindowInventory` behind one **diagnosis-only** `IWindowInventory` (no auto-dismiss — §08 v1 takes no action); owner-only ACL on the instance file. **Note (2026-09-11):** the Mac side uses Core Graphics `CGWindowListCopyWindowInfo`, **not** `NSApplication.windows` as originally written here — the §08 fallback exists precisely because the main thread may be blocked, so the enumeration must run off the main thread, and AppKit (`NSApplication`) is main-thread-only. `IWindowInventory` bakes in no main-thread assumption and the dispatcher runs it on the connection thread, never the `capture_view` main-thread hop.

**Tests.** Tier 1: `IWindowInventory` policy with a fake; ACL helper. Tier 2: phase 1's suite on Windows, with the single-document-per-instance assertions (`TestDocumentIdRouting` becomes a skip-with-reason on Windows and a new `TestOmittedDocumentIdIsActive` runs on both).

**Exit.** Phase 1 suite green on Windows, pasted into the PR.

## Phase 3 — API discovery *(harness-gated for the ranking corpus only)*

**Work.** Port `DiscoveryCache`/`DiscoveryReflector`/`XmlDocIndex`/`DiscoveryService` and their tests; add `kind = grasshopper | rhinoscript`; the `rhinoscriptsyntax` docstring indexer; both call shapes in `describe_function`; the shared ranking pipeline via the Rhino `dump_members`; `ambiguous-instance-version`.

**Tests.** Tier 1 (the strongest tier here, as in Revit): `MCPBridge.Discovery.Tests` against the real `RhinoCommon.dll` + `.xml` and `Grasshopper.dll` + `.xml` from NuGet — every Revit case (paging, namespace scoping, named indexed properties, add-in visibility, XML doc ids) plus Python call-shape rendering (`out` → tuple, generics, `rs.` wrapper lookup); a labelled query set (`TestRealCorpusRecall` equivalent, 40+ task-style queries, recall@1/3/10 snapshot at depth 10 — the skill's blast-radius rule). Tier 2: one round trip per tool and the version-ambiguity case.

**Exit.** Recall numbers committed; discovery answers for both languages.

## Phase 4 — Grasshopper *(harness-gated)*

**Work.** `gh_document_id` in `register`/`list_instances`/`execute_script`; `IGrasshopperAdapter` (document server, find, set, solve, data) with the real adapter over `Grasshopper.Kernel`; `Connector.Grasshopper`; the solve-event subscriber and report; the bounded data-tree serializer; the Grasshopper canvas target for `capture_view` (`GH_Canvas` image generation, zoom to components); pause/step idioms in `skill.md`.

**Tests.** Tier 1: `Connector.Grasshopper.Set` dispatch by control type against fakes; the data serializer's bounds (item caps, geometry summarised, paths preserved) — the largest new tier-1 surface; the solve-report assembler from a scripted event sequence; the gate on `GH_DocumentIO.Save*`/`Close`. Tier 2: PRD §18 phase 4's acceptance against a shipped example definition, both languages; `TestSolveReportNamesFailedComponent`; `TestSolverDisabledPausesRun`; `TestGhDocumentIdRouting`; `TestCaptureCanvasReturnsImage`.

**Exit.** Phase 4 acceptance pasted; the mid-solve limitation either lifted by a live finding or restated in `skill.md`.

## Phase 5 — File exchange & audit trail

**Work.** Port `WorkspacePaths`, `ExecutionAuditTrail`, `Publish`, `files[]`; `DocumentIdentity` with the `.3dm`/`gh-` rules and macOS realpath resolution.

**Tests.** Tier 1: identity table (saved, unsaved, Grasshopper, symlinked path, case), `Publish` per-file status and overwrite flag, sweep ageing — all ported with their tests. Tier 2: `TestPublishRoundTrip`, `TestImportsDirectoryIsReadable`.

## Phase 6 — How-to corpus

**Work.** Reuse the shared `howto` packages; extend stamps with `platform` and `language`; seed from the harness; `search_howtos`/`describe_howto`/`submit_howto`; `TestHowToSweep`/`TestHowToEndToEnd` for Rhino.

**Tests.** Tier 1: the stamp-dimension extension (a stamp for another language or platform must not count). Tier 2: the sweep on Mac × both languages before the first corpus ships; Windows at release.

## Phase 7 — Distribution

**Work.** The yak package (spike first: size and payload acceptance, §15/§17); `MCPBridgeRegister`; `update_connector` as a check; notarization deferred.

**Tests.** Tier 1: registration JSON writer; version comparison. Tier 2: fresh-machine install on Mac and Windows, scripted where yak allows, manual otherwise, with the steps recorded in `install.md`.

## Sequencing and parallelism

```
0 (shared pkgs) ──► 1a (spikes) ──► 1 (core, Mac) ──► 2 (Windows) ──► 7 (distribution)
                                        │
                                        ├──► 3 (discovery) ──► 6 (how-tos)
                                        ├──► 4 (Grasshopper)
                                        └──► 5 (file exchange)
```

Phases 3, 4 and 5 are independent once phase 1's dispatcher and adapter seam exist and can run in parallel worktrees; phase 6 needs 3 (the corpus is searched by the same index) and a stable harness to seed from; phase 7 needs 2. Phase 2 should not wait for 3–5 — the earlier Windows runs, the earlier a platform-specific surprise costs little.

## PR discipline

- One concern per PR; harness-gated PRs paste the harness output for the cases they add.
- Every guard PR includes its mutation check in the description ("reverted X, `TestY` failed with Z").
- The `skill.md` budget test runs locally before push, per the skill.
- Reviews are a second reader, spawned by the coordinator, never the author's fork; PRs that touch `Core`'s public surface get the "what does this member's body pass outward" read.
- `rhino/docs/PRD.md` is amended in the same PR as any finding that contradicts it — the Revit drift cleanup (#273) is the cost of not doing so.
