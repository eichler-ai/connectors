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
  - **`prlctl exec` runs as `NT AUTHORITY\SYSTEM`, not the interactive user** — `taskkill /IM Revit.exe /F` works fine (killing doesn't need a session), but a plain `start "" "...\Revit.exe"` launches Revit into the non-interactive Session 0 (`tasklist` shows it under `Services`, not `Console`) — invisible, no real desktop, no usable UI thread for live testing. Don't attempt `prlctl exec -u <user> ...` to work around this — it prompts for that user's password, which is out of scope to handle through this channel.
  - **To relaunch Revit into the actual interactive desktop session without a password**, use a one-shot Scheduled Task with the interactive-token flag, which reuses the already-logged-in user's existing session token rather than performing a fresh logon:
    ```
    prlctl exec <vm> cmd /c "schtasks /create /tn LaunchRevit /tr \"cmd /c set MCPBRIDGE_BROKER_MODE=remote&& set MCPBRIDGE_SHARED_ROOT=\\\\psf\connectors&& start \\\"\\\" \\\"C:\Program Files\Autodesk\Revit 2027\Revit.exe\\\"\" /sc once /st 23:59 /ru <username> /it /f"
    prlctl exec <vm> cmd /c "schtasks /run /tn LaunchRevit"
    prlctl exec <vm> cmd /c "schtasks /delete /tn LaunchRevit /f"
    ```
    Set any env vars the add-in needs (e.g. remote-mode broker discovery) *inline in the same launch command*, not via a prior `setx /M` — a scheduled task's own launching process (the Task Scheduler service) has its own long-lived environment snapshot from boot and generally won't pick up a machine env var change made after that, even though it's technically spawning a "new" process. Confirm the relaunch actually landed in the interactive session with `tasklist | findstr /i revit` (look for `Console`, not `Services`).
- **Dev-loop add-in signing** — `MCPBridge.AddIn.csproj` has a post-build MSBuild target (`MCPBridgeSignDevBuild`) that signs the built DLL automatically with a local self-signed cert, eliminating Revit's "unverified publisher, Load Once/Always Load/Do Not Load" prompt on every rebuild during iterative live testing (real, repeated friction discovered during Phase 1 live-wiring validation — every content change re-triggers Revit's own per-file trust decision, since it's keyed by file identity, not just publisher). One-time setup per machine: `powershell -ExecutionPolicy Bypass -File tools\New-DevSigningCert.ps1` — creates a code-signing cert in `Cert:\LocalMachine\My` and trusts it in `Cert:\LocalMachine\Root`/`\TrustedPublisher`. Use `LocalMachine`, not `CurrentUser`, stores: builds driven via `prlctl exec` run as `NT AUTHORITY\SYSTEM`, and `LocalMachine` stores are both writable by SYSTEM and trusted system-wide regardless of which user later runs Revit — `CurrentUser` stores are scoped to whichever account created them, which silently breaks either the write (SYSTEM can't `X509Store.Add()` to its own `CurrentUser\Root` — throws "the request is not supported") or the trust (Revit-as-`nicholas` never sees a cert only trusted for a different user). Opt out per-build with `-p:MCPBridgeSignDevBuild=false` (the target `ContinueOnError`s if the cert isn't set up, so a fresh checkout/CI still builds fine, just unsigned). **Not** the PRD §12 production signing plan (a CA-issued, publicly-trusted, timestamped cert for real distribution) — this cert is trusted only on the machine it was generated on.
  - Multi-targeted csproj gotcha (`<TargetFrameworks>`, plural, even with only one entry — see the multi-target rationale comment in this csproj): `AfterTargets="Build"` fires on both the outer cross-targeting orchestration pass and the inner per-`TargetFramework` pass, but `$(TargetPath)` is only populated in the inner one. Guard any such post-build target with `Condition="'$(TargetFramework)'!=''"` or it silently gets an empty path on the outer pass.
  - XML comments still can't contain `--` (MSB4025) — this bit twice in one sitting while writing this target's own doc comments; use `;` instead, same as the earlier `CopyLocalLockFileAssemblies` comment already had to.
- **`revit/install/`** — installs the built add-in DLL + `.addin` manifest into Revit's per-version Addins folder, and registers the Revit MCP Server with Claude's MCP client config. Use this rather than manually copying files when testing an install end-to-end.
- **`revit/test-harness/runner/`** — orchestrates VM/Revit lifecycle plus the corpus run; this is what "re-run the corpus" actually means mechanically.
- **Shared drive (`Z:` in the dev VM)** — backs remote-mode file exchange and broker discovery (PRD §05, §09). If it stops resolving, check the Parallels shared-folder config before assuming a code bug.

### Add-in deployment location — only two are valid, and Revit fails silently on the wrong one

Revit's `AddInLoader` recognizes exactly two manifest locations per version, and treats everything else as if it doesn't exist — **no error, no dialog, `OnStartup` simply never runs**:
- All-users: `C:\Program Files\Autodesk\Revit\Addins\<version>\`
- Per-user: `%AppData%\Roaming\Autodesk\Revit\Addins\<version>\`

**`C:\ProgramData\Autodesk\Revit\Addins\<version>\` is NOT a valid location**, despite looking like a plausible "all-users" path and despite `xcopy`/manual deployment there succeeding without complaint. During first live-wiring validation, every rebuild/redeploy cycle for several hours targeted `ProgramData` while a stale, hours-old copy of the add-in sat undisturbed in the per-user `Roaming` folder from an earlier test — Revit loaded only the stale `Roaming` copy every time, so identical symptoms (the same `FileNotFoundException`) persisted across multiple genuinely-different code fixes, each of which was actually correct but never reached the binary Revit was loading. **Pick exactly one of the two valid locations and always deploy there** — don't let both accumulate copies, since a stale one will win silently if the fresher one happens to be invalid or the deploy to it partially failed.

To confirm which manifest location(s) Revit actually recognized on a given launch, check its journal (`%LocalAppData%\Autodesk\Revit\Autodesk Revit <version>\Journals\journal.NNNN.txt`) for a line like:
```
Add-in manifest file from: <path>\<name>.addin, won't be loaded. All-users Add-in manifest files must be installed to: C:\Program Files\Autodesk\Revit\Addins\<version>
```
If that line appears for the path you just deployed to, nothing else about the deploy matters until the location itself is fixed.

### Verifying you're actually debugging the binary you just built

Don't assume a redeploy landed where Revit will load it from, or that Revit is running the DLL you just built — verify it directly, every time symptoms don't match a code change:
- **Log the loaded assembly's own identity as literally the first statement of `OnStartup`**: its `Assembly.Location` and `File.GetLastWriteTime` (or `FileInfo(...).LastWriteTime`) against that path. Compare against the build output's own timestamp before treating any subsequent log line (or its absence) as meaningful. A "diagnostic that should fire but doesn't" is otherwise indistinguishable from "the diagnostic isn't in the binary Revit actually loaded."
- **File locks can silently defeat `xcopy /Y`.** `RevitWorker.exe`/`RevitAccelerator.exe` (not just `Revit.exe`) can hold a lock on a deployed DLL even after `Revit.exe` itself is killed. Kill all three before redeploying, and `del /F` the target before `xcopy` so a lock shows up as a loud failure instead of a silent no-op.
- Any temporary `File.AppendAllText`-style diagnostic logging added for a debugging session must be stripped back out once the real fix is confirmed working — it's scaffolding, not something that belongs in a merged PR (don't hardcode a dev machine's user-specific path into the add-in either).

### `register`'s document list is a one-shot snapshot, not live-updated

The add-in sends `register` once per successful connect (first connect and every reconnect), with whatever documents are open *at that instant* — there's no live push when a document opens/closes mid-connection (Phase 1 scope). If you need a real `document_id` for live testing and you connected before Revit finished opening a document (e.g. one passed as a launch argument, which opens after add-ins load and after the connect race typically wins), the only way to get an updated `register` today is to force a reconnect — e.g. restart the broker process so the add-in's reconnect loop redials and re-snapshots. Confirm the document is actually open first (screenshot or journal check) before doing this, or you'll just get another empty list.

## Per-stage workflow (autonomous)

Each development stage — a roadmap phase (PRD §15) or any other discrete scope of work — runs this pipeline. Once step 1 is resolved, the rest runs without further check-ins; status lives on GitHub (the PR and its review comments), not in a running report back to the user.

1. **Questions up front, once.** Before starting implementation on a new scope of work, batch and ask whatever clarifying questions it actually needs — don't trickle them out mid-implementation. Once answered, proceed through the rest of this pipeline autonomously. The only thing that should interrupt it after this point is a genuine blocker — a decision only the user can make, not a judgment call this skill or the PRD already settles.
2. **Implement via subagent(s), TDD-first**, per the testing strategy above. The orchestrating session doesn't write implementation code directly — it delegates to a subagent (or several) so its own context stays free for spec alignment and review rather than filling up with implementation tool-call noise, and it's the one that checks the result against the PRD before moving on.
   - **MCP Server work runs in an isolated git worktree** (`isolation: "worktree"`) — it's pure Go with no dependency on the VM-mounted shared folder, so full isolation is free.
   - **MCP Bridge work runs in the main checkout, not a worktree** — the Windows VM's shared folder is bound to this repo's actual path (`\\psf\connectors\`, see PRD §05/§09), and a worktree would land somewhere the VM can't see, breaking the build. This is a real constraint, not a preference — don't isolate Bridge work into a worktree without first re-sharing the worktree's path into the VM.
   - **Independent parts of a stage run in parallel.** MCP Server and MCP Bridge touch disjoint directories, so their subagents can run concurrently even though only one of them is worktree-isolated.
   - Live end-to-end validation (an actual script running against a live Revit document, per each phase's roadmap "Success" criterion) needs *both* components built and running together — that's an orchestrator-level step after the subagents return, not something to ask a single subagent to do alone.
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
- Implementation now delegates to subagents (worktree-isolated for MCP Server; main-checkout for MCP Bridge, since the VM's shared folder is bound to that specific path) instead of the orchestrating session writing code directly — keeps the orchestrator's context free for spec alignment and review. Independent parts of a stage run in parallel.
- Documented the interactive-Revit-relaunch technique (Tools & scripts) after `prlctl exec`'s default SYSTEM-context launch put Revit in a non-interactive session during first live-wiring validation — a one-shot Scheduled Task with `/it` reuses the logged-in user's session without needing their password.
- Added "Add-in deployment location" and "Verifying you're actually debugging the binary you just built" (Tools & scripts) after a multi-hour debugging session chased a real, already-fixed assembly-resolution bug through several correct-but-never-tested code changes, because every redeploy targeted `C:\ProgramData\...` — not a valid Revit add-in location — while Revit kept silently loading an untouched, hours-stale copy from the per-user `Roaming` folder instead. The fix (only one valid location, verify via the loaded assembly's own logged identity, check the journal for the "won't be loaded" line) is meant to make this class of "identical symptom despite a genuinely different fix" mistake fast to rule out instead of the multi-hour rabbit hole it was this time. Also documented `register`'s one-shot-snapshot timing (Tools & scripts), discovered live: a document opened via launch argument is often still loading when `register` fires, so its `document_id` doesn't appear until something forces a reconnect.
- Added "Dev-loop add-in signing" (Tools & scripts) after re-clicking Revit's unverified-publisher security prompt dozens of times across one debugging session became the dominant source of friction: a post-build MSBuild target now auto-signs with a local self-signed cert, one-time setup via `tools/New-DevSigningCert.ps1`. Documented two real gotchas hit while building this: `CurrentUser` cert stores silently don't work when the build runs as SYSTEM (via `prlctl exec`) but Revit runs as the interactive user — must use `LocalMachine` stores instead; and `AfterTargets="Build"` on a `<TargetFrameworks>` (plural)-style csproj fires on an outer pass where `$(TargetPath)` is empty, needing a `'$(TargetFramework)'!=''` guard.
