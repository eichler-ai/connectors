# How-to corpus — searchable, versioned, community-fed worked examples

**Status: design (no code).** Resolves the open point left by
[`search-ranking-redesign.md`](search-ranking-redesign.md) §8.5 / §10.4: `search_functions` now
finds API *members* by meaning, but the idiom-shaped tasks it still misses ("get all walls" is a
`FilteredElementCollector` pattern, not a member) are how-to territory, and `get_skills` cannot grow
to hold them — it sits at its token budget covering basic usage, and the Revit API surface is far
too large for a skill file or even tens of them. The corpus is searched on demand, exactly like the
API, and the two are ranked by the same pipeline.

Companion files: [`howto-schema.json`](howto-schema.json) (the document schema, JSON Schema 2020-12),
[`howto-verification-schema.json`](howto-verification-schema.json) (the harness-owned verification
sidecar, §3) and [`howto-example-join-walls.json`](howto-example-join-walls.json) (one real document,
extracted by hand from `test-harness/validation_corpus_test.go` case #2 and validated against the
schema). The example carries **no verification**: its `script` replaces the test's fixture preamble
with `var doc = Document;`, so no harness run has executed that exact text, and a stamp for it would be
declared rather than earned — which §3 forbids. The first sweep (§3) will write the stamp.

This note was revised after an independent review (PR #159); the review's design questions are
carried in §9 rather than answered by assertion.

Throughout: **[decided]** is settled by this note, **[open]** needs a decision before implementation.

---

## 1. What a how-to is

One task an agent might be asked to do, with the shortest path to doing it correctly on a specific
Revit version, and the mistakes that path avoids. Not API reference (that is `describe_function`),
not orientation (that is `get_skills`). The unit of value is the **pitfall**: the harness comments are
full of them ("`AreElementsJoined` and `IsWallJoinAllowedAtEnd` measure different things";
"`LoadFamily` needs *both* documents to have no open transaction, so load first, then open for
writing") and nothing today serves them to an agent at the moment it needs them.

A document carries:

| Field group | Purpose |
|---|---|
| `id`, `title`, `task` | identity and the task in agent language — `task` is the primary embedding text |
| `queries` | phrasings that found it and phrasings that *missed*; the misses are the corpus's most valuable rows, they are where API search fails and the how-to earns its keep |
| `members` | fully-qualified members used, so an API hit and a how-to hit can be cross-linked |
| `script` | the working C# body, in the connector's script dialect **as of the version it was verified on** — see the dialect note below |
| `pitfalls` | one entry per mistake avoided, each with the symptom the agent would otherwise see |
| `provenance` | where it came from and how it was reviewed |

Verification is deliberately **not** a field of the document (§3): it lives in a sidecar keyed by the
document's id and script hash, so a submitter cannot write it and an append-only corpus file never
needs editing.

**The script dialect is itself versioned by the connector, not only by Revit.** #146 Phase 3 (merged
as #160) replaced the ambient-transaction model with group-always / transaction-on-write: a top-level
`Level.Create(Document, …)` is now refused with `script-write-outside-transaction`. The shipped
dialect (`skill.md` section "Writing: one block per batch, nothing open in between"): reads at top
level; writes inside
`Connector.WithTransaction(doc, () => { … })`, which returns the body's value, one block per batch;
self-transacting calls (`LoadFamily`, `RequestViewChange`, EditScope start/commit, `Export`) go
*between* blocks; `Connector.Settle` unchanged; `OpenForWriting` and `WithoutTransaction` gone. The
validation-corpus case #3 pitfalls about `LoadFamily` and open transactions are therefore about to
change shape too — a second concrete instance of D6. Every script in the seed — including the example beside this note — is written in the pre-#146
dialect and fails the sweep against a #160 broker (the example's source test predates #160). That is the mechanism working as intended (§3):
the stamps go `failed` with the diagnostic naming the fix, the seed is re-extracted from the harness
tests once they are updated, and the sidecar's `connector_version` records which broker verified
what. It also means the sweep must key stamps by connector version as well as Revit version, and
`search_howto` should prefer documents verified on the *running* connector's version — added to §9
as D6.

The full schema is `howto-schema.json`; the example document shows every field populated from a
real case.

**Format [decided]:** the canonical interchange and release format is **JSONL** (one document per
line, one `howto-corpus.jsonl` per release). Humans do not hand-edit JSONL: authoring happens through
the `submit_howto` tool (§5) or through the harness-extraction step (§2), both of which emit
schema-valid documents. A Markdown authoring format was considered and rejected for v1: it adds a
converter and a second place for the schema to drift, for a corpus whose documents are mostly
machine-produced.

## 2. Seed: extract from the harness

Every harness test that runs a script against live Revit is a how-to with its verification already
done: the test name and doc comment are the task and pitfalls, the script literal is the script, the
`--revit-version` it ran under is the version tag, and a passing run is the verification. The seed
corpus is produced by a `go run ./cmd/howto-extract` **[proposed]** over `revit/test-harness/*_test.go`
that emits one document per annotated test — annotation being a `// howto:` marker comment so
extraction is opt-in per test and the test author writes the `task` line deliberately rather than
having a Go identifier turned into prose.

What the harness already records, and the extractor keeps:

- `validation_corpus_test.go` case #2: the query `join walls at corner` **missed** (top results were
  enum noise the ranker now masks), `AreElementsJoined geometry` hit at rank 1, and the finding that
  the found member measures the wrong thing — three fields of the example document.
- `validation_corpus_test.go` case #3: two transaction-order pitfalls for `LoadFamily`.
- `phase_a_test.go`: a **negative** how-to — "there is no `Application.CreateSharedParameterFile`;
  write the file with `System.IO`". Negative results are a first-class kind (`kind: "negative"`),
  because the semantic ranker always returns *something* and the agent needs a document that says
  "stop looking".
- `phase_c_test.go`: `create sheet place view` never surfaced `ViewSheet.Create` (the miss that
  became `ranking-corpus.tsv`'s first asserted row).

Extraction is a build step, not runtime: the JSONL it produces is committed under
`revit/howto/corpus.jsonl` and reviewed like code.

## 3. Version tags are earned by running, not declared

**[decided]** Verification records live in a **sidecar**, `howto-verified.jsonl`, one line per
(document `id`, `script_sha256`, Revit version) — schema in `howto-verification-schema.json`. Only the
harness writes it (or, weaker and local-only, the submitting session — §6). The corpus file itself is
never edited to record a run, which is what lets §6 keep it append-only; the broker joins the two at
index time, and a stamp whose hash no longer matches the document's script is stale and ignored.

`TestHowToCorpusScriptsStillRun` **[proposed]** runs every document's `script` against the connected
Revit — the version comes from the instance record the harness already reads (`revit_version` on
`list_instances`), not from a flag — and appends a `passed` or `failed` (with the §01 diagnostic) line
for that version. A failing script is not deleted: the document keeps serving for the versions where
it passes, with the failure visible to the agent. That is the mechanism for "constantly updated with
fixes": drift (the `BuiltInParameterGroup` → `GroupTypeId` class of change) is detected by the same
thing that detects it in the connector's own code.

**Fixture rule [decided]:** the sweep never runs a script against the operator's document. It creates
a throwaway fixture document (the harness's `createBlankFixtureDocument`), routes the script at it by
`document_id`, and closes it afterwards. The script text is executed unchanged — `Document` resolves
to the routed fixture — so the hash the stamp binds to is the hash the agent will read. A how-to
whose script cannot run against a blank fixture (needs a family, a link, a workshared central) says so
in `task` and is skipped by the sweep with a `not-sweepable` note rather than stamped `failed`.

A passing run proves the script executes without error and returns a non-empty value. It does not
prove the result is *right*; that is what review (§6) is for, and why a `fix` sentence can go stale
while its script still passes (§9).

At query time the connected instance's `revit_version` is a **pre-ranking preference**, not a filter:
documents verified on that version rank first; others are still returned, labelled with the versions
they were verified on, because a 2027-verified how-to is usually still the right starting point on
2025.

`api_since` / `api_until` are optional declared hints for members that appear or disappear across
versions; they are not verification and are shown as such.

## 4. Search: the same pipeline, a second corpus

**[decided]** The how-to corpus is a second index built by the same pipeline. Today's
`semsearch.Index` is typed to the API `Doc` with three fixed fields and a `namespace` mask, so this
means **generalising** it (a field-set per corpus, and a version *preference* — a rank boost, which
the API index does not have today) rather than instantiating it twice. Fields for BM25F and the static
embedder: `title`, `task` (highest weight), `pitfalls` text, and `members` (an exact-name hit when the
agent already suspects a member). The cross-encoder reranks the fused top 20 exactly as for the API.
Cost is negligible: a few thousand documents is two orders of magnitude below the 68k-member API
corpus that embeds in ~1.1 s (§10.3 of the ranking note), so rebuilding on every update is fine.

Two tools **[proposed]**, mirroring the API pair:

- `search_howto(query, revit_version?, cursor?, top_n?)` → short hits: `id`, `title`, `task`,
  `members`, `verified_on[]` (from the sidecar), `score`, `source` (`seed` / `shared` / `local`), plus
  the same `ranker`, `guidance` and `notices[]` fields `search_functions` carries.
- `describe_howto(id)` → the full document.

**Cross-promotion [open]:** `search_functions` could surface the top how-to hit when its rerank
score beats the API hits — the idiom queries are exactly where this helps. Deferred until the corpus
has enough documents for the reranker's cross-corpus scores to be trusted; until then the agent is
told (in `skill.md` and in `search_functions`' guidance) to try `search_howto` when a task-shaped
query returns only members.

## 5. Three sources, one index

| Source | Lives | Updated by | Trust |
|---|---|---|---|
| **shared** | `howto-corpus.jsonl`, a release asset of this repo | the review queue (§6), published by the release pipeline | reviewed, harness-verified |
| **local** | `<app-data>/howto/local/*.json`, one document per file | `submit_howto` writes here first; a person can also drop files | unreviewed, the user's own |
| **seed** | `revit/howto/corpus.jsonl` in the repo, embedded in the broker | harness extraction (§2) | reviewed, harness-verified; the offline floor |

**Distribution [decided, with one open question]:** the shared corpus is **not embedded** in the
broker. The intent is that it changes more often than the broker; **whether it does is §9 D1** — if
the corpus only ever ships inside a broker release, the fetch machinery below buys nothing and the
seed alone suffices. Assuming it does: the broker's existing 6-hourly release poll
(`internal/updatecheck`) is extended — today it reads only `tag_name` through a 1 MB limit and has no
asset or ETag handling — to also fetch the corpus asset with its digest, and a corpus-only release
must **not** flip the ribbon's "update available" (PRD §12), so corpus releases are tagged distinctly
(`howto-vN`) and the broker-update check ignores them. Every line is validated against the schema; a
line that fails is skipped and counted, never fatal, and the count reaches the agent as a
`notices[]` record. The cache is `<app-data>/howto/shared/`; offline, the cache serves; with no
cache, the embedded seed serves.

**The corpus is code the agent will run, so it is verified like code [decided]:** the release pipeline
already publishes `checksums.txt` and Authenticode-signs the binaries; the corpus asset's sha256 is
listed there and the broker refuses a corpus whose digest does not match. A signature on the corpus
itself is the stronger follow-up once a real code-signing certificate replaces the self-signed one.

**Forward compatibility [decided]:** documents allow unknown top-level fields, so an older broker reads
a newer corpus, validates the fields it knows, and reports `howto-corpus-newer-than-broker` in
`guidance` and `notices[]` rather than skipping the whole corpus.

**Local corpus:** PRD §09 keeps human-browsed files under the exchange root, never app-data, so the
local corpus lives at `<exchange-root>/howto/` (beside `imports/` and `exports/`), one document per
`.json` file. It is indexed alongside, marked `source: "local"` on every hit so the agent knows it is
unreviewed, and re-scanned when a file changes (mtime check on search; no watcher). An `id` collision
between local and seed/shared resolves to local with `supersedes_shared: true` on the hit — the
intended way to override a shared how-to for one environment; two local files with one `id` is a
validation error naming both files. Local documents are never uploaded except through the explicit
submit flow below.

**Local documents are an injection surface.** A how-to is text the agent reads and a script it may
run, and anyone who can write to the exchange root can plant one. `source: "local"` on the hit is
the minimum; `skill.md` tells the agent that local how-tos carry no review and their scripts should be
read before being run. Whether that is enough is §9 D4.

**Bounds** (CONVENTIONS.md): a document's `script` ≤ 16 KB; a corpus ≤ 20,000 documents / 64 MB;
the local directory ≤ 2,000 files. Beyond any bound the broker stops loading, logs, and reports the
truncation as a `notices[]` record on every `search_howto` response (the precedent is
`search_functions`' `search-index-building` notice), never silently.

## 6. Submission: `submit_howto` → review queue → shared corpus

The corpus grows from the agents using it. The flow has to be safe for a public repo and for a user's
private model data, and cheap for the maintainer.

**`submit_howto` [proposed]** — an MCP tool the agent calls after a task succeeded:

```
submit_howto(title, task, script, members[], pitfalls[]?, queries?, notes?, confirm_submission: bool)
```

1. The broker assembles a schema-valid document with `provenance.kind = "local"` and writes it to
   the **local corpus immediately**; the submitter benefits at once whether or not the shared review
   ever happens. If the exact `script` text was executed successfully in this session, the broker
   appends a `by: "session"` line to the local verification sidecar. Today's execution record keeps
   status and result but not the script text or its hash, and is evicted after 200 runs or 10
   minutes, so this needs the broker to retain the sha256 of each executed script for the record's
   lifetime **[proposed]**; a `session` stamp is weaker than `harness`, shown as such, and never
   promoted to the shared corpus.
2. **Outward-facing, so confirmation-gated.** Without `confirm_submission: true` the tool stops after
   step 1 and returns the document plus a `howto-submission-confirmation-required` record. This
   differs from `confirm_lifecycle_actions`, which refuses *before* any side effect: here the local
   write is the intended non-gated half, and only the outward half is gated.
3. **Scrub, then show.** The broker rewrites absolute and UNC paths to placeholders and drops anything
   that looks like a document title, project path, user name, machine name or bind address, across
   **every** text field — `task`, `summary`, `pitfalls[].symptom` (which §01 diagnostics deliberately
   fill with concrete identifiers), `queries[].text` and the script's string literals — and returns
   the scrubbed document so the user sees exactly what will leave the machine. Anything that still
   matches a path or host pattern after scrubbing is refused with a `remedy` naming the field and
   line. The scrubber is a filter, not a guarantee; the confirmation step is where the user reads it.
4. **Open the review-queue issue.** One GitHub issue in this repo, label `howto-submission`, title
   from `title`, body = the scrubbed document in a fenced ` ```json ` block plus the target version.
   **Credentials [decided]:** the connector never holds a repo token and never spawns `gh` — a
   PATH-resolved subprocess is a new capability class for the broker, and posting through the user's
   own login would attach the identity the scrubber just removed. The tool returns a prefilled
   `issues/new` URL as `queue_ref` for the person to open; because URLs cap at roughly 8 KB and a
   script may be 16 KB, the prefill carries title, task, members and pitfalls, and the body asks the
   person to paste the JSON the tool also wrote to `<exchange-root>/howto/outbox/<id>.json`.
   Attribution is opt-in (`provenance.submitter`), never inferred.
5. **Review** happens on the issue and is **human-gated before any execution [decided]**: a
   maintainer reads the submission and applies `howto-reviewed`; only then does the review agent
   (a `/schedule` routine is the natural home) run the script — against a disposable fixture
   document on a review VM holding no real model data — which is what earns a `harness` stamp;
   then de-duplicates (same `members` set plus similar `task` embedding), edits `task`/`pitfalls`
   wording, and opens the append PR for a human to merge. Running an arbitrary public submission
   under the `Connector` global without that label would be remote code execution from a public
   queue. Accepting appends the document to `revit/howto/corpus.jsonl` **[decided: append-only in
   git; edits are new documents with `supersedes`]**, so the file's history is the audit trail;
   uniqueness of `id` across the file is a CI check on the PR.

## 7. Relationship to the ranking note

[`search-ranking-redesign.md`](search-ranking-redesign.md) §4 sketched the how-to index as "embedded
once, shipped" with the two result streams merged into one `search_functions` response. This note
supersedes both points: the shared corpus is fetched rather than embedded (§5, with §9 D1 open), and
the two corpora get separate tools with cross-promotion deferred (§4). The ranking note's §10.4
entry for 8.5 points here.

## 8. What is not in scope

- Ranking quality for how-tos is not measured yet; the API ranking corpus (`ranking-corpus.tsv`)
  pattern applies once there are enough documents, and the recorded misses are its seed.
- No authoring UI, no Markdown front-matter format, no per-document licensing beyond the repo's.
- Cross-promotion into `search_functions` (§4, open).

## 9. Open decisions (from review)

- **D1 — corpus cadence.** Does the shared corpus really ship more often than the broker? If not,
  drop §5's fetch machinery and ship the corpus embedded, in the broker release. Decide from the
  submission rate after the seed lands; the design works either way.
- **D2 — trust model for stamps.** The sidecar makes it structural (only the harness writes it), but
  who runs the harness that writes the *shared* sidecar, and how does the broker know a sidecar line
  came from it? Today: it ships in the same digest-checked release asset. A signed sidecar is the
  stronger form.
- **D3 — public-queue hygiene.** Spam and volume on `howto-submission` issues; rate limits; the
  human label as the only gate before execution; attribution opt-in.
- **D4 — local corpus as a prompt-injection surface.** Is `source: "local"` plus a `skill.md` warning
  enough, or should local scripts be shown but never auto-run?
- **D5 — prose drift.** A `fix` or `symptom` can go stale while the script still passes the sweep;
  nothing detects that except re-review. Consider a `reviewed_at` age shown on the hit.
- **D6 — connector-version dialect.** The sidecar already records `connector_version`; should a
  stamp be keyed by it (so a script verified under the pre-#146 dialect is not offered as verified
  on a post-#146 broker), and should the sweep run on every connector release, not only on Revit
  version changes? #146's transaction-model change is the first concrete case.

## 10. Implementation order [proposed]

1. Schemas + validator package (Go; document and sidecar), and the harness extractor producing the
   seed `revit/howto/corpus.jsonl` from the annotated tests (≈ a dozen documents), with a check that a
   seed document's script hash matches its source test. Tier-1 tests on the validator and extractor.
2. Generalise `semsearch.Index` (field set per corpus, version preference) + `search_howto` /
   `describe_howto`; `skill.md` gains one bullet. Live: the pitfall queries recorded in the harness
   must return their document at rank 1.
3. `TestHowToCorpusScriptsStillRun` with the fixture rule, writing the sidecar; local corpus directory.
4. Shared corpus as a digest-checked, distinctly-tagged release asset + the extended update loop —
   only if D1 says the cadence warrants it.
5. `submit_howto` with the gate, the scrubber, the outbox file and the prefilled-URL queue; the
   `howto-reviewed` label gate and the review routine.
