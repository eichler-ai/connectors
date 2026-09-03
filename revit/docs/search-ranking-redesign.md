# search_functions ranking redesign — semantic hybrid retrieval

**Status: implemented (issue #107); §10 records what shipped and what the implementation measured.**
§§1–7 are the design note as written before implementation, kept because the POC evidence and the
rejected alternatives are what a later reader needs; §8's open decisions are each resolved in §10.
Design rationale for the tool: [`PRD.md`](PRD.md) §08 (API discovery), which carries a revision note
pointing here.

Throughout, findings are tagged **[measured]** (a run made in the POC, numbers reproducible from the
scratch harness), **[proposed]** (a design choice not yet built), or **[open]** (an unresolved
decision). The distinction is load-bearing: the POC ran in Python against a snapshot corpus, so its
numbers are directional evidence for a Go implementation, not a measurement of one.

---

## 1. Why change

`search_functions` is the agent's entry point when it knows *what* it wants but not the member name.
Two structural limits make it the weakest of the three discovery tools:

1. **Keyword recall gap.** The ranker matches query tokens against member names and XML-doc summary
   text. A natural-language task and the member that answers it frequently share no tokens — "get all
   walls" is answered by `FilteredElementCollector.OfClass`, which contains neither word. Recall, not
   precision, is the failure mode: the agent concludes "the API isn't there" when the member exists
   under different wording.
2. **It lives on the wrong side, and it is version-specific.** All ranking is in the C# add-in
   (`DiscoveryCache.Search` + `IdentifierRelevance`), operating on a per-Revit-version SQLite index
   built by reflecting that instance's loaded assemblies. This makes the ranker hard to iterate on
   (redeploy per version), and it cannot ever include how-to documentation — the index is API-only by
   construction.

The goal is a ranker that (a) retrieves by meaning, not just keyword overlap, (b) lives broker-side so
it is version-independent and iterable, and (c) can merge API results with how-to-doc results in one
response.

---

## 2. Current implementation (baseline being replaced)

Traced end-to-end so the redesign's "what moves where" is concrete.

**Broker (Go), routing only** — `mcp-server/internal/`
- `mcpserver/discovery_tools.go` — the `search_functions` tool: input schema (`query`, `namespace`,
  `cursor`, `top_n`), output schema, and the `guidance` note (§7). Does **no** ranking.
- `discovery/discovery.go:257` `(*Router).SearchFunctions` — picks the instance, forwards params over
  the wire, unmarshals the reply.

**Add-in (C#), all ranking** — `mcp-bridge/src/MCPBridge.Core/`
- `Dispatch/RequestDispatcher.cs:1085` `HandleSearchFunctions` — clamps `top_n` (default 20).
- `Discovery/DiscoveryService.cs:141` `SearchFunctions` — orders by score, tie-breaks, paginates.
- `Discovery/DiscoveryCache.cs:818` `Search` — **the ranker**: three disjoint score bands —
  - Tier 1 exact `Type.Member` → `1000 + CoreBoost`
  - Tier 2 token match graded by `IdentifierRelevance` → `500 + relevance×249`
  - Tier 3 FTS5 BM25 fallback → normalized `<500`
- `Discovery/IdentifierRelevance.cs` — tier-2 lexical scorer: camelCase/underscore `SplitWords`,
  a synonym dictionary, stop-words, leading-word synonym credit.
- FTS5 virtual table `members_fts` (`unicode61` tokenizer), populated on reflection `Sync`.
- Corpus source: `DiscoveryReflector` reflects `RevitAPI.dll` + XML docs into a per-version
  `cache-<version>.db`.

**The one fact that shapes the redesign:** ranking is 100% add-in-side and per-version. There is no
Go-side ranking seam today.

---

## 3. POC results (evidence)

A throwaway Python POC (sentence-transformers + a local encoder) was run against the real 2027
discovery corpus (`cache-2027.db`, 76,601 members) to measure whether semantic retrieval closes the
recall gap, and by how much. Models: `BAAI/bge-small-en-v1.5` (384-dim bi-encoder) for embeddings,
`cross-encoder/ms-marco-MiniLM-L-6-v2` for reranking.

### 3.1 Junk filter — the cheapest, largest win **[measured]**
The corpus is flooded with `BuiltIn*` enum members (`BuiltInParameter`, `BuiltInCategory`, …). Masking
them at index time took **noise@1 from 8/79 → 0** on the original 79-query ranking snapshot. This is
free and applies regardless of ranker.

### 3.2 Retrieval comparison — 43 authored task-style queries **[measured]**
Natural-language task queries (e.g. "move an element to a new location", "get every wall in the
document") mapped to their correct member(s). A fresh set the fusion weights were **not** tuned on, so
it reads as quasi-held-out.

| Approach | recall@1 | @3 | @10 | MRR |
|---|---|---|---|---|
| Lexical (BM25F, keyword proxy) | 14/43 | 20 | 26 | 0.426 |
| Vector (BGE-small, description-weighted) | 18/43 | 24 | 33 | 0.527 |
| Hybrid RRF (lexical 1.5 : vector 1) | 19/43 | 26 | 32 | 0.543 |
| **Hybrid + cross-encoder reranker (pool 50)** | **23/43** | **29** | **34** | **0.629** |
| Hybrid + reranker (pool 100, return 20) | 23 | — | 36 | @20 = 37 |

- Embeddings alone beat the keyword proxy by +4 rank-1 / +0.10 MRR — the semantic recall the current
  ranker cannot reach.
- Hybrid > either alone: RRF recovers exact name/path matches vector drops.
- The **cross-encoder reranker is the single biggest lever** (+4 rank-1 over fused).

> **Measured after shipping (issue #188, 2026-09-03).** Two changes to the shipped pipeline, measured on the
> same 43 labels against the real 2027 corpus (76,600 members, the add-in's own namespace and core flags)
> with the shipped models and pool 20: (1) the indexed side of the keyword pass also carries each
> adjacent pair of identifier parts joined, so `NewFootPrintRoof` indexes `footprint` as well as `foot`,
> `print` (Revit spells the compound both ways and people write it as one word); (2) the reranker text
> carries a callable's parameter list, since a task description names what a call takes and the summary
> alone gave the reranker nothing to match.
>
> | shipped pipeline | recall@1 | @3 | @10 | issue query |
> |---|---|---|---|---|
> | before (#154 as shipped) | 24/43 | 29 | 34 | absent |
> | + compound bridge | 25 | 31 | 35 | 11 |
> | + bridge + parameter types only (tried, rejected) | 24 | 32 | 35 | 1 |
> | + bridge + full parameter list (ships) | **25** | **33** | **35** | **1** |
>
> Per query, ten deep, every labelled query whose rank moved (0 = not returned):
>
> | query | shipped | + bridge | + bridge + full list |
> |---|---|---|---|
> | create a footprint roof from a curve array on a level with a roof type | 0 | 11 | 1 |
> | export the view to dwg | 14 | 1 | 1 |
> | get an element by its id | 6 | 2 | 1 |
> | create a section view | 4 | 4 | 2 |
> | create a sheet | 17 | 18 | 15 |
> | load a family from a file | 5 | 5 | 3 |
> | tag a door | 46 | 39 | 39 |
> | place a family instance in the model | 36 | 32 | 32 |
> | place a view on a sheet | 114 | 113 | 113 |
> | create a 3d view | 5 | 5 | 9 |
> | move an element | 1 | 1 | 2 |
> | prompt the user to pick an element | 5 | 5 | 6 |
> | create a direct shape from geometry | 18 | 18 | 20 |
> | intersect two solids | 44 | 45 | 45 |
>
> So the bridge moved nothing on the first page down; the parameter list costs "move an element" 1→2,
> "create a 3d view" 5→9 and "prompt the user to pick an element" 5→6, and wins "get an element by its
> id" 2→1, "create a section view" 4→2, "load a family from a file" 5→3 and the issue's own query 11→1.
> Parameter *types only* was measured as a middle ground and lost to the full list on every aggregate.
> The 43 labels and the corpus dump live outside the repo (POC scratch). `TestRealCorpusRecall`
> reproduces the aggregate rows from a `members.json` that carries `signature`, `namespace` and `core`
> (a discovery-cache dump does; the original POC dump does not, and then measures the pre-#188
> reranker text with prefix-derived core flags).

### 3.3 Recall ceiling — recall is buried, not lost **[measured]**
The correct member is present in *either* retriever's candidate pool for **40/43 (93%)** at top-200
(38/43 at top-50, 39/43 at top-100). So the surfacing problem dominates the coverage problem: widening
the returned set and the rerank pool recovers most of the gap. The 3 genuinely unreachable queries are
multi-step **idioms** where no single member is the answer ("get all walls" → the
`FilteredElementCollector.OfClass` pattern) — how-to-doc territory, not API-search failures.

### 3.4 Field weighting and namespace filter **[measured]**
- Per-field embeddings (name / path / summary as separate vectors), weighted, beat a single blended
  embedding. Name/path weighted for lexical; description weighted for vector.
- The namespace filter works as a **pre-ranking mask** (out-of-namespace members set to −∞ before
  scoring), so scoping never costs relevance — verified under both retrievers.

### 3.5 Honesty caveat on the baseline
The "Lexical (BM25F)" row is a **keyword-baseline proxy computed in the POC, not the shipped C#
ranker**. The 43 task-queries are not in the ranking snapshot, so no real-ranker baseline exists for
them (it would need a live VM run). The deltas among vector / hybrid / reranker are apples-to-apples;
the gap to the *real* current ranker is anchored only on the separate 79-query snapshot (noise@1 = 8).

---

## 4. Proposed architecture

Move all ranking to the broker; keep the add-in as the reflection source only.

```
┌─ ADD-IN (C#) ─ the only thing that can see this Revit's loaded assemblies ─┐
│  Reflection ONLY: enumerate API members (name, namespace/path, summary,    │
│  member_id) for THIS version + installed add-ins. Ships the corpus over    │
│  the wire. No FTS5, no ranking.                                             │
└────────────────────────────────────────────────────────────────────────────┘
        │ corpus rows (once per version / add-in-set fingerprint)
        ▼
┌─ BROKER (Go) ─ owns all retrieval and ranking ─────────────────────────────┐
│  ENCODER (ONNX Runtime, bi-encoder)                                         │
│  ├─ index build: embed each member's name / path / summary → 3 vectors     │
│  ├─ vector index (per version, cached to disk)                             │
│  ├─ lexical index (BM25F over name / path / desc)   [open: Go vs C# FTS5]  │
│  └─ how-to-docs index (embedded once, version-independent, shipped)        │
│                                                                            │
│  QUERY: encode query → vector + lexical retrieve → namespace pre-mask →    │
│         RRF fuse → cross-encoder rerank(pool ~100) → merge API+docs →       │
│         return ~15-20 + guidance note                                      │
└────────────────────────────────────────────────────────────────────────────┘
        ▲
        │ MCP: search_functions(query, namespace, top_n)
     agent
```

**Indexing lifecycle [proposed].** On instance connect + version detect, the broker checks for a
cached index keyed by `(revit_version, add-in-set)`. If absent, it pulls the member corpus from the
add-in (extends today's discovery-sync wire), embeds it (junk-filtered, §3.1), and caches to disk. The
how-to-docs corpus is embedded once at release time and shipped with the broker.

**Query-time pipeline [proposed], each stage POC-validated in §3:**
1. Encode the query into a per-field-weighted vector.
2. Vector (cosine) + lexical (BM25F) retrieval in parallel.
3. Namespace filter as a pre-ranking mask.
4. RRF fusion (lexical ~1.5 : vector 1).
5. Cross-encoder rerank over the fused top ~100.
6. Parallel how-to-docs search; merge the two streams.
7. Return ~15–20 results + the `guidance` note.

---

## 5. Query interface — one required long-form string

**Decision [proposed]:** a single **required** `query` field, a full natural-language sentence, not
keywords. Rationale: the vector encoder and the cross-encoder both want prose; BM25F tokens can be
*derived* from a sentence, but a sentence cannot be derived from keywords — so one rich field feeds
both retrievers, and it is the input an agent can least fill wrong. A second "keywords" field was
considered and rejected: it mostly serves the "I already know the identifier" case, which
`describe_function`/`list_functions` cover, and tier-1 exact-match inside the sentence handles the
residual.

The behavioral levers that actually elicit context (independent of field count): make it **required**;
teach it by **example in the schema description**; reinforce via the `guidance` note; and prime it in
skill.md (§6).

---

## 6. Agent-facing guidance (skill.md text, lands with the ranker)

An LLM agent knows what a bi-encoder and a reranker are, so naming the mechanism lets it craft better
queries. This text is **held to ship with the semantic ranker** — teaching sentence-queries against
today's lexical ranker would train agents toward the input it handles worst.

> - **`search_functions`** — semantic API search. Ranking is a **sentence-embedding (bi-encoder)
>   vector search, fused with a keyword (BM25) pass, then a cross-encoder reranker** re-reads your
>   query against the top candidates. Write `query` to exploit each stage:
>   - **Describe the task in one natural-language sentence.** The whole string is embedded as a single
>     vector, so intent, paraphrase, and synonyms match — you don't need the exact API name.
>     `{"query": "move an element to a new location"}`, not `{"query": "move"}`.
>   - **Name the concrete Revit type and the operation verb.** The type noun (Wall, View, Parameter,
>     FilteredElementCollector) and verb are the highest-weighted fields *and* give the fused keyword
>     pass an exact-match hit. "get every **wall** in the document" beats "get things".
>   - **Be specific; don't pad.** Every irrelevant word shifts the vector and costs recall.
>   - **Drop in an identifier if you suspect one.** A likely type/method name rides the keyword pass
>     for an exact boost while the sentence carries the semantics — the hybrid uses both.
>   - **Scope with `namespace`** when you know the area — it is a pre-ranking filter, so it never
>     costs relevance.
>   - Read the `guidance` note: nothing matched → reword (the target is in the pool ~93% of the time
>     under different wording, almost never truly absent); too many → add context or a more precise
>     term.

---

## 7. The `guidance` note (shipped now; rewritten for the ranker)

A retry hint attached to every `search_functions` response, steering by result-set shape. **This is
the one piece already implemented** (`discovery_tools.go`, `searchGuidance`), and its current wording
is accurate to today's keyword ranker.

| Result set | Nudge (current, keyword-ranker-accurate) |
|---|---|
| 0 matched | "Not absent — reword" (synonym / verb / domain noun) + `list_functions` |
| > 50 matched | "May rank below the page — narrow it: add context, a precise term, or a `namespace` scope" |
| workable set | "Top hit may still be wrong — a reworded retry is on the table" |

When the semantic ranker lands, the wording is rewritten to name the mechanism at the moment of
failure, e.g. empty → *"Matching is semantic (embedding) + keyword… describe the task in a sentence
and name the element type and operation"*; too-many → *"the reranker may have buried the one you
want… name the specific element type and operation, or scope with namespace."*

---

## 8. Open decisions

1. **Lexical in Go, or keep C# FTS5? [open]** Cleanest is BM25F in Go (one place, no version
   coupling), at the cost of reimplementing tokenization + shipping the corpus. Alternative: the
   broker calls the add-in's existing FTS5 *and* its own vector index and fuses — less rewrite, but
   keeps a wire round-trip and the version coupling for the lexical half.
2. **Index build timing/storage [open].** Lazy on first connect (simple; first-search latency) vs.
   background pre-build on connect.
3. **Latency budget [open] — the real go/no-go.** Encoder + rerank must fit the wire budget. ONNX
   Runtime timing on the target hardware (the Parallels VM / broker host) is the next thing to
   measure; it is not yet measured.
4. **Corpus freshness [open].** The add-in's surface changes with installed add-ins; the fingerprint
   and re-embed trigger need defining.
5. **How-to-docs corpus [open].** Source, format, and build step for the documentation corpus that
   rides the same pipeline.

---

## 9. What is shipped vs. pending

- **Shipped:** the `guidance` note (three branches), accurate to the current keyword ranker.
- **Pending, this design:** everything in §4 (encoder, vector/hybrid retrieval, reranker, docs merge),
  the §5 query-interface change, and the §6 skill.md teaching — all gated on resolving §8, with the
  latency budget (§8.3) as the first measurement to take.


---

## 10. Implementation record — what shipped, and what the spike measured

Everything here is **[measured]** in this repo's own code unless marked otherwise; the numbers come
from `go test -v` runs of the gated tests named beside them (Apple M1 Max unless stated).

### 10.1 The go/no-go spike (§8.3) — pure Go, and what it ruled out

The broker is a single cgo-free binary by PRD §04, and the release runner has no C toolchain, so
ONNX Runtime was never an option without breaking that. hugot's GoMLX (pure Go) backend was
measured instead (`scratchpad/onnx-spike`, fp32 and int8, on the Mac and natively inside the
Windows arm64 guest with 4 cores):

| Measurement | M1 Max | Windows guest (arm64, 4 cores) |
|---|---|---|
| BGE-small: one query embedding | 70 ms | 74 ms |
| BGE-small: corpus embedding throughput | 13.9 docs/s → **4.6 h** for 76,601 members × 3 fields | 13.4 docs/s → 4.8 h |
| ms-marco-MiniLM cross-encoder, pool 20 / 50 / 100 (fp32) | 0.95 s / 2.1 s / 4.3 s | 1.0 s / 2.4 s / 4.8 s |
| same, int8 model | 1.27 s / 3.3 s / 6.8 s | — |

Two conclusions decided the design:

1. **A transformer bi-encoder cannot embed the corpus live in pure Go.** Hours per index rules out
   §4's "embed on first connect"; precomputing per-Revit-version vectors offline would have meant a
   ~79 MB download per version and no coverage of third-party add-ins.
2. **The reranker is affordable only at a small pool.** Re-running the POC eval at pools 10–100
   (`eval_pool.py`): pool 20 already gives the full gain — recall@1 23/43, MRR 0.629, identical to
   pool 50 — so `semsearch.DefaultRerankPool = 20`, ~1 s.

### 10.2 The substitution that made §4 buildable: static embeddings

model2vec static models (a token-embedding table, mean-pooled) were evaluated in the same harness
(`eval_static.py`) as the bi-encoder in the hybrid pipeline:

| Embedder | vector only | hybrid RRF | + rerank pool 20 | + rerank pool 50 |
|---|---|---|---|---|
| BGE-small (transformer, POC) | 18 / 0.527 | 19 / 0.543 | **23 / 0.629** | 23 / 0.629 |
| potion-base-8M (static) | 18 / 0.503 | 16 / 0.483 | **23 / 0.624** | 23 / 0.660 |
| potion-base-32M (static) | 18 / 0.521 | 15 / 0.477 | 23 / 0.633 | 23 / 0.662 |

(recall@1 / MRR on the 43 labelled queries.) Once the cross-encoder reranks, the 8M static model is
indistinguishable from BGE-small — and it embeds the whole corpus in **2.4 s** in Python, **1.4 s**
in Go (`TestRealCorpusRecall`). The retrieval stage only has to get the answer into the top 20; the
reranker does the discrimination. So the shipped bi-encoder is `potion-base-8M` (MIT, 30 MB), read
by `internal/semsearch/staticembed` — a pure-Go WordPiece tokenizer + safetensors reader with no
third-party dependency, pinned to the Python implementation by `TestParityWithPythonModel2Vec`
(identical token ids; cosine ≥ 0.9999 on the reference vectors).

### 10.3 What shipped (resolving §8)

| §8 decision | Resolution |
|---|---|
| 8.1 Lexical in Go or C# FTS5 | **Go.** `internal/semsearch` implements BM25F over name / path / summary with the POC's field weights, the identifier splitter mirroring the add-in's `IdentifierRelevance.SplitWords`, RRF (1.5 : 1, k = 60), the `BuiltIn*`/`PostableCommand` junk mask, the namespace pre-mask, and core-wins-exact-ties. The C# ranker is untouched and still answers as the fallback. |
| 8.2 Index build timing | **On attach, in the background, never blocking a call.** `internal/semsearch/manager` pages the corpus over a new add-in wire method `dump_members` (5,000 members a page, ~16 pages for 2027), builds lexical (0.3 s) then dense (1.4 s), and caches by corpus fingerprint (SHA-256 of the loaded assembly set, computed by `DiscoveryCache.CorpusFingerprint`) so a reconnect or a sibling instance reuses the index after one page. Until ready, `search_functions` forwards to the add-in's keyword ranker and the response says `ranker: keyword-fallback`. |
| 8.3 Latency budget | Query path: static embed < 1 ms, brute-force cosine over 3 × 68k × 256 ≈ 80 ms, int8 cross-encoder pool 20 ≈ 1.2–1.5 s on the M1 Max (fp32 was 0.95 s; the guest measured fp32 only). No ANN index needed at this corpus size. Cursor pages are slices of a small bounded cache of ranked lists (16 entries), so a second page costs nothing; the cursor's scope includes the corpus fingerprint and ranker so it cannot replay across ranked sets. |
| 8.4 Corpus freshness | The fingerprint. A fingerprint change between pages fails the build (the add-in re-synced mid-dump) and the next attach rebuilds; a failed build never blocks search (fallback). |
| 8.5 How-to-docs corpus | **Not shipped**; the merge point in §4 remains open. |

**Model bundling.** Both models are `go:embed`ded by `internal/semsearch/models`, fetched at build
time by `fetch-models.{sh,ps1}` against sha256 pins and never committed; CI and the release workflow
fetch them, and the release asserts `mcp-server -search-models` reports them bundled. A build
without them compiles and ranks lexical-only (`ranker: lexical`, said in every `guidance`).
Measured binary: **79.5 MB** with models (from 11.6 MB) — the cross-encoder ships int8 (23 MB) and
the static model fp32 (30 MB); hugot/GoMLX add ~15 MB of code.

**Go pipeline measured on the 43 labelled queries** (`TestRealCorpusRecall` with all three model
env vars set; real 2027 corpus, 68,410 non-junk members indexed; Apple M1 Max) — recall@1 / 3 / 10:

| Stage (Go, shipped code) | recall@1 | @3 | @10 | per query |
|---|---|---|---|---|
| lexical (BM25F) | 14 / 43 | 21 | 28 | 2 ms |
| hybrid RRF (potion-base-8M) | 17 / 43 | 23 | 33 | 77 ms |
| **hybrid + cross-encoder, pool 20 (what ships)** | **24 / 43** | **29** | **34** | 1.54 s |

Against the Python POC's 14 / 20 / 26, 16 / 22 / 32 and 23 / 29 / 33 for the same three stages: the
port reproduces the POC within one query at every rank. Lexical build 0.25 s, dense build 1.14 s.

**Cross-encoder parity** (`crossenc.TestScoresMatchPythonCrossEncoder`, int8): 0.9960 / 0.9978 /
0.0000 against Python fp32 sigmoid 0.9971 / 0.9987 / 0.0000 on the reference pairs; same ordering.

**Live, Revit 2025 (dev VM, remote mode, broker on the Mac)** — `TestSemanticSearchAnswersTaskSentences`
and the broker log: the corpus the add-in ships is **46,877 documented members** (2025 has fewer than
2027's 76,601), paged in 10 `dump_members` calls; the index was **ready 21 s after registration**
(wire transfer through the Parallels guest dominates — the same corpus indexes in ~1.5 s from a local
file); model load at broker start 139–157 ms; a semantic query round trip including the reranker
~1.5 s; `move an element to a new location` → `ElementTransformUtils.MoveElement`, `delete an element
from the document` → `Document.Delete`, `get the parameter of an element by its name` →
`Element.LookupParameter` all within the top 3; namespace pre-mask, cursor paging and the junk mask
hold live. The known miss reproduced too: `find every element of a given class in the document`
ranks `ElementClassFilter`'s constructor first and leaves `FilteredElementCollector.OfClass` outside
the top 5 — the idiom shape §3.3 predicted.

**Race detector.** hugot's Go backend uses unsafe pointer arithmetic that `-race`'s checkptr rejects
(fatal `pointer arithmetic result points to invalid allocation` in gomlx's matmul). Tests that run a
model carry a `!race` constraint; CI's `go test -race ./...` still covers everything except model
inference, and the plain gated runs cover that.

### 10.4 Still open after this implementation

- **§8.5 how-to-docs corpus** — the pipeline merges API hits only. Designed separately in
  [`howto-corpus-design.md`](howto-corpus-design.md), which supersedes §4's "embedded once, shipped"
  and "merge the two streams" sketches: the shared corpus is fetched, and the two corpora get their
  own tools with cross-promotion deferred.
- **Junk-mask escape hatch** — `BuiltInCategory` and friends are unreachable through search even
  when the query names them; `list_functions` / `describe_function` still reach them. A query-side
  exception (name the enum type → unmask it) is the obvious next step if agents hit this.
- **Retire the C# ranker** once the fallback has proved unnecessary in the field; today it is still
  the answer for the first seconds after connect and for model-less builds.
- **Go toolchain**: hugot requires Go 1.26, so `revit/mcp-server/go.mod` moved from 1.25.4.
- **Efficiency headroom, deliberately not taken yet** (from the pre-PR quality review): the ~700
  distinct namespace strings are embedded and stored once per member rather than once per string
  (~78 MB per index); `topIdx` sorts every positive-score doc to take 200; dense scoring is
  single-threaded; model loading (~sha256 of 53 MB, tokenizer parse, GoMLX graph) runs on the
  broker's startup path and is logged; the add-in's `dump_members` deserialises `params_json` for
  every row it ships and recounts the corpus per page. Each is a measured-cost, low-risk follow-up
  once the live latency numbers say which matters.
- **Structural junk rule**: the mask is a hardcoded type list; the deeper fix is an `enum` flag on
  the wire and a member-count threshold.
- **Attach/detach plumbing**: the manager is told about instances through a third broker hook
  (`Broker.Search`); an observer on `discovery.Router` would fence once for every consumer.
- **Corpus freshness after the deferred sync** (independent review of #154): the add-in re-checks its
  assembly set ~8 s after startup; if that sync changes the corpus after the index is ready, nothing
  today tells the broker (`register` carries no fingerprint). Ship the fingerprint on `register`
  and rebuild on change. Related: `DiscoveryService.DumpMembers` takes its three cache locks
  separately, so a page's rows and fingerprint can straddle a sync; the mid-dump fingerprint check
  catches the cross-page case, not the intra-page one.
- **Result semantics worth documenting for agents**: `total_matched` is now the size of the fused
  candidate set (≤ 400), not a corpus match count; `score` is the cross-encoder's sigmoid for the
  reranked head and 1/rank beyond it.
- **Build-time network**: CI and the release runner fetch the two models from Hugging Face on every
  run (sha256-pinned, revision-pinned); a mirror or cache would remove the egress dependency.
