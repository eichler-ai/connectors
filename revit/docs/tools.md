# Tool & script-globals reference

The agent-facing surface of the Revit connector: eight MCP tools served by the MCP Server
(broker), and the globals available inside an `execute_script` script. The connector also
serves this material to agents directly — `get_skills` returns a built-in guide covering the
same ground — so this page is primarily for humans evaluating or debugging the connector.

Design rationale for all of it: [`PRD.md`](PRD.md) §06 (execution), §07 (dialogs), §08
(discovery), §09 (file exchange), §14 (the script API tiers).

## The tools

| Tool | Needs a live Revit? | Purpose |
|---|---|---|
| `list_instances` | no* | connected Revit instances + their document snapshots and process memory (*a `busy`/`pending` instance is re-checked with the add-in first, one bounded wait per call, #54) |
| `execute_script` | yes | compile & run C# in a Revit instance |
| `poll_execution` | yes | wait on / re-check a long-running execution |
| `cancel_execution` | yes | cooperative cancellation of a running script |
| `undo` / `redo` | yes | post Revit's own Undo/Redo and report what it reverted (gated: the stack is global) |
| `list_functions` | yes | browse the reflected API: namespaces → types → members |
| `search_functions` | yes | ranked search over member names + XML docs |
| `describe_function` | yes | full signature/params/docs for one member |
| `get_skills` | no | the built-in agent guide for using all of the above |
| `search_howtos` | no | ranked search over the how-to corpus (embedded seed + the user's local documents); needs exactly one of `instance_id` / `revit_version` |
| `describe_howto` | no | one how-to in full, with its verification for the caller's Revit version |
| `submit_howto` | no | hand in a how-to the agent just learned (saved to the user's local corpus; optionally prepared for the maintainers' review queue) |
| `update_connector` | no | check GitHub now for a newer release and report the server and each Revit version's add-in; with `apply` + `confirm_lifecycle_actions` start the installed updater (local mode only) |

> **Everything above is compiled into the broker binary** — the guide, these schemas, the
> descriptions — so a broker left running from an older build serves an older connector
> (issue #116). `get_skills` therefore returns a `build` field, repeated as a footer on the
> guide itself; the same provenance is the version at `initialize`, the first line of the
> broker's log, and `mcp-server -version`. The check to trust is `skill_sha256`: it is the
> served document's own hash, so `shasum -a 256
> revit/mcp-server/internal/mcpserver/skill.md` printing anything else means the broker is
> not your checkout. The revision is a weaker signal — the Go toolchain infers it, and
> misattributes it for a build made inside a git worktree, so only builds that stamp it
> explicitly (`install-mac.sh`, `deploy-and-verify.sh`) offer a revision comparison.

> The `build` block also carries `howto_corpus` — the shared how-to corpus compiled into this
> broker: `documents`, a 12-hex content `hash`, and `verified_on` (the Revit versions at least one
> document is verified on) — and the guide's footer repeats it in one sentence; `mcp-server -version`
> prints the same line. Design: [`howto-corpus-design.md`](howto-corpus-design.md); the corpus lives
> at `revit/mcp-server/internal/howto/corpus/`.

### `execute_script`

| Parameter | Default | Meaning |
|---|---|---|
| `instance_id` | required | target Revit instance, from `list_instances` |
| `document_id` | required | **see routing caveat below** |
| `script` | required | C# script body; `return` a value to get it back as `return_value` |
| `label` | optional | Short name for what the script does, shown as this run's entry in Revit's Undo history as `MCP: <label>`. Omitted, the connector derives one from what changed (`MCP: 12 Walls created`), falling back to `MCP Bridge Script`. |
| `timeout_ms` | 30000 | how long the call waits before returning a non-terminal `pending`/`running` status with an `execution_id` to poll |
| `max_duration_ms` | 600000 | hard runtime ceiling; on lapse the broker auto-issues cancellation |
| `overwrite_output_files` | `false` | whether `Connector.Publish` may replace an existing file in `exports/` (per-file failure otherwise) |
| `confirm_lifecycle_actions` | `false` | opt-in for the confirmation-gated members below; per-request, never cached |

> **Routing.** `document_id` genuinely routes: the script runs against the open document
> whose identity matches it — the active document or any background one — and the
> file-exchange workspace follows the routed document. Omitting it (or `""`) runs against
> the active document. An id matching no open document fails loudly with
> `document-not-found`, whose `detail.open_documents` lists every addressable document
> (id, title, active flag) so you can correct without a `list_instances` round trip. One
> asymmetry to know: `UIDocument` is only non-null in script scope when the routed
> document is the active one — Revit has no UIDocument for a background document — so a
> script addressed at a background document should use `Document`, not `UIDocument`.
> `list_instances` stays current on its own: the add-in pushes a fresh document snapshot
> on every document open/close/create/activate, so the ids it reports are live, not a
> connect-time snapshot.

Results are one of two shapes (all three execution tools share it): a terminal result —
`status` `success` / `error` / `cancelled` / `unrecoverable`, with `return_value`, `output`,
`notices[]`, `files[]`, `mutations`, and `error` as relevant — or a non-terminal `pending` / `running` / `busy` status
carrying the `execution_id` to pass to `poll_execution`. `busy` means the instance is already
running some other script (one at a time per instance, it's Revit's UI thread); the returned
`execution_id` is that script's. Before answering `busy` the broker asks the add-in, once and
without waiting, whether that script has in fact finished (#54), so a run that completed with
nobody polling never blocks the next caller; `list_instances` does the same before reporting
`busy`/`pending`.

> **`mutations`** (present only on a `success` that changed something): the run's NET effect across
> every document it changed — `net_created`, `net_modified`, `net_deleted` counts, `by_category`
> `net_created`/`net_modified` tallies keyed by Revit category name (`(uncategorized)` for elements with
> no category), and `truncated` (a per-run cap on category resolution or id
> retention was hit; totals still exact). Net means an element created and deleted in the same run
> contributes nothing, and a created-then-edited one counts once, as created. Documents the script
> settled with `keep: false` are left out. `net_modified` is noisy by nature (Revit marks dependents modified
> on regeneration). Absent on a read-only run and on every failed one — including a partial commit or a
> failure after `Settle(keep: true)`, where some writes may still have landed; `notices[]` says so.
> Use it instead of a read-after-write script.
>
> **`return_value` vs `output`.** `return_value` is what your script `return`ed; `output` is stdout
> captured while it ran. They are separate fields because `output` is not your script's alone —
> Revit writes to the process console during a run (`PlayerServer:Warning:No subscriber
> registered.`), and those lines used to appear ahead of the returned value in the same field.
> A returned **string** comes back verbatim and is **not truncated at all** — that is what makes
> `return File.ReadAllText(path);` work, and it is the one way to get a multi-megabyte
> `return_value`, so return a summary rather than a whole file unless you mean it. Anything
> structural — a collection, a dictionary, an anonymous type, a type your script declared — is
> serialized to JSON, so `return levels.Select(l => new { l.Name, l.Elevation }).ToList();` gives
> you the data rather than the collection's type name. A value the connector will not serialize and
> that has no display form of its own (a Revit `Element`, say) comes back as an explicit
> `<Autodesk.Revit.DB.Level: no display form ...>` marker naming its type: never a type name
> dressed up as data.
>
> Everything except that root string is bounded: nesting depth 6, 500 items per collection or
> members per object, 5,000 values, about 64 KB of text, 8 KB per nested string, and a 2-second
> formatting budget. Every limit announces itself in place — `<truncated: ...>`,
> `<max depth 6 reached: ...>`, `<circular reference to ...>` — so a bounded result never reads as a
> complete one. For a large result, return a projection of the fields you need, or write it to a
> file with `Connector.Publish`.

### `undo` / `redo`

Post Revit's own Undo or Redo on an instance and report what it reverted. `instance_id` and
`confirm: true` (required — see below); optional `document_id` (the document you expect to be active —
Revit's undo acts on the *active* document's stack, so a mismatch is refused before anything is posted,
`undo-wrong-active-document`); optional `timeout_ms` (default 10000, min 1000, max 30000) bounds how long
the add-in waits for the posted command to take effect. An undo **is an execution** for the busy gate: it
gets an `execution_id`, scripts answer `busy` while it runs and it answers `busy` while a script runs (an
undo posted mid-script would revert that script). The result shares execute_script's shape: `status`,
`mutations` (the net change the undo/redo made to the model) and `notices[]` naming the reverted
transaction(s) and document — `undo-reverted-connector-work` (info) when every name is one of the
connector's `MCP: …` entries, `undo-reverted-other-work` (warning) otherwise, because **Revit's undo
stack is global and not inspectable**: the tool reverts the most recent action in the session, which is a
person's if they acted after the agent's last script. That is why `confirm` is required (the refusal
names how long ago the connector last ran on that instance) and why the notice exists — if it reverted
the wrong thing, call the opposite tool at once. `undo-not-posted` when Revit refused the command (a modal
state). `undo-no-change-observed` when nothing followed the post within `timeout_ms`: the command **was
posted** and may still take effect, so inspect the model rather than retrying — a second post would revert
the next action too. For a mistake inside a script, roll back there instead: throw, or use a
`SubTransaction`.

### Discovery tools

All three take an optional `instance_id`. Omitting it works while every connected instance
shares one Revit version; otherwise the call errors with `ambiguous-instance-version` and a
candidate list rather than silently answering from an arbitrary version. Every response
carries the `revit_version` that answered.

- `list_functions(namespace?, type_name?, cursor?, page_size?)` — strict one-level tree:
  no args → namespaces; `namespace` → its types; `namespace` + `type_name` → that type's
  member names. Paginated via `next_cursor`.
- `search_functions(query, namespace?, cursor?, top_n?)` — semantic search, ranked in the broker
  (issue #107; design in [`search-ranking-redesign.md`](search-ranking-redesign.md)): a BM25F
  keyword pass and a static sentence-embedding pass over member names, namespaces and summaries are
  fused by reciprocal rank, then a cross-encoder reranks the top 20. `query` is one plain sentence
  naming the element type and the operation. `namespace` is an exact-match pre-ranking filter.
  `BuiltInCategory`/`BuiltInParameter`/`BuiltInParameterGroup`/`BuiltInFailures`/`PostableCommand`
  members are masked from search (they flooded every keyword match) but remain reachable through
  `list_functions` and `describe_function`. Core Revit API results win exact ties over other loaded
  add-ins'. Every response carries `ranker` — `semantic` (the broker index), `lexical` (a broker
  built without the bundled models), or `keyword-fallback` (the add-in's own ranker, while the
  instance's index is still building in the seconds after it connects) — and a `guidance` note;
  a fallback response also carries the reason as a §01 record in `notices[]`
  (`search-index-building` / `search-index-build-failed`). Cursor pages are served from the
  ranked list the first page produced, so paging never re-runs the reranker, and a cursor is
  bound to that ranked set (a cursor minted under the keyword fallback is rejected once the
  index answers).
  The corpus comes from the add-in over an internal `dump_members` wire method, keyed by a
  fingerprint of the loaded assembly set, so two instances of one Revit build share an index.
- `describe_function(member?, member_id?)` — one member's full signature, parameters, returns,
  and XML docs; requires at least one of `member`/`member_id`. An overloaded `member` with no
  `member_id` returns its overload list to pick from instead — `member_id` (from that list or
  from a `search_functions` result) is the reliable way to pick exactly one overload, and can be
  passed on its own.
  A property whose signature reads `T get_X(...); void set_X(..., T value)` is a *named indexed
  property* — the C++/CLI shape `Element.Parameter`, `Element.Geometry`, `FamilyInstance.Room`,
  `FootPrintRoof.SlopeAngle` and every `XxxArray.Item` have (95 of RevitAPI.dll's 104 indexed
  properties per version; only the 9 `default` ones are true indexers). C# has no `obj[...]` syntax
  for those; the accessor call is the only spelling, so that is what discovery shows. `member` may
  name either spelling (`FootPrintRoof.SlopeAngle` or `FootPrintRoof.set_SlopeAngle`); `list_functions`
  lists the property name only. A type's genuine default indexer still renders as `T this[...]`.

### `update_connector`

The agent-side counterpart of the Revit ribbon's **Update Now**. With no arguments it performs the
server's GitHub latest-release check **now** (the same code path as the 6-hourly background check, so
the two cannot disagree), records the result in `broker.json` — which the add-in's Status window
re-reads on every click — and returns the picture: `latest`; `server.running` (this process),
`server.installed` (the installer's version marker; differs from running after a staged swap until the
MCP client reconnects) and `server.update_available`; and `revit[]`, one entry per Revit version the
installer tracks (`state`: `deployed` / `deferred` while that Revit was running / `skipped` when the
release shipped no payload for it; `unknown` for a connected version the marker does not list) with
`addin_installed`, `connected_instances` (from the registry) and `update_available`. Multiple Revit
versions are first-class: an update asks every running one to close. Notices: `server-restart-pending`
when the new release is installed but this process is still the old one — it steps aside on its own
within about a minute (a running server re-reads the installer's version marker every 30 s and exits
once the release on disk differs *and* its own executable has been replaced, waiting first for any
script that is pending or running on a connected Revit; a process that wins the singleton lock while
stale exits instead of serving), after which the client's next call starts the installed release. Fail-fast: a secondary
that cannot reach or authenticate with the primary three times in a row exits with
`primary-unresponsive` naming the pid, instead of retrying behind an opaque client timeout.

With `apply: true` **and** `confirm_lifecycle_actions: true` it also starts the installed updater
(`install.ps1 -Update -Silent -Scope <User|AllUsers>`, detached) when anything is behind: every running
Revit is asked to close (Revit prompts to save unsaved work; a Revit kept open is updated when it is
next closed), nothing is relaunched, and this server keeps serving the old version until the client
reconnects (`update-started` notice says all of this; `already-current` when there is nothing to do).
Refusals: `update-requires-confirmation` (apply without the confirmation — the same gating shape as
`execute_script`'s lifecycle actions; ask the user first), `update-not-available-in-remote-mode` (the
installer lives on the Revit machine, not where a remote-mode server runs; the check still works),
`installer-not-found` (no `install.ps1` self-copy beside the server — run the one-liner once),
`installer-launch-failed`. `update-check-failed` (GitHub unreachable or rate-limiting) still returns
what the marker and registry know.

### `search_howtos` / `describe_howto`

The read side of the how-to corpus (design: [`howto-corpus-design.md`](howto-corpus-design.md) §3-§5).
The corpus served is the seed embedded in the broker (`revit/mcp-server/internal/howto/corpus/`,
reviewed and harness-verified, `source: "seed"`) overlaid with the user's own documents under
`<app-data>\howto\local\` (`source: "local"`, unreviewed, re-scanned on every call by a
name/size/mtime signature). A local file with a seed's `id` and an identical script is reported
superseded (`howto-local-superseded-by-shared`); one with a different script is served instead of the
seed with `shared_rev` alongside (and `howto-local-shadows-shared` when the seed has moved past it).
Neither tool needs a live Revit, but **both require exactly one of `instance_id` or `revit_version`**:
verification is per Revit version, and the agent must always be told whether the script it is about to
run was verified on the version it is driving. Neither, or both, is refused with
`howto-version-required`; a malformed `revit_version` with `howto-version-invalid`. The version is a
*preference*, never a filter.

- `search_howtos(query, instance_id | revit_version, cursor?, top_n?)` — the same pipeline as
  `search_functions` (BM25F + static embeddings fused by reciprocal rank, cross-encoder rerank of the
  top 20) over a how-to field set: `title`, `task` (highest weight), the recorded hit `queries`,
  `pitfalls` text, `members` as `Type.Member`, and `tags`. Within the head of the ranked list (the
  reranked pool of 20, or the top 20 in a lexical-only build) documents verified on the resolved
  version are moved ahead of the rest in their ranked order; beyond it the ranked order stands, so a strong but unverified match cannot be pushed past the first pages. Each hit
  carries `id`, `rev`, `title`, `task`, `members`, `tags`, `verified_on[]`, `failed_on[]`,
  `verified_here`, `score`, `source` and, for a shadowing local document, `shared_rev`. The response
  carries `revit_version` (what the call resolved to), `ranker` (`semantic` / `semantic-no-rerank` /
  `lexical` — there is no add-in fallback), `guidance`, `total_matched`, `next_cursor` (bound to the
  query, version and corpus fingerprint, like `search_functions`'), and `notices[]` for anything the
  loader skipped or flagged (`howto-local-corpus-problems`, `howto-local-corpus-truncated`,
  `howto-corpus-newer-than-broker`) plus the overlay notices above for hits on the page. `top_n`
  defaults to 5 (a hit is a paragraph), max 50.
- `describe_howto(id, instance_id | revit_version)` — the document as the agent acts on it: `id`,
  `rev`, `kind`, `title`, `task`, `members`, `script`, `pitfalls`, `tags`, `api_since`/`api_until`,
  `absorbs`, `updated_at`; never `provenance`, `verify` or `contributors` (maintainer-facing). With it:
  `source`, `verified_here`, `verification` (the winning stamp for the resolved version — `status`,
  `by` harness/session, `at`, `connector_version`, `diagnostic` — or absent when never swept on it),
  `verified_on[]` / `failed_on[]`, `api_warnings[]` (declared hints evaluated against the version),
  `shared_rev`, and `guidance` that says plainly whether to run it as-is, read it first (local or
  session-verified), or expect to adapt it (failed / unverified here). An id merged into another
  lineage resolves to the survivor with `redirected_from` and a `howto-redirected` notice; an unknown
  id is `howto-not-found`.

### `submit_howto`

The growth mechanism of the how-to corpus (design: [`howto-corpus-design.md`](howto-corpus-design.md),
plan: [`howto-seed-plan.md`](howto-seed-plan.md) §4). The agent hands in `title`, `task`, `script`,
`members`, `pitfalls[]`, optional `queries`/`tags`, optional `credit_as`; to improve an existing
how-to it passes that document's `id` with only the changed fields and a `change_note`, and the
result is the next revision (the embedded seed and the user's own local documents can both be improved this way). The document is validated against the corpus schema first — a
non-compliant submission is refused with `howto-invalid` naming every field to fix, and nothing is
written — then saved to `<app-data>\howto\local\<id>.json`, with a `session` verification stamp if
that exact script ran successfully in this session. Without `confirm_submission: true` that is all;
with it, the document is scrubbed (paths, UNC, emails, addresses, the machine and user names, open
document titles — every text field including the script's comments and literals; residue refuses
the submission with `howto-submission-unscrubbed`) and an issue body is written to
`<app-data>\howto\outbox\<id>.md`. If the user set `REVIT_MCP_GITHUB_TOKEN` (their own token, opt-in),
the broker files the issue over GitHub's REST API and returns its URL; otherwise the response carries
the issue fields (`repo`, `title`, `body`, `labels`) for the agent to create with its own GitHub tool
(the GitHub connector, or the `gh` CLI — the equivalent command is included), plus a prefilled issue
URL (which applies the queue label through the Issue Form template) for filing by hand. The broker
never carries a maintainer credential; while the repository is private only collaborators can file,
and the outbox file is the hand-off for anyone else.

## Script globals

Inside `script`, these identifiers are in scope (only `System` is imported by default —
fully-qualify Revit types or add `using` directives):

| Global | Type / behavior |
|---|---|
| `Document` | the real `Autodesk.Revit.DB.Document` (the active document — see routing caveat) |
| `UIApplication` | `Autodesk.Revit.UI.UIApplication` (`UIApplication.Application` reaches the top-level `Application`) |
| `UIDocument` | `Autodesk.Revit.UI.UIDocument`, may be null |
| `CancellationToken` | check it in loops; `cancel_execution` signals it |
| `Connector` | the connector's own API, `Eichler.Connectors.Revit.Connector` — see below |

### The `Connector` global

The four globals above are Revit's own objects. `Connector` is everything this connector adds on top,
and it is **not part of the Revit API** — `Connector.Publish(path)`, `Connector.WithTransaction(doc, body)`,
and so on.

Its members are deliberately **not enumerated here**, and that is the point of issue #91: they are
indexed as an add-in API under the `Eichler.Connectors.Revit` namespace, so `list_functions`,
`search_functions` and `describe_function` return them with live signatures and summaries, generated
from the XML doc comments beside the code. Five hand-maintained copies of this list existed before, and
three of them were wrong. The file-exchange workspace (PRD §09) that `Connector` exposes the
imports and exports directories of also carries a per-run audit trail beside them — `scripts/`
(verbatim script per run) and `logs/` (per-run NDJSON diagnostics), aged out after 14 days.

**Transactions are never yours to open — blocks are.** The connector opens a `TransactionGroup` per
document for the run and no transaction: a document is readable but not modifiable until the script
writes inside `Connector.WithTransaction(doc, () => { ... })`, whose transaction the connector opens (with
failure capture) and commits at block end. A run that returns normally keeps what its blocks committed
as **one undo entry**; a run that throws rolls the whole group back; a run that only read leaves no undo
entry. Constructing `Transaction`/`TransactionGroup` is rejected at compile time, unconditionally
(`script-api-denied`). A native `SubTransaction` held in a `using` is permitted as a savepoint inside a
block; outside a `using` it is rejected the same way, and started with no block open it fails with
`script-subtransaction-needs-transaction`. (#146 Phase 3; before it a transaction was open for the whole
run, and `Connector.WithoutTransaction`/`OpenForWriting` existed to work around that.)

**Confirmation-gated members** (`confirm_lifecycle_actions: true` required): `Document.Close`
/ `.Dispose` / `.Save` / `.SaveAs` / `.SaveAsCloudModel` / `.SynchronizeWithCentral` /
`.Print` / `.PrintToFile`; `PrintManager.SubmitPrint`; `UIDocument.SaveAndClose`;
`UIApplication.PostCommand`; `WorksharingUtils.RelinquishOwnership`. The membership test:
these escape the transaction's rollback boundary (a person's session, the filesystem, a
shared central model, a printer, another user's checkout) — nothing else is gated.

## Error & notice shapes

Every failure and every auto-resolved event uses one diagnostic-record shape (PRD §01):
`{severity, code, source, message, detail, remedy}`. Failed tool calls come back as normal
MCP results flagged `IsError`, with this record in the `error` field — match on `code`,
not message text:

| `code` | Meaning / next step |
|---|---|
| `script-execution-failed` | the script itself threw or didn't compile; `message` carries the real compiler/API error |
| `script-api-denied` | flat rejection (own-transaction construction); change the script, no flag lifts it |
| `script-lifecycle-confirmation-required` | gated member used without the flag; resend identical call with `confirm_lifecycle_actions: true` if intended |
| `script-await-not-allowed` | top-level `await` isn't supported in scripts |
| `unknown-execution-id` | the execution predates a Revit/add-in restart; re-run |
| `ambiguous-instance-version` | discovery call with instances of multiple Revit versions connected; pass an `instance_id` from `detail.candidates` |
| `missing-required-param` | a required param was absent or empty; `detail.param` (or `detail.params`, where either of two satisfies the rule) names which |
| `invalid-param-type` | the param was present but the wrong JSON type; `detail.param` and `detail.expected_type` name which and what |
| `invalid-cursor` | `cursor` was unparseable, or was issued for a different query; re-issue with the original params, or drop `cursor` to start over |
| `invalid-execution-id` | `execution_id` is whitespace-only, or collides with a different, already-finished execution; `message` says which. Mint a fresh unique id per `execute_script` |
| `unknown-method` | the add-in doesn't route that method; `detail.supported_methods` lists what it does |
| `execution-record-vanished` | the execution's record aged out of the ring buffer mid-cancellation (a connector-side race, not a bad request); retry |
| `dispatch-failed` | the add-in threw where it should have returned an error — a connector-side bug; retry once, then report with `connection.log` |

`notices[]` on a **successful** result reports what was auto-resolved (PRD's
observability-over-silence principle): auto-answered dialogs, auto-dismissed transaction
warnings, and — for multi-document scripts — the `script-partial-commit` notice naming
which documents committed, rolled back, or ended in an unknown state if a late commit
failed.
