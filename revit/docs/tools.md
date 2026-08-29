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
| `list_instances` | no | connected Revit instances + their document snapshots |
| `execute_script` | yes | compile & run C# in a Revit instance |
| `poll_execution` | yes | wait on / re-check a long-running execution |
| `cancel_execution` | yes | cooperative cancellation of a running script |
| `list_functions` | yes | browse the reflected API: namespaces → types → members |
| `search_functions` | yes | ranked search over member names + XML docs |
| `describe_function` | yes | full signature/params/docs for one member |
| `get_skills` | no | the built-in agent guide for using all of the above |

### `execute_script`

| Parameter | Default | Meaning |
|---|---|---|
| `instance_id` | required | target Revit instance, from `list_instances` |
| `document_id` | required | **see routing caveat below** |
| `script` | required | C# script body; `return` a value to get it back as `output` |
| `timeout_ms` | 30000 | how long the call waits before returning a non-terminal `pending`/`running` status with an `execution_id` to poll |
| `max_duration_ms` | 600000 | hard runtime ceiling; on lapse the broker auto-issues cancellation |
| `overwrite_output_files` | `false` | whether `Publish` may replace an existing file in `exports/` (per-file failure otherwise) |
| `confirm_lifecycle_actions` | `false` | opt-in for the confirmation-gated members below; per-request, never cached |

> **Routing caveat — current implementation.** `document_id` is accepted but currently
> **ignored**: scripts always run against the instance's **active document**, and the
> file-exchange workspace is likewise derived from the active document, not the requested
> one — per-document routing is not implemented yet and is in progress (see the PRD §14
> note on `RequestDispatcher`). Until it lands, make sure the document you intend to touch
> is the active one, and treat `list_instances`' `active` flag as the thing that decides
> what a script sees.

Results are one of two shapes (all three execution tools share it): a terminal result —
`status` `success` / `error` / `cancelled` / `unrecoverable`, with `output`, `notices[]`,
`files[]`, and `error` as relevant — or a non-terminal `pending` / `running` / `busy` status
carrying the `execution_id` to pass to `poll_execution`. `busy` means the instance is already
running some other script (one at a time per instance, it's Revit's UI thread); the returned
`execution_id` is that script's.

### Discovery tools

All three take an optional `instance_id`. Omitting it works while every connected instance
shares one Revit version; otherwise the call errors with `ambiguous_instance_version` and a
candidate list rather than silently answering from an arbitrary version. Every response
carries the `revit_version` that answered.

- `list_functions(namespace?, type_name?, cursor?, page_size?)` — strict one-level tree:
  no args → namespaces; `namespace` → its types; `namespace` + `type_name` → that type's
  member names. Paginated via `next_cursor`.
- `search_functions(query, namespace?, cursor?, top_n?)` — ranked (exact `Type.Member`
  match, then name+declaring-type token match, then FTS5 BM25 over names+summaries); core
  Revit API results rank ahead of other loaded add-ins' at equal relevance.
- `describe_function(member, overload_index? | member_id?)` — one member's full signature,
  parameters, returns, and XML docs; an overloaded member returns its overload list to pick
  from.

## Script globals

Inside `script`, these identifiers are in scope (only `System` is imported by default —
fully-qualify Revit types or add `using` directives):

| Global | Type / behavior |
|---|---|
| `Document` | the real `Autodesk.Revit.DB.Document` (the active document — see routing caveat) |
| `UIApplication` | `Autodesk.Revit.UI.UIApplication` (`UIApplication.Application` reaches the top-level `Application`) |
| `UIDocument` | `Autodesk.Revit.UI.UIDocument`, may be null |
| `CancellationToken` | check it in loops; `cancel_execution` signals it |
| `ImportsDirectory` / `ExportsDirectory` | absolute paths of this document's file-exchange workspace (PRD §09) |
| `Publish(path, name?)` | copy a file into `exports/` and report it in the result's `files[]`, per-file status |
| `DialogResultOverrides` | per-dialog auto-answer override: `DialogResultOverrides["TaskDialog_X"] = 1001;` |
| `CreateProjectDocument(templatePath?)` | create a new, **writable** project document (connector manages its transaction) |
| `CreateFamilyDocument(templatePath)` | family-document counterpart; template path required |
| `OpenForWriting(document)` | open a managed transaction on an existing document found via `Application.Documents` — e.g. one a *prior* call created |

**Transactions are never yours to open.** Every script runs inside a connector-managed
`Transaction`/`TransactionGroup`: changes commit when the script returns and roll back if it
throws. Constructing `Transaction`/`TransactionGroup`/`SubTransaction` is rejected at
compile time, unconditionally (`script-api-denied`).

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
| `unknown_execution_id` | the execution predates a Revit/add-in restart; re-run |
| `ambiguous_instance_version` | discovery call with instances of multiple Revit versions connected; pass an `instance_id` from `detail.candidates` |

`notices[]` on a **successful** result reports what was auto-resolved (PRD's
observability-over-silence principle): auto-answered dialogs, auto-dismissed transaction
warnings, and — for multi-document scripts — the `script-partial-commit` notice naming
which documents committed, rolled back, or ended in an unknown state if a late commit
failed.
