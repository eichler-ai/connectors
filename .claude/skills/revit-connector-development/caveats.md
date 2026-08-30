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
| An opt-in test self-skips on an unset env var, so it has never once run | `return`-on-missing-config reports as **passed**, not skipped. Set the var and watch it fail before trusting it — `RealRevitApiTests` was dead from the day it was written |
| The assertion cannot fail | Revert the fix and confirm the test fails. If it still passes, it was never coverage |
| The fixture makes the test pass for the wrong reason | A ranking test passed under a mutation that removed the sort entirely, because SQLite's scan order happened to agree with the score order. Reversing two fixture declarations killed it. Mutating the mechanism is not enough — check the fixture actually *opposes* the mutation |

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
| Broken document tracking (`documents: []`) in a suspect PR | A `*.launch` signal consumed before its payload was written | Revit's journal and process command line had no document path — so Revit was never asked |
| A wedged Revit rejecting calls as `busy` | The harness's own 20s client deadline against a 30s server default | A clean restart reproduced it one subtest later, which ruled out accumulated state |
| A stale add-in defeating an env var fix | `prlctl exec` answering the env query as SYSTEM, not the interactive user | The add-in's own log said `Mode=Local` while the query said `remote` — two accounts, read as one |
| Types failing to load because they were new/malformed | An orphaned DLL in Revit's install directory shadowing every redeploy | Logging loaded assemblies' `.Location` |
| A trial-splash dialog wedging the idle loop and stalling the harness | Not that — the dialog does not block execution | Being told. The symptom fit a freshly-written rule, and the fit was taken as confirmation |
| `search_functions` dropping answers on long natural-language queries (issue #76) | Wrong shape entirely — those admit 455-592 candidates against a 500 cap. It is SHORT queries on common words that overflow it ("id": 4,768) | Re-running the counts through the real code path instead of the review's XML-derived corpus |

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
