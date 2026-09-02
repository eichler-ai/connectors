# search_functions ranking redesign — semantic hybrid retrieval

**Status: design note / proposal (issue #107).** Draft. Captures the direction, the POC evidence
behind it, and the open decisions — it is not yet a committed plan, and nothing here has shipped
except the agent-facing `guidance` note (see §7). Design rationale for the current tool:
[`PRD.md`](PRD.md) §08 (API discovery); the current implementation is mapped in §2 below.

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
