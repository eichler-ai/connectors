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
| 9 | The change was **broker-side** and only the add-in was redeployed | Ask the running broker, not the repo: `get_skills`' `build` field, or `<broker> -version`, names the revision compiled in. `skill.md`, every tool schema and every tool description live in the mcp-server binary; no add-in redeploy can move them, and `redeploy-and-verify.ps1` never builds Go at all (issue #116) |

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

## Symptom: a script throws "The document must not be modifiable" (or similar) calling a Document-level API

Some Revit API calls that take a `Document` argument — `Document.LoadFamily(Document)` is the one found
live, building the validation corpus's family-placement case — internally manage their own transaction
on **both** the document the call is on and the document passed as an argument, and refuse to run if
either already has one open. This connector's ambient-transaction model (`OpenForWriting`,
`CreateProjectDocument`/`CreateFamilyDocument`) keeps a document's transaction open for the rest of the
`execute_script` call that touched it, which collides with exactly this.

| Cause | Definitive check |
|---|---|
| The call's own document was created earlier in the SAME script call, so its managed transaction is still open | Split into two calls: create/edit in one, let it return (commits the transaction), call the API from a separate call that finds the document fresh |
| `OpenForWriting` was called on the TARGET document before the API call, not after | Reorder: make the call first, `OpenForWriting` afterward, if the call's own result is what you need to keep editing |

Not specific to `LoadFamily` — treat this as the general shape for any Document-argument API that
throws a transaction/modifiability error, and check both documents' transaction state before assuming
the API itself is broken.

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
