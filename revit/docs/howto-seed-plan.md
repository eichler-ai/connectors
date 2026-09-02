# How-to corpus — seeding strategy and document audit

**Status: for audit, before implementation.** Companion to
[`howto-corpus-design.md`](howto-corpus-design.md). §1 settles how corpus changes are versioned and
shipped (the design note's open decision D1). §2 is the seeding strategy. §3 is the audit: every
candidate document the harness can yield today, with its scope, so each can be accepted, split,
merged or dropped before an extractor is written. §4 lists the decisions the audit needs.

---

## 1. Versioning: one release stream, per-component change detection

**[decided]** The corpus ships **with** the connector release, not beside it. Change frequency will be
high at first and settle, and a separate fetch channel (design note §5) buys nothing while the
corpus is small and the release pipeline is cheap; it can be added later without changing the
document format. This resolves D1 as "bundled", and D2 with it (the corpus rides the same signed,
checksummed asset as the code).

What "bundled" means concretely:

- **One version line.** A release tag `vX.Y.Z` covers add-in, broker and corpus. A release can change
  any subset; the tag moves regardless. `mcp-server -version` and `get_skills`' `build` field gain a
  `howto_corpus` version (the corpus's own monotonic `corpus_version`, stamped at build) beside the
  source revision, so "which how-tos is this broker serving" is answerable the same way "which
  skill.md" already is (issue #116).
- **The corpus is embedded in the broker** (`go:embed` of `revit/howto/corpus.jsonl` and its
  verification sidecar), the same way `skill.md` is. Single binary stays true; no runtime fetch, no
  cache, no offline case.
- **Per-component change detection in the release**, so a corpus-only release costs the user nothing
  but a broker restart. The release zip gains `manifest.json`: `{ addin-2025, addin-2027, server,
  howto }` → sha256 of each payload. `install.ps1` records those hashes in `installed-version.json`
  and, on update, **skips any component whose hash is unchanged** — so a release that changed only
  the corpus (or only the broker) does not touch the add-in folder and never asks to close Revit.
  Today the script copies every payload on every version change and closes Revit for it.
- **Release notes name what changed.** The release workflow diffs `corpus.jsonl` against the previous
  tag and lists added / superseded document ids under a "How-tos" heading, beside the code changes.
  That is the human-facing change detection; the manifest is the machine-facing one.
- **The update check stays one check.** The broker's existing 6-hourly poll compares the release tag
  as it does now; the ribbon's "update available" is right for a corpus-only release too, because the
  install it triggers is a cheap no-Revit-close one.

Cost of this choice: a corpus edit needs a release. That is the point while churn is high — every
change is reviewed, signed and recorded — and the release pipeline already exists. If corpus churn
outpaces what the release cadence tolerates once the code settles, the design note's fetch channel is
the escape hatch, unchanged.

**Local corpus** (design note §5) is unaffected: it is per-machine and never versioned by the release.

## 2. Seeding strategy

The seed is extracted from the harness, and only from tests that opt in. Mechanics:

1. **Audit first (this document, §3).** Decide per candidate: accept as one document, split, merge, or
   skip. Decide the kind (`howto`, `negative`, `pitfall`). Write the `task` sentence by hand — an agent
   reads it, and a test name turned into prose is not that.
2. **Annotate, don't extract blindly.** An accepted test gets a `// howto:` header block above its
   `t.Run` / `func Test`: `id`, `task`, optional `kind`, optional `pitfall:` lines. The script literal,
   the members it calls, and the recorded queries in the surrounding comment are extracted; the rest
   of the comment is not (it is written for maintainers, not agents). A test without the header yields
   nothing, so guard-style tests never leak into the corpus by accident.
3. **Fixture preamble is stripped at extraction.** Harness scripts open with a fixture preamble
   (`fixtureWritePreamble`) that looks the document up by title; the how-to's script starts at the
   first line after it and uses `Document` as the routed document. The verification sweep (design
   note §3) runs the how-to's script, so the hash it stamps is the hash the agent reads.
4. **Dialect is current.** #146 Phase 3 (#160) rewrote the harness to the `WithTransaction` dialect,
   so extracted scripts are already in the shipped dialect; the design note's warning that seed
   scripts would fail the sweep no longer applies to a seed extracted after #160.
5. **Extractor is a build step**, `go run ./cmd/howto-extract`, committed output at
   `revit/howto/corpus.jsonl`; CI fails if the committed file is stale against the tests. The sidecar
   `revit/howto/verified.jsonl` is written only by the tier-2 sweep and committed with it.
6. **Then the ranking corpus.** Every recorded `queries.miss` becomes a candidate row for the how-to
   equivalent of `ranking-corpus.tsv`, once `search_howto` exists to grade it.

## 3. Audit: candidate documents

Sources: `revit/test-harness/*_test.go` at `35fe333` (post-#160). "Recorded" means the pitfall or
query is written in the test's own comment; nothing below is inferred beyond that. **Value**: H = a
pitfall an agent would otherwise hit, or an idiom `search_functions` cannot answer; M = a worked
example of a common task; L = covered by `get_skills` or by `describe_function` alone.

### 3a. Revit API tasks

| # | Proposed id | Source | Kind | Task (draft) | Recorded pitfalls / queries | Value | Recommendation |
|---|---|---|---|---|---|---|---|
| 1 | `wall-create-on-level` | phase A `CreateWall` | howto | Create a wall from a line on a level using a Basic wall type | — | M | accept; merge #16's type-lookup pitfall in |
| 2 | `collect-elements-by-category` | phase A `QueryElementsByCategory` | howto | Find every element of a category in the document | the `FilteredElementCollector` idiom `search_functions` cannot return as a member (design note §3.3; live miss on 2025) | H | accept; add a sibling `collect-elements-by-class` (OfClass) — the recorded miss "get all walls" |
| 3 | `parameter-get-set-by-name` | phase A `GetSetParameter` | howto | Read and set an element parameter by name | — | M | accept |
| 4 | `element-delete` | phase A `DeleteElement` | howto | Delete an element and confirm it is gone | — | L | accept as short doc, or fold into #3 as "CRUD basics" — decide |
| 5 | `shared-parameter-create-and-bind` | phase A `CreateSharedParameter` | howto + negative | Define a shared parameter in a fresh shared-parameter file and bind it to a category | (1) there is **no** `Application.CreateSharedParameterFile`: write the tab-delimited file with `System.IO`, then `OpenSharedParameterFile`; (2) `BuiltInParameterGroup` is gone → `GroupTypeId` (2025+) | H | accept; **split**: the negative is its own `negative` document so "create shared parameter file" resolves to a "stop looking" answer |
| 6 | `group-edit-propagates` | phase A `EditGroupPropagatesToAllInstances` | howto + negative + pitfall | Edit a group so every placed instance changes | no group-edit-scope API (no `Document.EditGroup`); editing a member directly is allowed with one placed instance (warning) and **refused** with two or more (rolled back as `status=error`); `ElementTransformUtils.MoveElement` on a member's own id silently does nothing — move the group's id instead | H | accept; the no-scope result is a `negative` document, the MoveElement trap a `pitfall` |
| 7 | `room-create-and-tag` | phase B `CreateRoomAndTagIt` | howto | Create a room on a level and tag it | `Level.Create` does **not** create a floor-plan view; without a level-scoped `ViewPlan`, `NewRoomTag`/`NewDimension` fail with a deep `ArgumentNullException`; `NewRoomTag` overload note | H | accept; the view pitfall also stands alone (see #21) |
| 8 | `door-place-in-wall` | phase B `PlaceDoorInWall` | howto | Place a door family instance hosted in a wall | `FamilySymbol.Activate()` before `NewFamilyInstance` or it throws | H | accept |
| 9 | `dimension-between-walls` | phase B `DimensionBetweenWalls` | howto | Dimension between two walls in a plan view | needs a view scoped to the level (shares #7's pitfall) | M | accept |
| 10 | `schedule-create-wall` | phase B `CreateWallSchedule` | howto | Create a wall schedule with fields | — | M | accept |
| 11 | `floor-create-from-loop` | phase C `CreateFloor` | howto | Create a floor from a closed curve loop | — | M | accept |
| 12 | `grid-create` | phase C `CreateGrid` | howto | Create a grid line | — | L | accept as short doc or merge with #1/#21 into "datum elements" — decide |
| 13 | `sheet-create-and-place-view` | phase C `CreateSheetAndPlaceView` | howto + pitfall | Create a sheet and place a view on it | recorded miss: `create sheet place view` never surfaced `ViewSheet.Create` (issue #65; tsv row 1); `Viewport.Create` throws `ArgumentException` for a view already placed on any sheet (the default template ships 22 sheets with views on them) — check `Viewport.CanAddViewToSheet` or create a fresh `ViewPlan` | H | accept |
| 14 | `text-note-create` | phase C `CreateTextNote` | howto + pitfall | Add a text note to a view | `TextNote.Text` comes back with a trailing `\r` appended, undocumented in Autodesk's XML doc | M | accept; pitfall is the value |
| 15 | `export-view-dwg` | validation #1 | howto | Export a view to DWG | recorded hit: `export view to dwg` → `Document.Export` rank 6, `DWGExportOptions` rank 1 | M | accept |
| 16 | `walls-closed-footprint-confirm-joins` | validation #2 | howto | (the shipped example) | recorded miss/hit; `AreElementsJoined` vs `IsWallJoinAllowedAtEnd` | H | accept (already drafted) |
| 17 | `family-build-load-place` | validation #3 | howto | Build a minimal family document, load it, place an instance | `LoadFamily` needs **no open transaction on the target**: load between blocks, place inside one; the two-call split | H | accept; dialect-sensitive, extract post-#160 text |
| 18 | `stairs-create-with-editscope` | interactive `StairsAreCreatableWithSettleOnRequest` | howto | Create stairs with `StairsEditScope` | `EditScope.Commit` is refused while a connector transaction is open; `Connector.Settle` ordering | H | accept; connector-flavoured but the API pattern is real |
| 19 | `room-on-created-level-computation-height` | interactive `RoomOnScriptCreatedLevelNeedsComputationHeight` | pitfall | A room on a script-created level reports no area until the level's computation height is set | recipe: `LEVEL_ROOM_COMPUTATION_HEIGHT` inside the wall body; A/B not controlled (comment says so) | M | accept as `pitfall`; state the uncontrolled A/B honestly |
| 20 | `sheet-titleblock-never-swap-on-instance` | interactive `SheetTitleBlockRecreateWorkaroundIsSafe` | pitfall | Changing a sheet's title block: create with the right type, never set `SheetTitleBlockId` on a live instance | **Revit crash** (issue #113) | H | accept; highest-value pitfall in the set |
| 21 | `level-create-with-plan-view` | harness `TestCreateLevel` + #7's finding | howto | Create a level and a floor-plan view for it | the view does not come for free (from #7) | M | accept as the "datum + view" doc that #7, #9 and #12 reference |

### 3b. Connector mechanics (in scope — decided; worked examples `get_skills` states as rules but cannot show)

| # | Proposed id | Source | Kind | Task (draft) | Recorded pitfalls | Value | Recommendation |
|---|---|---|---|---|---|---|---|
| 22 | `write-inside-withtransaction` | interactive `DocumentsAreNotModifiableUntilABlockOpens`, `WriteOutsideATransactionCarriesAnActionableCode`, `WithTransactionReturnsTheBodysValue` | howto | Read at top level; write inside `Connector.WithTransaction`, one block per batch; use the returned value | `script-write-outside-transaction`; the `Func<T>` form returns the body's value | H | accept; **the** dialect document; keep `get_skills` as the rule, this as the example |
| 23 | `self-transacting-calls-between-blocks` | `external_commit_test`, `TargetMustNotBeModifiable…`, `StairsEditScopeCannotCommit…` | howto + pitfall | `LoadFamily`, `Export`, `RequestViewChange`, `EditScope.Commit` must run **between** blocks | `script-target-must-not-be-modifiable`; #160's own finding that such commits used to be rolled back | H | accept |
| 24 | `subtransaction-savepoint` | `SubTransactionIsASavepoint…` | howto | Use a native `SubTransaction` as a savepoint inside a block; `using` + Commit/RollBack | Dispose alone rolls back; start outside any transaction is refused with its own code | M | accept |
| 25 | `document-create-project-or-family-and-write` | harness `TestApplicationCreatesDocuments`, `TestCreatedDocumentIsWritable` | howto | Create a new project or family document and write into it in the same run | created document is headless (no window, `active:false`, #118); writes survive; a throw rolls created documents back too; activation refused inside a block | H | accept; large — consider **split** into create / write / headless-trap |
| 26 | `document-close-created-by-routing-away` | interactive `CreatedDocumentCloseRequiresRoutingAwayFromIt` | howto + pitfall | Close a document the script created: route the call elsewhere and find it by title | closing while routed at it fails for that call only (#114) | H | accept |
| 27 | `document-id-routing-background` | `document_routing_test` | howto | Run a script against a background document by `document_id`; `UIDocument` is null there | unknown id fails with the candidate list | M | accept, short |
| 28 | `publish-files-and-audit-trail` | `file_exchange_test` | howto | Publish an output file and find it; where scripts and logs land | overwrite fails per file unless the flag is set | M | accept |
| 29 | `return-projections-not-elements` | `return_value_test` | pitfall | Return a projection of the data, not a Revit element; `output` vs `return_value` | element returns a no-display-form marker; console noise is separate | M | accept as `pitfall` |
| 30 | `settle-then-lifecycle-action` | interactive `SettleMakesLifecycleActionsReachable…`, harness `ConnectorSettleIsConfirmationGatedLive` | howto | Save-as or close in the same run after `Connector.Settle` | confirmation gate; Revit's transaction-phase check precedes event dispatch | M | accept |
| 31 | `undo-redo-and-labels` | interactive `UndoLabel…`, `UndoAndRedoTools…` | howto | Label a run and undo/redo it with the tools | undo of a person's work vs the agent's is distinguishable by name | M | accept once #146 Phase 2c is stable |
| 32 | `mutation-report-reading` | interactive `MutationReportDescribesWhatTheRunChanged` | howto | Read the `mutations` report to confirm what a run changed | caught throws contribute nothing; settle-discarded documents are left out | L | defer; `get_skills` may suffice |
| 33 | `withtransaction-body-throws-and-script-catches` | interactive `WithTransactionRecoversWhenItsBodyThrows…` | pitfall | A body that throws is rolled back even if the script catches; the document stays usable | — | M | accept as `pitfall` |

### 3c. Not candidates

| Source | Why not |
|---|---|
| `denylist_bypass_test.go`, `TestDenylistRejectsOwnTransaction`, lifecycle-gate tests | guard tests; the rule is in `get_skills`, and an example of *breaking* a guard is not a how-to |
| `memcheck_test.go`, `memory_reporting_test.go`, `execution_lifecycle_test.go` (cancel/poll), `zz_cleanup_check_test.go` | connector plumbing; `get_skills` covers the agent-facing part |
| `connector_api_discovery_test.go`, `semantic_search_test.go`, `discovery_test.go` | tests of the discovery tools themselves |
| `TestDialogsAreStillAutoSuppressed` | behaviour the connector handles without the agent's involvement; a `notices[]` matter, not a task |

### 3d. Negative documents to add explicitly

Recorded "this does not exist" results deserve their own `negative` documents, because the semantic
ranker always returns something:

- no `Application.CreateSharedParameterFile` (from #5)
- no group-edit-scope API (from #6)
- no floor-plan view is created with a level (from #7)
- `BuiltInParameterGroup` / `ParameterType` gone in 2025+ (`GroupTypeId` / `SpecTypeId`; from #5 and the skill's own rule)

### 3e. Worked example: `group-edit-propagates`, fleshed out

Audit row #6 is written out in full as the level-of-detail reference, split as the audit proposed:

- [`howto-example-group-edit-propagates.json`](howto-example-group-edit-propagates.json) — the how-to
  (kind `howto`, ~2 KB script, three pitfalls; no `queries` — the test records the research outcome,
  not the query strings, and none were invented).
- [`howto-example-group-edit-mode-api-does-not-exist.json`](howto-example-group-edit-mode-api-does-not-exist.json)
  — the negative (kind `negative`, no members, the "stop looking" answer with the route to take).
- [`howto-example-group-member-move-silently-does-nothing.json`](howto-example-group-member-move-silently-does-nothing.json)
  — the pitfall (kind `pitfall`, no script, one symptom → cause → fix).

All three validate against the schema. None carries a verification stamp: the how-to's script is the
test's script minus the test-only proof scaffolding (a third instance placed for comparison), so no
harness run has executed this exact text — the sweep will.

**Level-of-detail guidelines the example sets** (these are what `/triage-howto-submission` enforces
at its edit step, §4c):

1. `task` is two sentences: the task as an agent would phrase it, then the one-line shape of the
   answer. It is the embedding text, so it names the element type, the operation and the key member
   nouns (`GroupType`, `ungroup`) in plain words.
2. `summary` is the recipe in prose, four to six lines, naming members in order. It is what
   `describe_howto` shows first; the script is what the agent copies.
3. `script` is complete and runnable against a blank document: any setup a real task would already
   have is included but labelled as setup, and numbered comments mark the steps that *are* the
   how-to. Test-only assertions and comparison scaffolding are removed. Target: under 3 KB; the
   16 KB bound is for exceptional cases.
4. One `pitfalls[]` entry per distinct mistake, each with the symptom the agent actually sees
   (error text quoted where the test recorded it), the cause, and the fix as an instruction. A
   pitfall that is a task in its own right (the silent `MoveElement`) becomes its own `pitfall`
   document instead of a fourth entry.
5. A "this does not exist" result is its own `negative` document, with the member it names as free
   text in `pitfalls[].members` (since it is not a real member) and the route to take in `summary`.
6. `queries` records only what a session actually typed and saw, in the words used; nothing is
   invented to make the record look complete. Where the source is a test comment rather than a
   transcript, the entry is kept minimal.
7. `provenance.ref` names the test function, and — for a document derived from a *comment* rather
   than from executed code (the pitfall here) — says so.

## 4. Growth: `submit_howto` → tagged issue → `/triage-howto-submission` → corpus

The seed is a one-off; the corpus grows because agents that just learned something can hand it in.
That path is as important as the seed and moves **ahead of** search in the implementation order
(§6): both the extractor and the submit tool emit schema documents, and a review queue can start
filling before `search_howto` exists to serve it.

### 4a. What the agent is told (`skill.md`)

One bullet, under the discovery tools, within the token budget (something of equal size comes out):

> - **`submit_howto`** — when a task needed more than one search, a reformulated query, or a pitfall
>   you hit and got past, hand it in: the task in one sentence, the working script, the members, the
>   queries that missed and the one that hit, the pitfall as symptom → cause → fix. To improve an
>   existing how-to (a missing pitfall, a better script, a version note), pass its `id` as `supersedes`
>   with only the fields you changed. It saves to your own how-to corpus at once and, with
>   `confirm_submission: true`, prepares a scrubbed GitHub issue for the maintainers to review.
>   Submit **after** the script ran successfully, never speculatively.

The trigger is the important part: the moment of value is when the agent has *just* recovered from a
miss, which is exactly the recorded episode the corpus wants. `get_skills` also tells the agent what
a submission must not contain (project data, paths, names), so the scrubber is a backstop rather
than the first line.

### 4b. `submit_howto` — tool contract

```
submit_howto(
  title, task, script, members[],           # required for a NEW document
  pitfalls[]?, queries?, tags?, summary?,   # schema fields, optional
  supersedes?,                              # id of an existing how-to this one improves (see below)
  change_note?,                             # one sentence: what changed and why (required with supersedes)
  instance_id?,                             # which Revit verified it (defaults as for discovery)
  confirm_submission: bool                  # outward half; default false
) -> {
  document,        # the schema-valid document as written locally (id assigned: slug of title)
  local_path,      # <exchange-root>/howto/local/<id>.json
  verified,        # the session stamp, if the exact script ran successfully this session, else null
  submission?: {   # only with confirm_submission: true
    scrubbed_document,   # what leaves the machine, shown in full
    outbox_path,         # <exchange-root>/howto/outbox/<id>.json -- the issue body
    issue_url            # prefilled https://github.com/eichler-ai/connectors/issues/new?labels=howto-submission&title=...
  },
  notices[], guidance
}
```

**Improving an existing how-to.** With `supersedes: <id>`, the tool loads that document (local first,
then the embedded/shared corpus), overlays only the fields the call supplied, and produces a
complete new document whose `supersedes` points at the old one — the corpus's append-only edit
shape (design note §6). So an agent that found one missing pitfall submits just `pitfalls` and
`change_note`; `title`, `task`, `script` and the rest carry over. Rules: the new document gets a
fresh id derived from the old one (`<old-id>-2`, then `-3` …) unless the caller names one; a
`script` change without a successful run in this session is accepted locally but flagged
`unverified-script-change` in the issue; `queries` and `pitfalls` are *merged* (appended, de-duplicated
by text) rather than replaced, since they are evidence; `change_note` becomes the first line of the
issue body and the label gains `howto-edit` beside `howto-submission`.

Behaviour, in order:

1. **Validate** against `howto-schema.json`; a failing field is a `howto-invalid` error naming it.
   With `supersedes`, also check the target exists and is not itself superseded (point at the head
   of the chain instead, and say so).
2. **Write locally first** (`provenance.kind: "local"`), so the submitter's own `search_howto` serves
   it immediately. Re-submitting the same `id` overwrites the local file and says so.
3. **Session stamp**: if this session executed exactly this `script` text and it succeeded, append
   a `by: "session"` line to the local sidecar. This needs the broker to keep the sha256 of every
   executed script for the execution record's lifetime (design note §6, still **[proposed]**).
4. **Gate**: without `confirm_submission: true`, stop here and return
   `howto-submission-confirmation-required` (info) with the document, so the agent can show the user
   what would be sent. With it:
5. **Scrub** every text field and the script's string literals; refuse (`howto-submission-unscrubbed`,
   naming field and line) if any path or host survives.
6. **Write the outbox file and return the prefilled issue URL.** The broker never files the issue: it
   holds no token and spawns nothing (design note §6). **Filing is the agent's job**, using the
   `gh` the agent session already has: the returned `guidance` says
   `gh issue create --label howto-submission --title "<title>" --body-file "<outbox_path>"`, and the
   agent runs it under its own permission model, which is where the user sees and approves the
   outward action. An agent without `gh` hands the user the prefilled URL and the outbox path.

The outbox file *is* the issue body: a one-line summary, the target Revit version and connector
version, a checklist for the reviewer (schema valid · script ran on `<version>` · scrubbed · not a
duplicate of …), and the document in a fenced ```json block so the triage command can parse it back
out verbatim.

### 4c. `/triage-howto-submission` — the maintainer's command

Modelled on `/triage-issues`: takes issue numbers (`/triage-howto-submission 171 172`), refuses to
choose issues itself, and loads `revit-connector-development` for the harness rules. Per issue:

1. **Read and parse.** Pull the fenced JSON out of the issue body; validate against the schema; a
   parse or schema failure is a comment on the issue asking the submitter for the outbox file, not a
   guess at what they meant.
2. **Scrub again, by eye.** The tool's scrubber is a filter; the maintainer reads the document for
   anything project-specific before it runs anywhere. Anything found is redacted in the comment and
   the reviewer says what was removed.
3. **Run it, on a disposable fixture.** Route the script at a blank fixture document on the
   connected Revit (the sweep's fixture rule, design note §3), capture status, diagnostic and return
   value, and record the outcome as a comment. A failure is not a rejection by itself — the fix may be
   a one-line edit — but nothing enters the corpus without a passing run on at least one version.
   This is the `howto-reviewed` gate made concrete: the human reads before the script runs.
4. **De-duplicate, or apply the edit.** For a new document, search the existing corpus (`search_howto`
   once it exists; a `members`-set match until then): a near-duplicate becomes a `supersedes` of the
   older document if it is better, or a comment pointing at the existing one if not. For a
   `howto-edit` submission, diff the new document against the one it supersedes (the command prints
   the field-level diff), and judge the *change*, not the whole document: a good pitfall added to an
   otherwise unchanged how-to is accepted on the strength of the pitfall; a rewritten script is
   re-run before it replaces the old one (step 3 applies to the new script text).
5. **Edit.** The maintainer edits `task`, `title`, `pitfalls` wording and `tags` in place — this is
   where prose quality is enforced, and it is a human edit, not the agent's. `queries.miss` is kept
   verbatim: it is evidence, not prose.
6. **Append and stamp.** Append the final document to `revit/howto/corpus.jsonl` with
   `provenance.kind: "submission"`, `ref` = the issue URL, `reviewed_by` = the maintainer's login
   (for an edit, `supersedes` set, so search hides the old document from then on and
   `describe_howto` on the old id points forward);
   append the harness stamp from step 3 to `revit/howto/verified.jsonl`; open one PR per triage run
   listing the issues it closes (`Closes #171`), CI validates both files.
7. **Report.** Issues closed, documents added, documents superseded, and — the same net-count
   discipline `/triage-issues` uses — the queue size before and after.

The command lives at `.claude/commands/triage-howto-submission.md` (beside `triage-issues.md`) and is written when the
validator and the fixture-run helper exist (§6 step 2); its text is this section.

### 4d. Queue hygiene

- Label `howto-submission` is applied by the prefilled URL and by the `gh` command; an issue without
  the label is not in the queue.
- A submission that mentions a document title, a file path or a machine name in *any* field is
  closed with a comment, never edited into shape by the maintainer — the point of the scrubber is
  that nothing private reaches the tracker, and a leak is a bug report against the scrubber.
- Rate: a session may open at most one submission issue per how-to `id`; re-submissions of the same
  id add a comment to the open issue (the `gh` command the guidance emits checks for an open issue
  with the id in its title first).

## 5. Decisions the audit needs

1. **Granularity.** One document per subtest (≈33) versus merged task bundles (≈20). Recommendation:
   one per subtest, with `pitfall`/`negative` split out where marked; small documents rank and read
   better, and `members` cross-links do the grouping.
2. **Scope — decided:** a how-to can cover *any* relevant usage topic, connector mechanics included.
   The 3b rows are in. `get_skills` keeps the rules and the orientation; the corpus holds worked
   examples of anything an agent does with the connector, whether the subject is a Revit API or the
   connector's own transaction model, documents, files or tools.
3. **Which 3a rows to merge** (#4 into #3; #12 into #21) — marked "decide" above.
4. **Whether to include #31/#32** before #146 Phase 2c settles.
5. **Versioning as in §1** (bundled release stream, per-component install skip, corpus version in
   `-version`/`get_skills`) — confirms or reopens D1/D2 of the design note.
6. **Filing by the agent's `gh`** (§4b step 6) rather than by the broker — confirms the design
   note's no-token / no-subprocess decision while still producing a tagged issue automatically for
   any agent session that has `gh`.

Nothing is annotated or extracted until this table is settled.

## 6. Implementation order [proposed, revised]

1. Schemas + validator (Go) — shared by everything below.
2. **`submit_howto`** (local write, gate, scrubber, outbox + prefilled URL, `skill.md` bullet) and
   the fixture-run helper; then `/triage-howto-submission` as a command file. The queue can start
   filling from real sessions while the seed is audited.
3. Harness extractor + the audited seed (`revit/howto/corpus.jsonl`), the tier-2 sweep writing the
   sidecar, and the corpus embedded in the broker with its version in `-version` / `get_skills`.
4. Generalised index + `search_howto` / `describe_howto`; local corpus indexed alongside.
5. Release manifest + install.ps1 per-component skip; release-notes diff of the corpus.
