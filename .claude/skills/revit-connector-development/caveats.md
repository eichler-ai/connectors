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
| 8 | You deployed from the wrong checkout | `redeploy-and-verify.sh --share-name`; its identity guard refuses rather than reporting PASS |

**Read the log's own unconditional first line before reasoning about anything after it.**
`connection.log` opens with `RunConnectionLoop starting. Mode=... ConnectorRoot=...`.

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

## Symptom: tests are green but prove nothing

| Cause | Definitive check |
|---|---|
| The test assembly failed to load and was skipped entirely | Confirm the test **count**, never the exit code — a skipped assembly still exits 0 |
| The test DLL for that TFM doesn't exist | `dotnet test` exiting 1 with **no summary line** means missing DLL, not failures. `Test-Path` the per-TFM DLL |
| Both TFM legs ran on the same runtime | `RollForward` prefers the highest major even when the requested one is present. Assert the runtime, don't assume it |
| The assertion cannot fail | Revert the fix and confirm the test fails. If it still passes, it was never coverage |

## Symptom: a live run stalls with no error

Screenshot first — `prlctl capture "<vm>" --file screen.png`, then read the PNG. You cannot drive the
VM's UI, but you can see it, and treating "can't drive" as "must work blind" has cost hours. A modal
can hide *behind* another window, so if the journal shows `ADialog::doModal start` with no dismissal,
believe the journal over a clean-looking screenshot.

**But a visible dialog is not automatically the cause.** See the misdiagnosis below — matching a
symptom to a known pattern is a hypothesis, not a diagnosis.

## Techniques worth reaching for

- **Byte-grep a deployed binary at both alignments.** The `#US` heap stores UTF-16 literals at
  arbitrary offsets, so a single-alignment decode gives false negatives. Confirmed: of two literals
  added in one edit, only one was found by a single-alignment scan.
- **Mutation-test your own new test.** Revert the fix, confirm the test fails, restore. Commit first
  so the restore can't lose work. This is the only evidence that a test covers what you think.
- **Prove identity, don't compare names.** To check two paths are the same tree, drop a
  uniquely-named probe file on one side and look for it from the other. Names can legitimately differ.
- **Isolate a `TypeLoadException` by decomposition.** The JIT resolves every type a method references
  before executing any of it, so the failure surfaces at the *caller*. Split into small
  `[MethodImpl(MethodImplOptions.NoInlining)]` methods, each wrapped individually at its call site.
- **Log loaded assemblies' `.Location` early.** Instantly reveals a shadow copy that no amount of
  redeploying will fix.
- **Tee complete output, filter the file afterwards.** A filter's empty output must never be the only
  record of a run.
- **Use the connector's own MCP tools for API research.** One `search_functions`/`execute_script`
  call beats a build/deploy/launch cycle when the open question is a script's correctness rather than
  the connector's behaviour.

## Misdiagnoses

The most expensive category, and the one with no other record: work that ships leaves a PR, work
that's filed leaves an issue, but a wrong hypothesis leaves nothing. Each of these cost roughly a
session.

| Looked like | Actually was | What settled it |
|---|---|---|
| Thread-pool starvation stalling the reconnect loop | An unbounded synchronous `broker.json` read wedged by host I/O contention | Reading the code: nothing in the call was async or pool-scheduled, so the proposed mechanism could not exist |
| Broken document tracking (`documents: []`) in a suspect PR | A `*.launch` signal consumed before its payload was written | Revit's journal and process command line had no document path — so Revit was never asked |
| A wedged Revit rejecting calls as `busy` | The harness's own 20s client deadline against a 30s server default | A clean restart reproduced it one subtest later, which ruled out accumulated state |
| A stale add-in defeating an env var fix | `prlctl exec` answering the env query as SYSTEM, not the interactive user | The add-in's own log said `Mode=Local` while the query said `remote` — two accounts, read as one |
| Types failing to load because they were new/malformed | An orphaned DLL in Revit's install directory shadowing every redeploy | Logging loaded assemblies' `.Location` |
| A trial-splash dialog wedging the idle loop and stalling the harness | Not that — the dialog does not block execution | Being told. The symptom fit a freshly-written rule, and the fit was taken as confirmation |

**The pattern across all six**: each was a *plausible* match to something already known. The cost came
from treating "this resembles a documented failure" as a diagnosis rather than a hypothesis, and
skipping the check that would have separated them. When a symptom matches a known pattern, that's the
moment to run the cheap distinguishing check — not the moment to stop checking.
