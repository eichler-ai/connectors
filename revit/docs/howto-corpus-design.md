# How-to corpus — searchable, versioned, community-fed worked examples

**Status: design (no code).** Resolves the open point left by
[`search-ranking-redesign.md`](search-ranking-redesign.md) §8.5 / §10.4: `search_functions` now
finds API *members* by meaning, but the idiom-shaped tasks it still misses ("get all walls" is a
`FilteredElementCollector` pattern, not a member) are how-to territory, and `get_skills` cannot grow
to hold them — it sits at its token budget covering basic usage, and the Revit API surface is far
too large for a skill file or even tens of them. The corpus is searched on demand, exactly like the
API, and the two are ranked by the same pipeline.

Companion files: [`howto-schema.json`](howto-schema.json) (the document schema, JSON Schema 2020-12)
and [`howto-example-join-walls.json`](howto-example-join-walls.json) (one real document, extracted by
hand from `test-harness/validation_corpus_test.go` case #2 — validated against the schema). Two
honesty notes on the example: its `verified.2027` stamp is dated to the test file's last commit
rather than to a recorded harness run, because no run timestamp exists to cite yet (§3 is what would
create one); and its `script` replaces the test's fixture preamble with `var doc = Document;`, so the
hash is of the how-to's script, not the test's literal.

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
| `script` | the working C# body, in the connector's script dialect (ambient transaction, `Connector` global) |
| `pitfalls` | one entry per mistake avoided, each with the symptom the agent would otherwise see |
| `verified` | per Revit version: when, how, against which script hash — see §3 |
| `provenance` | where it came from and how it was reviewed |

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

**[decided]** A how-to's `verified` map has one entry per Revit version, and only the harness writes
it. `TestHowToCorpusScriptsStillRun` **[proposed]** runs every document's `script` against whichever
Revit is connected (the same `--revit-version` sweep the rest of tier 2 uses), and on success stamps
`verified[<version>] = {at, script_sha256, by: "harness"}`. A script that fails is not deleted: the
entry is marked `failed` with the diagnostic, and the document keeps serving for the versions where it
still passes, with the failure visible to the agent. That is the mechanism for "constantly updated
with fixes": drift (the `BuiltInParameterGroup` → `GroupTypeId` class of change) is detected by the
same thing that detects it in the connector's own code.

At query time the connected instance's `revit_version` is a **pre-ranking preference**, not a filter:
documents verified on that version rank first; others are still returned, labelled with the versions
they were verified on, because a 2027-verified how-to is usually still the right starting point on
2025.

`api_since` / `api_until` are optional declared hints for members that appear or disappear across
versions; they are not verification and are shown as such.

## 4. Search: the same pipeline, a second corpus

**[decided]** The how-to corpus is a second `semsearch.Index`, built at broker start and rebuilt on
corpus change. Fields for BM25F and the static embedder: `title`, `task` (highest weight), `pitfalls`
text, and `members` (which gives an exact-name hit when the agent already suspects a member). The
cross-encoder reranks the fused top 20 exactly as for the API. Cost is negligible: a corpus of a few
thousand documents embeds in well under a second with the static model, so rebuilding on every update
is fine.

Two tools **[proposed]**, mirroring the API pair:

- `search_howto(query, revit_version?, cursor?, top_n?)` → short hits: `id`, `title`, `task`,
  `members`, `verified_on[]`, `score`, `source` (`shared` / `local`), plus the same `ranker` and
  `guidance` fields `search_functions` carries.
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

**Distribution [decided]:** the shared corpus is **not embedded** in the broker — it changes far more
often than the broker does. The broker already polls GitHub releases every 6 hours
(`internal/updatecheck`); the same loop fetches `howto-corpus.jsonl` when its release tag or ETag
changes, validates every line against the schema (a document that fails validation is skipped and
logged, never fatal), caches it under `<app-data>/howto/shared/`, and reindexes. Offline, the cached
copy serves; with no cache, the embedded seed serves. The corpus carries `corpus_version`
(monotonic) and `schema_version`; a broker older than the schema serves the documents it can parse
and says so in `guidance`.

**Local corpus:** anything under `<app-data>/howto/local/` is indexed alongside, marked
`source: "local"` on every hit so the agent knows it is unreviewed, and re-scanned when a file changes
(mtime check on search is enough; no watcher). An `id` collision between local and shared resolves to
local, with a `supersedes_shared: true` flag on the hit — that is the intended way for a user to
override a shared how-to for their own environment. Local documents are never uploaded except through
the explicit submit flow below.

**Bounds** (CONVENTIONS.md): a document's `script` ≤ 16 KB; a corpus ≤ 20,000 documents / 64 MB;
the local directory ≤ 2,000 files. Beyond any bound the broker logs and stops loading rather than
degrading silently.

## 6. Submission: `submit_howto` → review queue → shared corpus

The corpus grows from the agents using it. The flow has to be safe for a public repo and for a user's
private model data, and cheap for the maintainer.

**`submit_howto` [proposed]** — an MCP tool the agent calls after a task succeeded:

```
submit_howto(title, task, script, members[], pitfalls[]?, queries?, notes?, confirm_submission: bool)
```

1. The broker assembles a schema-valid document with `provenance.kind = "submission"`, `verified`
   set for the connected instance's version only if the exact `script` just ran successfully in this
   session (the broker has the execution record; that is `by: "session"`, weaker than `harness` and
   shown as such), and writes it to the **local corpus immediately**. The submitter benefits at once
   whether or not the shared review ever happens.
2. **Outward-facing, so confirmation-gated** — the same shape as `confirm_lifecycle_actions`: without
   `confirm_submission: true` the tool stops after step 1 and returns the document plus a
   `howto-submission-confirmation-required` record. Posting to a public issue tracker is a
   side-effect the transaction model does not cover.
3. **Scrub before sending.** The broker rewrites absolute paths to placeholders, drops anything that
   looks like a document title, project path, user name or machine name, and returns the scrubbed
   document in the response so the user can see exactly what will leave the machine. Scripts that
   still contain a UNC or drive path after scrubbing are refused with a `remedy` naming the line.
4. **Open the review-queue issue.** One GitHub issue in this repo, label `howto-submission`, title
   from `title`, body = the scrubbed document in a fenced ` ```json ` block plus the target version.
   **Credentials [decided]:** the connector never holds a repo token. If the `gh` CLI is present and
   authenticated on the machine, the broker uses it (`gh issue create`); otherwise it returns a
   prefilled `https://github.com/eichler-ai/connectors/issues/new?...` URL for the person to open
   themselves. Either way the response carries the issue URL or the prefilled URL as `queue_ref`.
5. **Review** happens on the issue, by a maintainer or a scheduled review agent: schema check, a
   harness run of the script against the tagged version (which is what earns `verified.by: "harness"`),
   de-duplication against existing documents (same `members` set + similar `task` embedding), and an
   edit pass on `task`/`pitfalls` wording. Accepting is a PR that appends the document to
   `revit/howto/corpus.jsonl` **[decided: append-only in git; edits are new documents with
   `supersedes`]**, so the file's history is the audit trail, and the next release publishes it.

The scheduled review agent is the natural place for `/schedule`: a routine that triages
`howto-submission` issues, runs the harness case, and opens the append PR for a human to merge.

## 7. What is not in scope

- Ranking quality for how-tos is not measured yet; the API ranking corpus (`ranking-corpus.tsv`)
  pattern applies once there are enough documents, and the recorded misses are its seed.
- No authoring UI, no Markdown front-matter format, no per-document licensing beyond the repo's.
- Cross-promotion into `search_functions` (§4, open).

## 8. Implementation order [proposed]

1. Schema + validator package (Go), and the harness extractor producing the seed
   `revit/howto/corpus.jsonl` from the annotated tests (≈ a dozen documents). Tier-1 tests on the
   validator and extractor.
2. Second index + `search_howto` / `describe_howto`, with the version preference; `skill.md` gains
   one bullet. Live: the pitfall queries recorded in the harness must return their document at rank 1.
3. Local corpus directory + `TestHowToCorpusScriptsStillRun` (the verification stamp).
4. Shared corpus as a release asset + the update loop.
5. `submit_howto` with the gate, the scrubber, and the `gh`/prefilled-URL queue.
