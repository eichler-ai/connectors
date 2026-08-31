---
name: revit-connector-development
description: Development process, tooling, testing strategy, and PR review checklist for building the Revit connector (Revit MCP Bridge add-in + Revit MCP Server) in this repo. Use whenever implementing, testing, reviewing, or deploying any part of the Revit connector, or when the process itself needs updating based on what's been learned.
---

# Revit Connector Development

How to build, test, and review this connector. Update this file when the process changes — see
"Keeping this skill current".

Two companion files sit beside this one. **`caveats.md`** is indexed by symptom — read it when
something is behaving strangely and you need to know what it's likely to be, before theorising.
**`dev-environment.md`** covers the Mac + Parallels specifics (`prlctl`, the launcher agent, VM
toolchain, deploy paths); read it before touching the VM.

## Orientation

- **Design source of truth:** `revit/docs/PRD.md`. Read the relevant sections before implementing.
  This file covers *how* to build; the PRD covers *what* and *why*.
- **Naming:** `CONVENTIONS.md` at the repo root. Get MCP Bridge (the add-in) vs. MCP Server (the
  broker) right — PRD §04.
- **Layout:** `revit/mcp-bridge/` (add-in, C#), `revit/mcp-server/` (broker, Go), `revit/install.ps1`
  + `revit/install-mac.sh` (installers), `revit/test-harness/` (live MCP harness),
  `revit/dev-tooling/` (this project's Mac+Parallels tooling, not shipped).
- **Diagnostics:** every JSON-RPC `error.data`, `notices[]` entry, and NDJSON log line uses the one
  record shape in PRD §01 — `severity`, `code`, `source`, `message`, `detail`, `remedy`. `code` is
  kebab-case. `source` is a real module name (`mcp-bridge.core.execution`,
  `mcp-server.internal.registry`), never invented per-feature.

## Engineering rules

These are the rules that generalize. Each one exists because violating it cost real time.

### Verification

- **A green result is not evidence. Assert on what actually ran.** Confirm the test *count*, not the
  exit code: a test assembly that fails to load is skipped entirely and `dotnet test` still exits 0.
  `dotnet test` exiting 1 with no summary line means the test DLL for that TFM does not exist, not
  that tests failed — `Test-Path` the per-TFM `bin\Debug\<tfm>\*.Tests.dll` before believing any
  multi-target result. Where a run's environment is the thing under test, assert on it directly
  (`RuntimeTargetingTests` pins which runtime each TFM leg executes on).
- **A test that cannot fail is not coverage.** After writing a test for a fix, revert the fix and
  confirm the test fails. Multi-targeting, doc markers, and "passing on both legs" have all been
  accepted here as coverage while proving nothing.
- **Measure a change's blast radius at the depth a user sees, not the depth that is convenient.**
  Diffing the top three results of a 79-query ranking corpus missed three degradations at ranks 2-10,
  and two of them were reported as improvements. The snapshot holds ten. A change that fixes rank 1
  while destroying ranks 4-10 is not an improvement, and a diff shallower than the page an agent
  actually reads cannot tell the two apart.
- **Never let the only copy of a result pass through a lossy filter.** Tee the complete output to a
  file and filter the file. A `tail`/`grep` chain has repeatedly destroyed the one line that
  mattered, and an empty filter result reads identically to a pass. In a background pipeline `grep`
  also needs `--line-buffered`, or the output file stays empty until the process exits.
  **This applies hardest to a run you expect to pass.** A one-off failure filtered down to its summary
  line is unattributable: a Core suite went red once on the net8 leg, the filter kept only
  `Failed: 1`, and ten subsequent runs were clean -- so the failing test's name, the only thing that
  would have identified it, was destroyed by the command that observed it. Intermittents are exactly
  the results you get one chance to capture.
- **Prove identity, don't compare names.** To check that two paths are the same tree, drop a
  uniquely-named probe file on one side and look for it from the other. Names legitimately differ
  (a worktree's share name need not match its directory), so a name comparison both false-positives
  and false-negatives.
- **A right conclusion is where wrong evidence hides.** Review checks conclusions, so support that is
  inverted, borrowed or invented survives whenever the conclusion itself is true — twice in one day: a
  "measured, not assumed" worked example with its two SHAs swapped (it read as the *opposite* of the
  bug it documented), and a true "three calls" claim resting on a false reason. Both had been reviewed.
  So: cite only runs you made, paste them with their labels attached, and never re-label someone else's
  numbers as your own measurement. Evidence marked "measured" carries an obligation — re-derive it
  before it becomes a comment, because a comment's whole job is to stop the next person re-deriving it.
- **Confirm you are running the artifact you just built.** Byte-grep the *deployed* binary for a
  string unique to your change, decoding at both byte alignments (the `#US` heap stores UTF-16
  literals at arbitrary offsets, so a single-alignment decode gives false negatives). Timestamps
  prove nothing — a copy step refreshes them.

### Boundaries and guards

- **In `MCPBridge.Core`, `MCPBridge.RevitAdapter` and `Eichler.Connectors.Revit`, `public` means
  script-reachable.** `RoslynScriptRunner.LoadableReferences()` hands script scope every assembly in the
  Revit AppDomain, and explicitly appends the third one. A public type must neither *be* an
  adapter/capability type nor *return or yield* one — directly, or through a caller-supplied callback or
  delegate. Default new types to `internal`; when reviewing a `public` type here, read its members'
  **bodies** for what they pass outward, not just signatures. This also covers types that decide
  **policy**, not just those holding capability.
  - **The one exemption, stated narrowly because it is easy to over-apply.** `ScriptGlobals`' own bound
    globals ARE the script API by definition — Roslyn requires that type and its bound members to be
    public — so `public Connector Connector { get; }` is not a violation. The exemption stops exactly
    there: it covers the globals type's own bound members and nothing they lead to. Every member of
    `Connector`, and anything `Connector` returns, is held to the rule in full. Adding
    `public void ForEachDocument(Action<Document> f)` to `Connector` would be the round-2 exploit shape
    (a public member handing a capability to script-supplied callback code) and no test in the repo
    would catch it.
- **Close a hole structurally, not by extending a list.** A name-based table is only as complete as
  whoever last remembered to extend it. Making a capability *unnameable* is usually cheaper than
  teaching a guard to judge — check first whether public members already return interfaces, in which
  case the fix is one word per file.
- **When a hole is closed, look beside the boundary, not just at it.** Ask what the *class* of bug
  is and sweep for siblings. Three review rounds here each found a new instance of "the same bug"
  one rung out from the last.
- **A guard that only fires incidentally is not a guard.** If the thing that actually stopped an
  exploit was a third party's invariant, you are not enforcing anything.
- **A comment asserting a negative capability is worth nothing until someone tries it.** Verify
  live rather than reasoning from the code; reasoning has concluded the opposite of reality here.
- **A test pinning one exploit's syntax is not coverage of the hole.** Name the *shape* being
  blocked (target-typed construction, method groups, callbacks, aliases), and when adding a case,
  say which shape it adds.
- **When a fix makes a feature's own plumbing `internal`, pin the FEATURE too, not just the exploit.**
  A test proving the tampering stopped compiling cannot fail if an `InternalsVisibleTo` grant is wrong
  in a way the compiler tolerates; only a test exercising the feature across that seam can.

### Correctness

- **Never compare Revit API objects with `ReferenceEquals`.** Revit mints a fresh managed wrapper per
  call site; `Application.Documents` and `ActiveUIDocument.Document` return different wrappers for
  one document. Compare PRD §09 identities (`DocumentIdentity.ResolveCached`). One limit: a document
  mid-transition whose `Title` accessor throws falls back to a per-wrapper GUID, so an id comparison
  against such a document is best-effort.
- **Assume a `BuiltIn*` enum used for parameter TYPING or GROUPING has a `ForgeTypeId` replacement**
  in Revit 2025+ (`ParameterType` → `SpecTypeId`, `BuiltInParameterGroup` → `GroupTypeId`); don't
  assume it still compiles. The qualifier matters: `BuiltInCategory` and `BuiltInParameter` are
  unaffected and still work, so don't go hunting for a `CategoryTypeId` that doesn't exist.
- **The acting connection's identity travels with the action** — no broker mutation keyed by a
  logical id alone; guard at the point of mutation (`CONVENTIONS.md`).
- **Every retained record or buffer has a stated bound** at its declaration, mirrored on both ends of
  a wire (`CONVENTIONS.md`).
- **An advertised-but-unimplemented parameter must error loudly**, never silently retarget. A loud
  gap stays known; a silent one gets trusted.
- **Exclusions outlive their reasons.** When a change makes a previously-unreliable value reliable,
  search for every site that special-cased the old unreliability — those guards become the new bug.
- **A test client's deadline must exceed the deadline of the thing it tests**, or you are testing
  your own patience. A client giving up early presents as a wedged/`busy` server.
- **Anything a script backgrounds must be detached from the script's own stdio**
  (`</dev/null >>log 2>&1`, backgrounded from the outer shell), or every pipeline consumer of that
  script inherits the daemon's lifetime and hangs.

### Working in agent sessions

- **Use absolute paths in every command**, and chain `cd <dir> && <cmd>` inside a single invocation —
  the shell's cwd resets between calls, and a wrong-directory failure reads exactly like a
  wrong-branch one. `git worktree add` is the sharpest case: given a relative `../name` with a
  drifted cwd, it fails silently — the worktree lands nested inside the repo instead of erroring.
- **Never put a destructive command after `;`** in a chain whose earlier `&&` parts can fail; the `;`
  part runs anyway. In `zsh`, an unmatched glob is a hard error that breaks `&&` chains.
- **Commit or stash before any mutation-test or restore dance** — a reflexive `git checkout <file>`
  destroys uncommitted work. Fetch before branching: a stale local `main` silently reverts merged
  work. Delete a PR branch only after `gh pr view` says MERGED.
- **A brief scoped to one component doesn't bound the change.** Before calling a feature done, check
  whether a *different* component encodes assumptions about the one you changed — the Go broker's
  structs once silently dropped every field of the add-in's new response shape, with each side
  correct in isolation.
- **A live test that COUNTS across all open documents can still fail on a re-run in one Revit
  session**, even with cleanup in place: a stray survivor from an earlier interrupted run, or the
  operator's own documents in that same interactive session, both count too. Reads exactly like a
  double-commit bug. Derive expected values per run and scope live checks to specific documents by
  `Title`.
- **Forked subagents cannot spawn their own reviewers** — the coordinator spawns the independent
  review. A fork's self-review misses what an independent pass catches.
- **Run the fast test nearest the file you just edited, immediately** — the token-budget test for
  `internal/mcpserver`'s embedded `skill.md` is sub-second and local, so an overage should never be
  found two steps later.
- **Parallel PRs**: work from `gh pr diff` or a dedicated worktree; never switch the main checkout's
  branch, which is bound to the VM share. Two PRs both appending to a shared file will conflict —
  resolve keep-both.

## Testing strategy

Two tiers, deliberately — no mocked-broker middle tier. Add one only if bugs start appearing
specifically at the broker↔add-in wire that neither tier catches.

### Tier 1 — unit tests

**MCP Server (Go): unit-test everything, TDD-first.** Pure logic, zero Revit dependency — protocol
framing, tool routing, registry/heartbeat state machine, singleton lock-or-proxy, path translation
(PRD §05/§09). Table-driven tests; `go test ./...` from `revit/mcp-server/`.

**MCP Bridge (C#): unit-test everything behind the `Core`/`RevitAdapter` seam.** Raw Revit-API code
(`ExternalEvent` plumbing, `DialogBoxShowing`, `Transaction` wrapping) is not meaningfully
unit-testable — Revit types are sealed/non-constructible outside a live session. Decision logic
belongs in `MCPBridge.Core` behind `MCPBridge.RevitAdapter` interfaces, testable with fakes:

- Dialog default-answer policy (§07); failures-API resolution policy (§07)
- Document identity — the four-state scheme (§09)
- Cancellation state machine (§06)
- Discovery reflection + pagination (§08) — fully testable, since reflection runs against static
  `RevitAPI.dll`/`.xml` files, no fakes needed at all

xUnit. **A test assembly must never reference `RevitAPI` directly** — it is a mixed-mode C++/CLI
assembly only Revit's own host can load, and referencing it makes the runner skip the whole assembly
silently. Keep raw-object accessors on separate capability interfaces that only real adapters
implement; where a test needs Revit types bindable from script scope, pass them to Roslyn as
metadata references by path.

**A static field can drag `RevitAPI` in by itself.** If a type's *other* statics reference Revit types
(e.g. a `ConditionalWeakTable<Document,...>`), the whole type initializer loads `RevitAPI.dll`, which a
tier-1 host cannot do — so anything meant to be tier-1-reachable must not share a type initializer with
Revit-typed statics. Put new static state in a nested holder type (see `DocumentIdentity`'s).

### Tier 2 — live harness

Everything needing a running Revit: `ExternalEvent` firing, real transaction commit/rollback, real
modal dialogs, the full add-in↔broker↔agent round trip. `revit/test-harness/` is a Go module that
spawns the real `mcp-server` binary and speaks real MCP JSON-RPC over its stdio
(`go test -tags harness`). It assumes Revit is already running and skips cleanly if not.

**Anything asserting on what a script gets from `ScriptGlobals` belongs here by construction** —
those are real Revit types now (PRD §14). Tier 1 can still assert everything compile-time about
scripts (denylist checks, globals binding), since Roslyn only needs metadata.

Run it before merging anything touching threading (§06), dialogs/failures (§07), discovery (§08), or
file exchange; before every corpus regression pass; and before cutting a release.

**Iterate with `-run <Case>` (Go) or `--filter <TestClass>` and one `-f <tfm>` (C#); run the full
suite once before opening the PR.** The C# default runs every suite twice, once per TFM. The dual
run stays mandatory at PR time — it has caught a real per-TFM difference — just not per edit.

## Script API surface — the denylist principle

`ScriptApiDenylist` is a guard, not a sandbox. Every script already runs inside an ambient
`Transaction`/`TransactionGroup`, so a thrown exception rolls back document content for free. The
denylist covers only the surface where that guarantee doesn't hold. One question decides membership
and tier: **does a thrown exception in the script actually undo this?**

- **Hard-blocked, no override** — no, because the problem is structural: constructing
  `Transaction`/`TransactionGroup`/`SubTransaction` violates the one-open-transaction-per-document
  invariant. No confirmation parameter rescues this; it conflicts with the execution model itself.
- **Confirmation-gated** — no, because the effect escapes the transaction boundary entirely: it
  changes a human's session (`Close`), the filesystem (`Save`/`SaveAs`), a shared central model
  (`SynchronizeWithCentral`), a device (`Print`), or another user's ability to edit
  (`RelinquishOwnership`). Gate with an explicit confirmation parameter on `execute_script`.
- **Unrestricted** — yes. Ordinary `Document`/`Element` mutation is covered by the ambient
  transaction.

Run any proposed change through that test, not "does this feel risky."

**Enforcement binds to the Roslyn semantic model (resolved symbols), never source text.** A
syntax-shape pass had two live bypasses that reached a real document — a target-typed
`new Transaction(...)` and a `Document.Close` method group. Check any new rule against those shapes
before considering it closed.

**When a guard is over-broad because it cannot see intent, give the connector the job the script was
reaching for.** The compile-time check cannot know which document a `Transaction` targets, so it also
refused a document the script had just created — which Revit allows. Rather than narrow the check,
the connector now owns a transaction for every document a script creates
(`CreateProjectDocument`/`CreateFamilyDocument`, and `OpenForWriting` for an existing one). The rule
stayed unconditional, no bypass surface appeared, and a bug in the new plumbing breaks a feature
rather than a security boundary.

**Reflection still reaches all of this, and that is the accepted guard-not-sandbox line** — including
the connector's own `ManagedDocumentTransactions`, i.e. commit/rollback authority over every managed
document. The point of every fix here is closing the *plausible-looking, one-line* route.

Bypasses are pinned by `revit/test-harness/denylist_bypass_test.go`, tier 2 by construction —
`internal` only means anything against the real assemblies as Revit loads them.

## Adding a function to the connector's own script API

The connector's script-facing functions live on **one public type**, `Connector`, in
`src/Eichler.Connectors.Revit/` (issue #91). A script reaches them as `Connector.Publish(path)`;
discovery indexes the assembly as an add-in, so `list_functions`/`search_functions`/
`describe_function` return them beside Autodesk's own API under `Eichler.Connectors.Revit`. That
namespace is a released identifier — see `CONVENTIONS.md`, and treat renaming it as a decision, not a
refactor.

To add one:

1. **Implement it in `MCPBridge.Core`**, next to the existing implementations on `ScriptGlobals`, and
   keep it `internal`. Maintainer rationale goes here.
2. **Add it to `IConnectorRuntime` and `Connector`.** The facade member carries the agent-facing XML
   doc comment.
3. **Nothing else enumerates it.** Not `execute_script`'s description, not `skill.md`, not
   `ScriptGlobals.GlobalNames` — those name the `Connector` entry point only, which is what stopped
   the five-way drift #91 was filed for. Do not add it back to any of them.
4. **Migrate call sites** if you are renaming: `revit/test-harness/*.go` script text and the
   validation corpus. One spelling, never two.

Three constraints that are not obvious, each found by violating it:

- **No Revit type may appear in `IConnectorRuntime`.** The CLR resolves every interface signature
  when it loads an implementing type, so a `Document` there makes `ScriptGlobals` unloadable wherever
  RevitAPI.dll cannot load — the entire tier-1 host (114 of 423 tests failed). Type them `object` and
  cast in `Connector`, whose method bodies JIT only inside Revit. A class's own members are resolved
  lazily, per member, which is why `ScriptGlobals` can carry `Document` properties and this interface
  cannot.
- **The XML doc comment is shipped product, not commentary.** `describe_function` returns it verbatim
  to an agent. CS1591 is an **error** in that project alone, so an undocumented public member fails
  the build; `ConnectorApiSurfaceTests` reads the generated sidecar and fails on a summary over 130
  words or one citing PRD sections, issue numbers, or internal type names. Put rationale in
  `<remarks>` — `XmlDocIndex` reads only `<summary>`, `<param>` and `<returns>`.
- **Keep `Connector` the only public type in that assembly.** `DiscoveryReflector` indexes publicly
  visible types, so anything else made public there lands in the agent-facing corpus. That is the
  whole reason the assembly exists rather than syncing `MCPBridge.Core`, which has 71 public types.

Deploying it needs the **`.xml` sidecar beside the `.dll`**, and this is load-bearing rather than
tidy: `DiscoveryReflector` treats a *missing* sidecar as "everything is documented", so a DLL-only
deploy yields discovery with empty summaries — which looks like working discovery. `install.ps1`
copies the whole build output and is fine; `redeploy-and-verify.ps1` copies files **by name** and has
to be extended when a project is added.

**Verify the rendered text live, not the source.** Reading `describe_function`'s actual output is what
caught two defects a source review would have passed: paragraphs concatenating without a separator,
and the deploy list missing the new assembly.

## Revit behaviours that mislead

- **A modal dialog wedges Revit's idle loop**, so any `ExternalEvent` (including the document
  snapshot) never runs, and `register` silently reports `documents: []` while the instance looks
  healthy — only the UI thread is stuck. `execute_script` also needs the idle loop, so it hangs;
  `list_instances` answers regardless. That difference is the liveness probe.
- **Dialogs raised during document *open* are outside anything §07 intercepts** (link-reload warning
  summaries, "File Opened By Another User"). A real coverage gap, not environment noise.
- **`register`'s document list is live** — pushed on every open/close/create/activate. A persistent
  `documents: []` is therefore a real symptom with two causes: Revit was never asked to open the
  document (check Revit's own journal and the process command line, not our logs), or a dialog is
  blocking as above.
- **Revit stamps "in use" inside the .rvt**, so force-killing Revit taints the file permanently and
  copies inherit it. Test against a working copy, re-copied from a pristine original each cycle.
- **Revit refuses `Document.Close` while any transaction is open** on that document.

## Per-stage workflow

Each discrete scope of work runs this pipeline. After step 1 it runs without check-ins; status lives
on the PR, not in a running report.

1. **Questions up front, once.** Batch whatever clarifications the scope needs before starting. After
   that, only a genuine blocker — a decision this skill and the PRD don't settle — interrupts.
2. **Implement via subagent(s), TDD-first.** The orchestrator delegates so its own context stays free
   for spec alignment and review, and checks the result against the PRD.
   - MCP Server work → isolated git worktree (pure Go, no VM dependency).
   - MCP Bridge work → the main checkout, because the VM's shared folder is bound to this repo's
     path. To use a worktree anyway, re-share it first:
     `prlctl set <vm> --shf-host-add <name> --path <worktree-path>`.
   - Independent parts run in parallel; Server and Bridge touch disjoint directories.
3. **Classify the work in the PR description:**
   - *Groundbreaking* — introduces a new architectural pattern or subsystem.
   - *Additive* — extends an established one.
4. **Groundbreaking only: run `/simplify` on the diff** before opening the PR.
5. **Open the PR** (`gh pr create`), following normal git hygiene: no force-push, no skipped hooks,
   no `--amend` on pushed commits.
6. **Deploy an independent code-review agent** — fresh, no shared context, reading the diff itself:
   Opus for groundbreaking, default model for additive. It posts findings to the PR and reports back.
7. **Merging is a human decision.** This pipeline creates and reviews PRs; it does not merge them.

## PR review checklist

- [ ] Unit tests for new `Core`/`RevitAdapter` (Bridge) or `internal/*` (Server) logic, written first.
- [ ] If the change touches threading, dialogs, failures, discovery, or file exchange — was the live
      harness actually run?
- [ ] Naming matches `CONVENTIONS.md` (Bridge vs. Server).
- [ ] New automatic-resolution behaviour follows observability-over-silence (PRD §01).
- [ ] Every new error/notice/log record uses the §01 shape: kebab-case `code`, a `message` naming
      concrete identifiers and the real underlying condition, a real `source`, and a `remedy`
      wherever there is a next step.
- [ ] If the change alters anything `get_skills` describes — tool surface, script globals, file
      exchange, error shapes, connection behaviour — is `skill.md` updated in the same PR, with
      claims verified against the running connector? (Adding a tool fails
      `TestSkillFileDocumentsEveryRegisteredTool`; changing *behaviour* fails nothing, so it's on you.)
- [ ] If the change refines a PRD decision, is `revit/docs/PRD.md` updated in the same PR?
- [ ] Temporary debugging scaffolding stripped — ad-hoc `File.AppendAllText` logging, hardcoded
      machine-specific paths. **Exception:** logging that closes a real observability gap (a silently
      swallowed exception, per §01) stays; if unsure which it is, ask.
- [ ] New corpus cases filed under the right category (PRD §13).

## Keeping key documents updated

- **`revit/docs/PRD.md`** — source of truth for design decisions; keep current with every PR that
  changes one. Its published artifact is frozen as of 2026-08-28; don't republish it.
- **`revit/mcp-server/internal/mcpserver/skill.md`** — what `get_skills` serves. **The one doc an
  agent treats as ground truth**, so drift here makes the connector harder to use than shipping no
  guide. It is embedded in the broker binary, so it is only as current as the last build. Update it
  in the same PR as any change to the tool surface, script globals, file exchange, error shapes, or
  connection/registration behaviour.
  - **It is at its token budget** (`TestSkillFileStaysWithinItsLightweightBudget`, ~1% headroom), so
    adding a paragraph means removing one. Reach for that only after the real question: does this
    belong here at all? Signatures, parameters and per-member behaviour go in XML doc comments, where
    `describe_function` serves them on demand and they cannot drift. `skill.md` keeps what discovery
    cannot express — the transaction model, the gated tier, ordering rules, worked examples.
  - **Verify claims against the running connector, not the PRD.** Where the two disagree, find out
    which one reality matches before "fixing" either — it has been the PRD three times.
  - **Pin topics, not the mechanism of the day.** A doc test requiring specific literals blocks the
    very correction it should permit. Assert that both halves of a claim are present and that false
    claims stay forbidden.
- **`CONVENTIONS.md`** — naming/process conventions, and the cross-connector engineering invariants.
- **Open-source-facing:** root `README.md` (per-connector status line), `revit/docs/quickstart.md`
  (build-from-source + install flags), `revit/docs/tools.md` (mirrors the agent-facing surface, so it
  changes whenever `skill.md` does), `SECURITY.md`, `CONTRIBUTING.md`.
- **Component READMEs:** `revit/README.md`, `revit/mcp-server/`, `revit/mcp-bridge/`,
  `revit/install.md`, `revit/test-harness/`, `revit/dev-tooling/`. Update when a phase completes, a
  project is added or renamed, or a "not yet built" claim stops being true.
- **A docs-sync pass across cross-referencing files introduces its own contradictions.** Do a final
  consistency pass reading the whole diff fresh, separate from per-file correctness review.

## Milestone retrospectives

After any major milestone — a PR series, a roadmap phase, a multi-PR feature — spend a few minutes on
three questions, and act on the answers immediately. Never just a mental note.

1. What broke or hung in the **process** (not the product)?
2. Where did wall-clock go that wasn't thinking or building?
3. What did a reviewer or test catch that the process should have caught earlier or cheaper?

Every answer lands somewhere concrete: a rule, a tooling change, a filed issue, or a `caveats.md`
entry. Question 2 is the one that most often produces a caveat rather than a rule — time lost to a
symptom you misread is a diagnosis worth recording, not a discipline problem to resolve by trying
harder.

## Keeping these files current

Record what a piece of work taught you as part of that work, rather than leaving the docs wrong for
the next session. Three files, and which one depends on the shape of the lesson:

| The lesson is… | Goes to | As |
|---|---|---|
| something to always do, or never do | `SKILL.md` | a rule, stated directly |
| a property of this machine or its tooling | `dev-environment.md` | a mechanism, with the check that reveals it |
| what a symptom turned out to mean | `caveats.md` | a row in that symptom's table |

Each file explains its own format; follow it there rather than inventing a shape.

**Two triggers are easy to miss, because neither produces a diff:**

- **You spent a long time on a symptom that turned out to mean something else.** Record it in
  `caveats.md`'s misdiagnosis table even though — especially though — nothing shipped. A wrong
  hypothesis leaves no commit and no issue, so if you don't write it down, nothing anywhere does. The
  entry that matters is *what finally distinguished the two*, not the story.
- **You found a dead end.** "We tried X, it does not work here" only exists if someone records it;
  otherwise the next session pays for it again. A dead end is none of the three rows above — put it in
  `dev-environment.md` beside the mechanism it defeats, as the existing ones are (UI automation,
  `CloseMainWindow`).

**Prefer sharpening over appending.** If a new lesson is a third instance of a rule already present,
tighten that rule rather than adding a fourth bullet beside it. `SKILL.md` is read in full every
session, so length here has a direct cost; the companions are read on demand, which is what the split
buys. Git history holds the narrative, these files hold the guidance.
