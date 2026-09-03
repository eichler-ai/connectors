# Caveats: what a symptom actually means

Diagnostic knowledge, indexed by **what you observe** — not by when it was learned. `SKILL.md` says
what to do; `dev-environment.md` says how this environment works; this file is for when something is
behaving strangely and you need to know what it's likely to be.

**How to add to this file.** Find the symptom that matches and add a cause to its table, or add a
technique that generalizes. Only start a new section for a genuinely new symptom. Do **not** append
entries chronologically or write "on <date> we found…" — this file replaced a changelog that grew to
10,000 words precisely because appending was easier than merging, and it stopped being read. If your
lesson is a *rule* ("always do X"), it belongs in `SKILL.md` instead; if it's a property of this
machine, `dev-environment.md`. This file is for **symptom → cause** and **how we cornered it**.

## Symptom: "I changed something and nothing changed"

The single most overloaded symptom in this project. Causes are ordered by how cheaply they're ruled
out — work down, don't guess.

| # | Cause | Definitive check |
|---|---|---|
| 1 | The VM is building from a stale local copy, not the share | Byte-grep the built DLL for a string unique to your change |
| 2 | Incremental build no-opped | Rebuild `--no-incremental` |
| 3 | Deploy silently failed on a file lock | Kill `Revit.exe`/`RevitWorker.exe`/`RevitAccelerator.exe`, `del /F` before copying so a lock fails loudly |
| 4 | Deployed to a path Revit doesn't read | Only two locations are valid; check Revit's journal for the "won't be loaded" line |
| 5 | Deployed to the *other* Revit version's folder | Per-version Addins folders drift independently — grep every one |
| 6 | An env var change didn't reach the process | The launcher agent holds a stale environment snapshot; restart the agent, not just Revit |
| 7 | A shadow copy in Revit's install directory is winning | Log every loaded assembly's `.Location` early in `OnStartup` |
| 8 | You deployed from the wrong checkout | `deploy-and-verify.sh --share-name`; its identity guard refuses rather than reporting PASS |
| 9 | The change was **broker-side** and only the add-in was redeployed | Ask the running broker, not the repo: `get_skills`' `build` field, or `<broker> -version`, names the revision compiled in. `skill.md`, every tool schema and every tool description live in the mcp-server binary; no add-in redeploy can move them, and `deploy-and-verify.ps1` never builds Go at all (issue #116) |

**Check for the log's startup line before reasoning about anything after it, in `connection.log`
*and* `connection.log.old`.** A session opens with `RunConnectionLoop starting. Mode=... ConnectorRoot=...`
— but these logs rotate at 5MB (issue #11), so a live log that begins mid-stream has rotated and that
line is in the `.old` file. Its absence from `connection.log` alone does **not** mean the add-in never
started, and only one previous generation is kept: the next rotation overwrites `.old`, so history
older than that is gone rather than archived. `startup-errors.log` rotates the same way.

## Symptom: `register` reports `documents: []`

The snapshot is pushed live on open/close/create/activate, so a *persistent* empty list is real.

| Cause | Definitive check |
|---|---|
| Revit was never asked to open the document | Revit's own journal, and the process command line — not our logs. If the path isn't on the command line, no add-in debugging will explain it |
| A modal dialog is blocking the idle loop, so the snapshot `ExternalEvent` never runs | `execute_script` hangs while `list_instances` answers — that difference is the probe. Then screenshot |

A non-atomically-dropped `*.launch` signal is the classic first cause: read while still empty, an
empty signal legitimately means "no document", so Revit starts with nothing open and correctly
reports zero.

## Symptom: an instance is `busy`, or a call hangs, but Revit looks fine

| Cause | Definitive check |
|---|---|
| The client's deadline is shorter than the server's | Compare them. A client giving up early is indistinguishable from a hung script from the outside |
| A modal dialog has stopped the idle loop | Screenshot. `list_instances` still answers, `execute_script` doesn't |
| Genuinely long work | Revit slows as a session accumulates documents; creating documents is slow |
| The run started seconds after a Revit **restart**, so the first `execute_script` raced Roslyn's cold start (measured: **~6.9s**) | Mostly fixed at source — the add-in logs `script pipeline warm: … execute_script is ready` and `deploy-and-verify` now waits for it before reporting `PASS`. If you still see this, you either launched Revit outside that script or it printed the `WARNING: no 'script pipeline warm' line` fallback. Re-run the failing case warm: passing unchanged means cold start, not a regression |
| `wire-call-failed / context deadline exceeded` on the START (or a poll) of a long-runner, intermittently, worse after a suite's worth of work — the §07 window-inventory diagnostic blocked the pending response past the broker's `timeout_ms + 5s` budget (issue #136) | `connection.log`'s `[#136]` lines show the inventory pass's elapsed. Fixed in #138 — the inventory is now capped to the wire-budget slice it can spare and dropped otherwise; since #149 the drop is stated on the wire as a `window-inventory-skipped` notice (`detail.reason` `ui-thread-busy` or `wire-budget-too-small`), so a pending answer is never silent about dialogs. If you see it again, the cap regressed or a *different* thing is delaying the timeout-branch response; the inventory is only one occupant of that path |

**The reusable trap under #136: `SendMessageTimeout(WM_GETTEXT, …, uTimeout)` does NOT bound a single
read to `uTimeout` against a script-blocked UI thread.** Measured live: one `WM_GETTEXT` took **1744ms**
with `uTimeout=100` and `SMTO_ABORTIFHUNG` set, because `SMTO_ABORTIFHUNG` short-circuits only once
Windows has flagged the thread *hung* (~5s of not pumping messages); before that the send just blocks.
Every window this connector reads is owned by Revit's UI thread, and the diagnostic runs precisely when
a script is holding it — so a per-call timeout and a between-call budget check are both soft (neither can
interrupt a read already in flight). Any UI-text probe on that path needs a **hard external wall-clock
cap** (a `Task.WhenAny` against a real `Task.Delay`), which is what #138 put around the whole pass.
Reading a window's *class name* (`GetClassName`) and owner (`GetWindowThreadProcessId`) does not send a
message and does not block — only the text read does.

## Symptom: a script throws "The document must not be modifiable" (or similar) calling a Document-level API

Some Revit API calls that take a `Document` argument — `Document.LoadFamily(Document)` is the one found
live, building the validation corpus's family-placement case — internally manage their own transaction
on the document they modify and refuse to run if it already has one open. (The original finding blamed
the SOURCE family document too; re-tested under Phase 3 on Revit 2025, `LoadFamily` from a modifiable
source into a non-modifiable target succeeds — under always-open the target was always modifiable, and
the error message was read as being about the source.) Since #146 Phase 3 (group-always, transaction-on-write) a document has NO
transaction open outside a `Connector.WithTransaction` block, so these calls work at top level; the
symptom now means the call sits INSIDE a block.

| Cause | Definitive check |
|---|---|
| The call is inside a `Connector.WithTransaction` block on the document it modifies (for `LoadFamily`, the TARGET project document) | Move the call between blocks: end the block, make the call, open a new block for further writes. The connector maps this to `script-target-must-not-be-modifiable` with that remedy |

Members known to behave this way (each live-verified): `Document.LoadFamily` (target non-modifiable),
`UIDocument.RequestViewChange`/`ActiveView`, `UIApplication.OpenAndActivateDocument`,
`Document.Export`, and every `EditScope` — whose second edge is the one that cost #115 the most time:
**no transaction may be open when the scope COMMITS** either. Live trace, pre-Phase 3:

```
1: scope.Start() OK, IsInEditMode=True
2: a connector transaction opened INSIDE the edit scope OK; IsModifiable=True
3: StairsRun.CreateStraightRun OK
4: scope.Commit() -> InvalidOperationException: "EditScope cannot be closed, for there is a
   transaction or transaction group still open in the document."   (Cancel() fails identically)
```

Note what the error says versus what is true: with the connector's **transaction** committed but its
**group** still open, `EditScope.Commit()` succeeds — the group is not the bar, the open transaction is.
So the stairs shape is: `scope.Start()` between blocks, `CreateStraightRun` inside a block, `scope.Commit()`
after the block. The run above returned `status: "success"` having created nothing, so this fails
**silently** — check `Document.IsInEditMode()` before believing an edit-scope result.

## Symptom: Revit crashes ("closed unexpectedly") on a Document.Close some seconds after a script ran

| Cause | Definitive check |
|---|---|
| A script's native `SubTransaction` was still ACTIVE when the enclosing connector transaction closed -- the `WithTransaction` block ending, `Connector.Settle`, or an exception unwinding through the connector's rollback -- because it was not held in a `using` | Observed once, live, Revit 2025 (#146 Phase 1 / H8 probe, a bare `var st = new SubTransaction(doc); st.Start();` left open at block end): the commit raised nothing, the slice was kept, the next `WithTransaction` block ran -- and the fixture document's `Close(false)` ~45s later took Revit down with the crash-report dialog. The connector cannot see the script's `SubTransaction` object at runtime, so `ScriptApiDenylist` now refuses any construction that is not a `using` resource (Dispose ends an active sub-transaction). The harness has NO live pin for the crash shape itself (it kills the shared session); the `using`-only path (no explicit Commit/RollBack, Dispose does the work) IS pinned live. Evidence beside `TestSubTransactionIsASavepointInsideTheConnectorsTransaction` |

## Symptom: tests are green but prove nothing

| Cause | Definitive check |
|---|---|
| The test assembly failed to load and was skipped entirely | Confirm the test **count**, never the exit code — a skipped assembly still exits 0 |
| The test DLL for that TFM doesn't exist | `dotnet test` exiting 1 with **no summary line** means missing DLL, not failures. `Test-Path` the per-TFM DLL |
| Both TFM legs ran on the same runtime | `RollForward` prefers the highest major even when the requested one is present. Assert the runtime, don't assume it |
| An opt-in test self-skips on an unset env var, so it has never once run | `return`-on-missing-config reports as **passed**, not skipped. Set the var and watch it fail before trusting it — `RealRevitApiTests` was dead from the day it was written, and **dead again later for a different reason**: the var was set for the interactive user, but `prlctl exec` runs as SYSTEM, which is how every agent session invokes `dotnet test`. Prefer probing a known path over requiring a variable; a suite whose duration jumps (0.7s → 10s) when you fix it was not running before |
| The assertion cannot fail | Revert the fix and confirm the test fails. If it still passes, it was never coverage |
| The fixture makes the test pass for the wrong reason | A ranking test passed under a mutation that removed the sort entirely, because SQLite's scan order happened to agree with the score order. Reversing two fixture declarations killed it. Mutating the mechanism is not enough — check the fixture actually *opposes* the mutation |
| The test faithfully checks a **proxy** for the property, not the property | Four instances in one PR (#92): a hand-rolled visibility predicate walking one nesting level where production used `Type.IsVisible`; a hand-rolled XML parse where production used `XmlDocIndex` (and dropped `<see cref>` text, the exact thing being forbidden); `Contains("Document")` where `"UIDocument"` was also asserted; reflecting instance members where the risk was a static one. All four passed; none was coverage. **Ask what production actually calls, and call that** — see the section below |
| A test asserts against the wrong corpus/build/version | A version probe that ignored the TFM made both multi-target legs load Revit 2027, so the `net8.0` leg reported on a corpus it never touched. Assert *which* input was chosen, not just that the assertion passed. **The replacement was wrong too, and silently**: `#if NET8_0_WINDOWS` is not a real symbol — the SDK defines `NET8_0` plus separate `WINDOWS`/`WINDOWS7_0`, never the combination — so the `#if` was never true and both legs compiled to the `#else`. A conditional-compilation mistake always fails *toward* a branch that compiles, so it cannot surface as an error. Anchor the assertion to something independent (here `Environment.Version`), never to the constant under test |

### If you wrote the guard, you are the last person who should trust it

The most expensive findings in the #92 review were not in the feature. They were in the **tests written to protect the feature**, and four of them could not fail. Every one had the same shape: the test checked something *adjacent to* the property it claimed to check.

- Claimed "`Connector` is the only publicly visible type"; checked `IsPublic || (IsNestedPublic && DeclaringType.IsPublic)`. Production checks `Type.IsVisible`, which walks the whole nesting chain. A type nested two deep was indexed by production and invisible to the test.
- Claimed "no summary leaks internal type names"; parsed the sidecar with `XElement.Value`. Production uses `XmlDocIndex`, which deliberately does *not*, because `.Value` drops self-closing elements. `<see cref="ScriptGlobals"/>` shipped the literal word to an agent while the test saw an empty string.
- Claimed "the tool description names every global"; asserted `Contains("Document")` alongside `Contains("UIDocument")`, and `Contains("Connector")` alongside `Contains("Eichler.Connectors.Revit")`. Neither could fail.
- Claimed "the public surface is exactly these seven"; reflected `BindingFlags.Public | Instance`. A `public static` member reaches `describe_function` and never appears.

**It recurs inside the fix, too.** Correcting one instance of this produced another within the hour: a
paragraph-separator guard was rewritten to compare the rendered text against the XML's own block
boundaries — exact, structural, no prose matching — and it still passed with the fix fully reverted,
because the helper returned null unless the previous node was *text*. That skipped every
`</para><para>` boundary, which is the only boundary that was ever broken. It was checking exclusively
the cases that already worked. **Run the mutation after fixing a guard, not just after writing one.**

**The check.** For any guard, name the production code path that decides the thing, and call *that* — not your own reading of it. If production has a predicate, a parser, or a visibility rule, the test must go through it; a reimplementation tests the reimplementation. Where you genuinely cannot call production (a different assembly, a load-context limit), say so in the test and pin the divergence.

**The second check, cheaper.** Write the mutation you are most afraid of, in the spelling a *maintainer* would actually use — not the spelling that is easiest to mutate. The #92 tests killed "put `PRD §` in a summary" and missed "put `<see cref="ScriptGlobals"/>` in a summary", which is the same mutation in the form someone would really write.

**Beware the general-looking pattern.** Replacing a brittle literal with a regex feels like a
strengthening and often is not: `[a-z][.!?][A-Z]` was meant to catch concatenated paragraphs and
instead flagged five of seven live summaries, because rendered prose cannot distinguish a sentence join
from `System.IO` or `Document.LoadFamily`. If the property is structural, assert it where the structure
still exists — here, against the XML rather than the rendered string.

**And prefer the shape that cannot drift.** A guard that reflects, parses, or derives from the real artifact stays true; one that restates a rule in a second language goes stale the first time the rule moves. That is the whole argument behind issue #91: five hand-maintained lists, three already wrong.

## Symptom: `search_functions` / `search_howtos` ranks keyword-only

The response's `ranker` field says `lexical` (and the guidance says "the embedding models were not
bundled"). Search still returns results, but the semantic + cross-encoder pipeline (#154) is absent,
so recall on task-style phrasings drops — this is not a ranking bug to chase in the ranker code.

| Cause | Definitive check |
|---|---|
| The **broker** serving your session was built without the embedded models (`fetch-models` not run before `go build`) | Ask the binary, not the ranking: `<broker> -search-models` prints `bundled` or `not usable`. It is broker-side, so no add-in redeploy changes it — same family as symptom #9 above |

`deploy-and-verify.sh` and the release pipeline (`release.yml`) both run `fetch-models` before the
`go build`, so a broker from either is fine. The lexical-only broker comes from a bare `go build` for
a quick local binary, or a `-LocalPackagePath` install zip built without the fetch step (`install.ps1`
warns when it detects one). The models are `go:embed`ed at build time, so the fix is a rebuild with
the fetch, not a config toggle or a restart.

## Symptom: a live run stalls with no error

Screenshot first (`dev-environment.md` → When something looks wrong). A modal can hide *behind*
another window, so if the journal shows `ADialog::doModal start` with no dismissal, believe the
journal over a clean-looking screenshot.

**But a visible dialog is not automatically the cause.** See the misdiagnosis below — matching a
symptom to a known pattern is a hypothesis, not a diagnosis.

## Techniques index

Where the recurring diagnostic techniques are actually written down. This is an index, not a second
copy — a technique is stated once, in the file that owns it, and listed here so you can find it while
stuck. **Add a bullet only when the technique is not already a rule in `SKILL.md` or a mechanism in
`dev-environment.md`; if it is, add the pointer instead.**

| Technique | Stated in |
|---|---|
| Byte-grep the deployed binary, at **both** byte alignments | `SKILL.md` → Verification |
| Mutation-test your own new test (revert the fix, confirm it fails) | `SKILL.md` → Verification |
| Prove two paths are one tree with a probe file, not by name | `SKILL.md` → Verification |
| Tee complete output, filter the file afterwards | `SKILL.md` → Verification |
| Confirm the test **count**, not the exit code | `SKILL.md` → Verification |
| Log loaded assemblies' `.Location` to catch a shadow copy | `dev-environment.md` → Deployment |
| Isolate a `TypeLoadException` by decomposing into `NoInlining` methods | `dev-environment.md` → Assembly loading |
| Screenshot the VM (`prlctl capture`) before theorising | `dev-environment.md` → When something looks wrong |
| Read the Windows **Application** event log when Revit crashed and our logs just stop | `dev-environment.md` → When something looks wrong |
| Read Revit's journal to learn what Revit was actually asked to do | `dev-environment.md` → Deployment |
| Use the connector's own MCP tools for API research | `dev-environment.md` → Scripts |

## Misdiagnoses

The most expensive category, and the one with no other record: work that ships leaves a PR, work
that's filed leaves an issue, but a wrong hypothesis leaves nothing. Each of these cost roughly a
session.

**Bounded at 8 rows** — this table is the one part of this file that cannot merge on its own, so it
needs an explicit bound or it becomes the changelog this file replaced. At the bound, adding a row
means removing one: either fold it into the row whose *distinguishing check* it shares, or promote
that check into a rule in `SKILL.md` or a row in a symptom table above and drop it. A row's value is
the check that separated the two hypotheses; once that check is stated as a rule, the row is history
and belongs in git, not here.

| Looked like | Actually was | What settled it |
|---|---|---|
| Thread-pool starvation stalling the reconnect loop | An unbounded synchronous `broker.json` read wedged by host I/O contention | Reading the code: nothing in the call was async or pool-scheduled, so the proposed mechanism could not exist |
| Broken document tracking (`documents: []`) in a suspect PR; later, a freshly deployed add-in answering `unknown-method` for a method its DLL demonstrably contained (marker check passed) | A `*.launch` signal consumed before its payload was written. Second time: `deploy-and-verify --revit-exe` without `--doc-dest` wrote a ONE-line signal, which the agent reads as a document path — so the default 2027 launched with the 2025 exe path as its document, and that instance's old add-in registered as if the deploy had worked | Revit's journal and process command line had no document path — so Revit was never asked. Second time: Revit's loaded modules (`Get-Process Revit \| % Modules`) pointed at `Addins\2027` and `C:\dev\launcher-agent.log` read `1 line(s); exe=…2027…; doc=…Revit 2025\Revit.exe`. The marker check reads the DLL on disk, not the one loaded; the script now refuses exe-only |
| A wedged Revit rejecting calls as `busy` | The harness's own 20s client deadline against a 30s server default | A clean restart reproduced it one subtest later, which ruled out accumulated state |
| A stale add-in defeating an env var fix | `prlctl exec` answering the env query as SYSTEM, not the interactive user | The add-in's own log said `Mode=Local` while the query said `remote` — two accounts, read as one |
| Types failing to load because they were new/malformed | An orphaned DLL in Revit's install directory shadowing every redeploy | Logging loaded assemblies' `.Location` |
| A trial-splash dialog wedging the idle loop and stalling the harness | Not that — the dialog does not block execution | Being told. The symptom fit a freshly-written rule, and the fit was taken as confirmation |
| `search_functions` dropping answers on long natural-language queries (issue #76) | Wrong shape entirely — those admit 455-592 candidates against a 500 cap. It is SHORT queries on common words that overflow it ("id": 4,768) | Re-running the counts through the real code path instead of the review's XML-derived corpus |
| `C:\WINDOWS\TEMP\<guid>\` looked like a scratch directory created for one document, because a script had saved a `.rvt` into it | **Revit's live per-session temp directory.** Enumerating and deleting it destroyed Autodesk IPC lock files (`adsk_adp_*_ipc_channel.lock`, `adsk_odis_sdk.lock`), `revittemp_*` scratch, and a `.adsklib` material temp, and cost the user a Revit restart. Thirteen `.tmp` files survived only because Revit still held them open | Reading the deletion output — after the fact. **Delete files you can name, never the result of an enumeration**: the first script did `GetFiles(dir)` and deleted everything without inspecting what was there. The signal was available beforehand: the directory contained files nothing in this session had created |
| A wedging modal meant `OverrideResult(Cancel)` was being rejected by dialogs with no Cancel button | Not that — Revit suppresses a TaskDialog even when sent a result it never offered (measured: Close-only and Yes/No-only both returned `Cancel(2)` without displaying) | Running it, three dialogs from a script. **What it did NOT settle**, and the trap: the window-inventory fallback fires on `first != workTask && !record.Status.IsTerminal()` — timeout plus non-terminal, nothing else. It does **not** detect whether a `DialogBoxShowing` event fired, so its presence is not evidence the dialog was never intercepted; the code comment beside it says as much. The live candidate is `DialogSuppressionHandler`'s deliberate `if (!ActiveDialogContext.IsActive) return;` — a framework dialog raised BETWEEN runs (a save reminder is timer-driven) is seen, left unanswered by design, and wedges the next call |
| Revit crashing because a sheet's title block was swapped to **another family** (issue #113) | A **type id assigned to an instance-typed property**. `ViewSheet.SheetTitleBlockId` holds the placed instance's id; `ViewSheet.Create` takes a type id. Cross-family was incidental — `ChangeTypeId` crosses families cleanly | Reading the getter: it returned the *instance* id, before and after a family swap. **The crash itself never reproduced** — the corrupt assignment was accepted, queried and reversed with Revit still up. Untried lead for anyone resuming it: force a redraw/regeneration of the sheet view, which the original session did and the follow-up did not |

**The pattern across the first six**: each was a *plausible* match to something already known. The cost
came from treating "this resembles a documented failure" as a diagnosis rather than a hypothesis, and
skipping the check that would have separated them. When a symptom matches a known pattern, that's the
moment to run the cheap distinguishing check — not the moment to stop checking.

**The seventh is the same failure one step earlier**, and worth stating separately because it is easier
to commit: the evidence itself was borrowed. A review had measured the ranking against a corpus rebuilt
from `RevitAPI.xml`, which is a fine way to review a scorer and a bad way to count what a SQL predicate
admits. Those counts were then written into a code comment and a filed issue as though they had come
from the code, and everything downstream inherited the error. **A number describing this system's
behaviour has to have been produced by running this system** — if it came from a model, a port, or
another agent's harness, say so where it is recorded, or measure it again before relying on it.

## Harness runs come back `cancelled`, then the instance is `instance-unrecoverable`

**Symptom:** parallel harness sessions against one Revit: runs return `status: cancelled` that nobody
cancelled, `ensureInstanceIdle` logs "instance busy with stale execution … resolving", and after a
few rounds the broker reports `instance-unrecoverable` ("didn't respond to cancellation within its
grace period"); every later case SKIPs until Revit is relaunched.

**Cause:** the harness's stale-execution recovery (`ensureInstanceIdle`, also inside `targetDocument`)
cancels whatever execution it finds in flight. That is right for one session's own leftover and
destructive for a peer's live run. Three forks sweeping the how-to corpus concurrently (2026-09-02)
cancelled each other until Revit's cancellation grace expired.

**Fix:** one live harness session per Revit at a time. Parallelise authoring, serialise verification.
`TestHowToSweep` no longer calls `ensureInstanceIdle` per document for this reason. Recover with
`revit/dev-tooling/deploy-and-verify.sh --skip-copy --doc-source … --doc-dest …` (a relaunch with no
`--doc-dest` opens no document, and every case then skips with "connected instance has no open
document").

