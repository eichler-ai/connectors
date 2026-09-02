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
| `list_instances` | no | connected Revit instances + their document snapshots and process memory |
| `execute_script` | yes | compile & run C# in a Revit instance |
| `poll_execution` | yes | wait on / re-check a long-running execution |
| `cancel_execution` | yes | cooperative cancellation of a running script |
| `list_functions` | yes | browse the reflected API: namespaces → types → members |
| `search_functions` | yes | ranked search over member names + XML docs |
| `describe_function` | yes | full signature/params/docs for one member |
| `get_skills` | no | the built-in agent guide for using all of the above |

> **Everything above is compiled into the broker binary** — the guide, these schemas, the
> descriptions — so a broker left running from an older build serves an older connector
> (issue #116). `get_skills` therefore returns a `build` field, repeated as a footer on the
> guide itself; the same provenance is the version at `initialize`, the first line of the
> broker's log, and `mcp-server -version`. The check to trust is `skill_sha256`: it is the
> served document's own hash, so `shasum -a 256
> revit/mcp-server/internal/mcpserver/skill.md` printing anything else means the broker is
> not your checkout. The revision is a weaker signal — the Go toolchain infers it, and
> misattributes it for a build made inside a git worktree, so only builds that stamp it
> explicitly (`install-mac.sh`, `redeploy-and-verify.sh`) offer a revision comparison.

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
`execution_id` is that script's.

> **`mutations`** (present only on a `success` that changed something): the run's NET effect across
> every document it changed — `created`, `modified`, `deleted` counts, `by_category` created/modified
> tallies keyed by Revit category name, and `truncated` (a per-run cap on category resolution or id
> retention was hit; totals still exact). Net means an element created and deleted in the same run
> contributes nothing, and a created-then-edited one counts once, as created. Documents the script
> settled with `keep: false` are left out. `modified` is noisy by nature (Revit marks dependents modified
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
and it is **not part of the Revit API** — `Connector.Publish(path)`, `Connector.OpenForWriting(doc)`,
and so on.

Its members are deliberately **not enumerated here**, and that is the point of issue #91: they are
indexed as an add-in API under the `Eichler.Connectors.Revit` namespace, so `list_functions`,
`search_functions` and `describe_function` return them with live signatures and summaries, generated
from the XML doc comments beside the code. Five hand-maintained copies of this list existed before, and
three of them were wrong. The file-exchange workspace (PRD §09) that `Connector` exposes the
imports and exports directories of also carries a per-run audit trail beside them — `scripts/`
(verbatim script per run) and `logs/` (per-run NDJSON diagnostics), aged out after 14 days.

**Transactions are never yours to open.** Every script runs inside a connector-managed
`Transaction`/`TransactionGroup`: changes commit when the script returns and roll back if it
throws. Constructing `Transaction`/`TransactionGroup` is rejected at compile time,
unconditionally (`script-api-denied`). A native `SubTransaction` held in a `using` is permitted as
a savepoint inside the connector's transaction (#146 Phase 1); constructed outside a `using` it is
rejected the same way, and started with no transaction open it fails with
`script-subtransaction-needs-transaction`.

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
