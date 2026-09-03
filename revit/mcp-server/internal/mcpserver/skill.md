# Working with Revit through this connector

You drive one or more **live Revit sessions**: you run C# with `execute_script`, and three discovery
tools let you read Revit's own API documentation on demand. Read this once at the start of a Revit
task; it is orientation, not reference.

**Two facts to carry into every script.**

1. **The globals are the real Revit types.** `Document` **is** `Autodesk.Revit.DB.Document`, not a
   wrapper; `UIApplication` and `UIDocument` are the real `Autodesk.Revit.UI` types (`UIDocument` may
   be null). Pass them straight into any Revit API.
2. **Writes go inside `Connector.WithTransaction(doc, () => { ... })`; never open a Revit transaction
   yourself.** Documents are read-only outside a block. The connector commits each block when it ends,
   and **undoes the whole run as one unit if the script throws**.

```csharp
var walls = new FilteredElementCollector(Document)
    .OfClass(typeof(Wall)).GetElementCount();          // reads need nothing
var level = Connector.WithTransaction(Document, () =>  // writes go in a block
    Level.Create(Document, 42.0));
return new { walls, levelId = level.Id.Value };
```

---

## How the pieces fit

```
  you  ──stdio/MCP──▶  MCP Server (broker)  ──TCP/JSON──▶  MCP Bridge (add-in) ──▶ Revit
                        one process,                        one per running
                        routes by instance                  Revit session
```

- **Scripts run on Revit's UI thread, one at a time per instance.** A long script blocks that
  instance, which is why `execute_script` can hand back `pending`/`running` instead of a result.
- **The broker knows what's connected; only the add-in can touch a document.** `list_instances` and
  `get_skills` answer instantly; anything script-shaped needs a live, idle Revit.
- **The add-in dials out and retries on its own.** Start order doesn't matter and a broker restart
  heals itself. If something isn't connected, **wait a few seconds and re-check** rather than reporting
  a failure. `documents[]` is live — a just-opened document appears within moments.

## Addressing: instances and versions

Every script call targets `{instance_id, document_id}`, both from `list_instances`:

```json
{"instances": [{
  "instance_id": "eb81f92b-...", "revit_version": "2027", "pid": 10652, "status": "idle",
  "memory": {"private_mb": 4096, "working_set_mb": 1800, "managed_mb": 520},
  "documents": [{"document_id": "doc-B2C2...", "title": "Tower", "workshared": false, "active": true}]
}]}
```

- `instance_id` is stable for that Revit **process**; it changes when Revit restarts.
- `document_id` is `doc-<hash>` for a saved file (derived from its path, stable across reopen) or
  `tmp-<guid>` for an unsaved one (session-only; don't persist it).
- `status` is `idle` / `pending` / `busy` / `unresponsive` / `unrecoverable`. Only `idle` starts work
  immediately. `unrecoverable` means that instance needs Revit restarted — nothing you send will run.
- `memory` (MB, per heartbeat) is the Revit process's own use. `private_mb` only climbs — Revit never
  releases document memory until exit — so restart Revit once it reaches multiple GB.

**Several Revit versions can be connected at once**, and 2025 and 2027 have genuinely different API
surfaces. Scripts are always explicitly targeted, so they're unaffected; discovery is not (see below).

## Running a script

`execute_script` takes `instance_id`, `document_id` and `script`; its schema lists the optional
timeout, duration, overwrite, label and confirmation parameters. `document_id` routes: the script runs
against that document — active or background — and its workspace follows it. An unknown id fails with
`document-not-found` and an `open_documents` list; omitted means the active document. `UIDocument` is
null unless the routed document is the active one; use `Document` for a background one.

**What comes back.** `return` a value and it arrives as `return_value` — strings verbatim, collections
and anonymous types as JSON, anything else as a self-explaining `<...>` marker. `output` is stdout
(Revit's own writes too). `notices[]` carries everything the run wants to tell you besides the return
value — dialogs auto-answered, documents created, a partial commit, a `Settle` (see "Reading errors")
— and appears on failed runs too. `files[]` is what you published (see "Exchanging files"). A
successful run that changed anything carries **`mutations`**: `net_created`/`net_modified`/
`net_deleted` across every document it touched, plus `by_category` (elements with no Revit category
tally under `(uncategorized)`). **Net, not activity**
— what is different in the model now, exactly what one Undo of this run would revert: an element
created then deleted contributes nothing, created then edited counts once as created. Revit itself
reports each committed block's net effect, so no gross count exists. Skip the read-after-write check.

```csharp
return Document.Title;
```
```json
{"status":"success","execution_id":"exec-4927...","return_value":"MCPBridgeTest"}
```

**Long scripts.** Past `timeout_ms` you get `{"status":"running","execution_id":...}`; call
`poll_execution` with that id until a terminal status. `cancel_execution` requests a stop, but
cancellation is *cooperative*: check the token in any loop, or the script won't stop and the instance
ends up `unrecoverable`.

```csharp
foreach (var x in items) {
    CancellationToken.ThrowIfCancellationRequested();
    // ...
}
```

### What's in scope

| Global | Type / use |
|---|---|
| `Document` | `Autodesk.Revit.DB.Document` — the real one, full API |
| `UIDocument` | `Autodesk.Revit.UI.UIDocument`; may be null |
| `UIApplication` | `Autodesk.Revit.UI.UIApplication`; `UIApplication.Application` reaches `Autodesk.Revit.ApplicationServices.Application` |
| `CancellationToken` | check it in loops (above) |
| `Connector` | **this connector's own functions, not Revit's** — `Connector.Publish(path)` and the rest, indexed under `Eichler.Connectors.Revit` (see "Discovering the API") |
| the .NET BCL | `System.IO`, `System.Linq`, etc. — fully usable |

Only `System` is imported, so use fully-qualified names (`Autodesk.Revit.DB.Wall`,
`System.IO.File.ReadAllText`) or you get `CS0246`; `using Autodesk.Revit.DB;` at the top works too.

## Writing to a document

`Connector.WithTransaction(doc, () => { ... })` is **the** way to write. Outside a block every document
is readable and not modifiable; inside it, write directly. It returns the body's value:
`var id = Connector.WithTransaction(doc, () => Level.Create(doc, 3.0).Id);`. Use one block per batch
of changes, not one per element — each block is a Revit commit and a regeneration. A block that throws
rolls back only its own slice and the document stays usable, so `try { WithTransaction } catch` is
catch-and-continue; a script that throws to the top undoes **every** block in the run. Nesting on the
*same* document is refused; blocks on different documents nest freely.

**Calls that need their target *not* modifiable go between blocks**, where they simply work:
`Document.LoadFamily` (its *target*), `UIDocument.RequestViewChange`/`ActiveView`,
`UIApplication.OpenAndActivateDocument`, `Document.Export`, and every `EditScope`. Inside a block they
fail with `code` `script-target-must-not-be-modifiable`. Stairs are the canonical shape — the scope
starts and commits *between* blocks, the run is built *inside* one:

```csharp
var scope = new Autodesk.Revit.DB.StairsEditScope(doc, "stairs");
var id = scope.Start(baseLevel.Id, topLevel.Id);
Connector.WithTransaction(doc, () => {
    Autodesk.Revit.DB.Architecture.StairsRun.CreateStraightRun(
        doc, id, line, Autodesk.Revit.DB.Architecture.StairsRunJustification.Center);
});
scope.Commit(yourFailuresPreprocessor);
```

**One run, one Undo entry.** A run that only reads leaves none; a run that wrote leaves exactly one,
named by the call's `label` ("create L1 walls") or else by what changed — the person's backstop, one
Ctrl+Z. The `undo`/`redo` tools (`confirm: true`) post Revit's own Undo/Redo and report what they
reverted. The stack is **global** and may hold a person's action on top of yours: pass `document_id`
as the document you expect to be active (a mismatch refuses the call rather than undoing the wrong
stack), read the notice naming the reverted transaction (`MCP: …` is one of your runs; anything else
is a person's), and never retry a timed-out undo blindly. Fix your own mistakes *inside* a script by
throwing instead.

**`new Transaction(...)` and `new TransactionGroup(...)` are rejected** before the script runs
(`code` `script-api-denied`, no flag lifts it): the connector's own transaction is what captures
warnings into `notices[]` and rolls the run back, and yours would bypass both. A `SubTransaction` is
allowed as a savepoint inside a block, but only as `using (var st = new
Autodesk.Revit.DB.SubTransaction(doc)) { st.Start(); … st.Commit(); }` — one left open when the block
ended has crashed Revit. Outside a block, `SubTransaction.Start()` fails with
`script-subtransaction-needs-transaction`.

## Documents

**Creating documents — use `Connector.CreateProjectDocument` / `Connector.CreateFamilyDocument`.**
The document is tracked for the run (its writes roll back with everything else) and written to like any
other. It is **headless** — no window, never the active document — and gets a session-only `tmp-<guid>`
`document_id` that appears in `list_instances`, so a later call can route straight at it; a
`WithTransaction` block on any other open document adopts it the same way. Template paths:
`Application.DefaultProjectTemplate` (a full `.rte` path) and `Application.FamilyTemplatePath` (a tree —
search it recursively). Raw `Application.NewProjectDocument` works too, but nothing tracks it.

```csharp
var doc = Connector.CreateProjectDocument();
Connector.WithTransaction(doc, () => { Autodesk.Revit.DB.Level.Create(doc, 10.0); });
return doc.Title;   // a follow-up call finds it by this
```

**`Connector.Settle(doc, keep:)` ends the run's hold on a document early** — required before `Close`,
`Save`, `SaveAs` or `SynchronizeWithCentral` in the same run, all gated on
`confirm_lifecycle_actions: true`. `keep: true` makes its writes permanent now (a later throw no longer
undoes them); `keep: false` discards them. To make a created document visible: `Settle(doc, true)`,
`SaveAs`, then `OpenAndActivateDocument` from a later call.

**Close your scratch documents — nothing else will**, and a pile of them exhausts the memory of the
Revit a person is working in. Same run: `Settle(doc, false)` then `doc.Close(false)`. Later run, by the
title the creating run returned:

```csharp
var wanted = "Project7";
Autodesk.Revit.DB.Document scratch = null;
foreach (Autodesk.Revit.DB.Document d in UIApplication.Application.Documents)
    if (d.PathName == "" && d.Title == wanted) { scratch = d; break; }
if (scratch == null) return "already gone";
scratch.Close(false);            // discard; never prompts
```

`PathName == ""` matters: Revit names unsaved documents `Project1`, `Project2`, …, so a title alone
can match a person's own unsaved work or a saved `Project1.rvt`. The active document cannot be closed;
activate another first.

**A thrown script leaves created documents open** (writes roll back, documents don't disappear); the
`script-created-documents` notice on every run names them by title and `tmp-` id. With several
documents, commits land in order — created, adopted, active last — and a `script-partial-commit`
notice reports any that kept their changes after a failure; an adopted document may be a real, saved
model.

## What needs your confirmation

The document-lifecycle and worksharing calls are allowed **only with `confirm_lifecycle_actions: true`**:

| Gated | What it escapes to |
|---|---|
| `Document.Close`, `.Dispose` | the session a person has open in front of them |
| `Document.Save`, `.SaveAs` | the filesystem |
| `Document.SynchronizeWithCentral` | the shared central model your teammates see |
| `Document.SaveAsCloudModel` | a cloud project other people can open |
| `Document.Print`, `.PrintToFile`, `PrintManager.SubmitPrint` | a physical device |
| `UIDocument.SaveAndClose` | the filesystem, then that person's session |
| `UIApplication.PostCommand` | anything, after your run has already ended |
| `WorksharingUtils.RelinquishOwnership` | another user's ability to edit |
| `Connector.Settle` | this run's own rollback guarantee for that document |

**Why these and nothing else:** everything else you change is covered by the group wrapped around your
run, so a thrown exception undoes it. These act outside the document's own content, and no exception
takes them back. That one question — "would a thrown exception actually undo this?" — is the whole
rule; the list is about damage nothing can undo, not sandboxing.

Call one without confirming and the run is refused before it starts: `code`
`script-lifecycle-confirmation-required` (match on the code, not the text), `message` naming every
gated member you used, `remedy` spelling out the resend. To go ahead, resend the **same** call with the
flag:

```json
{"instance_id":"...","document_id":"...","script":"Document.Save(); return \"saved\";",
 "confirm_lifecycle_actions": true}
```

It applies to that one call only — confirming once does not confirm the next, even for identical
script text. Treat the refusal as a real question: if saving/closing/syncing is what was asked for,
confirm and proceed; if it crept into a script written for another purpose, remove it.

## Reading errors

Every failure uses one shape. Read `message` for what happened, `remedy` for what to do:

```json
{"severity":"error","code":"script-compilation-failed","source":"mcp-bridge.core.execution",
 "message":"(1,8): error CS0103: The name 'doc' does not exist in the current context",
 "detail":{"execution_id":"exec-33a9..."},
 "remedy":["..."]}
```

- **`message` names the real condition** — a compiler error, an API exception. Read it literally;
  compiler errors mean fix the script, not retry it.
- **`code` is what you match on.** A compile error is `script-compilation-failed` (fix the script;
  the remedy names the globals). Otherwise most failures are the generic `script-execution-failed`; a
  refusal carries its own — `script-api-denied` (change the script),
  `script-lifecycle-confirmation-required` (resend with the flag), `script-write-outside-transaction`
  (wrap the write in a block), `script-target-must-not-be-modifiable` (move the call between blocks).
- **`remedy` is actionable.** Follow it before retrying.
- **`notices[]` on a *successful* result** means something was auto-resolved. Dialogs your script
  triggers are auto-answered with the safe option and reported here (override per dialog with
  `Connector.DialogResultOverrides`); it worked — check what was papered over. A dialog already on
  screen when your script arrives is different: it blocks Revit's UI thread and needs a human.
- `status: "cancelled"` is not an error; you asked for it.

## Exchanging files

Each document gets a workspace with `exports/` and `imports/`. Both directions go through the
filesystem, not MCP, so size is not a constraint. **The location is not fixed and is not restated
here — never hard-code it.** To find it, read `Connector.ExportsDirectory` / `Connector.ImportsDirectory`
(or `return` one to see the absolute path); the workspace root has changed before. Beside them, per-run audit files: `scripts/` (verbatim
script) and `logs/` (NDJSON diagnostics), swept after 14 days.

**Revit → you.** Write the file, then `Connector.Publish` it. Published files come back in `files[]`,
each with its own `status`; publishing onto an existing name **fails that file** unless you pass
`overwrite_output_files: true`. Paths are Windows-native; in remote mode map them through the shared
folder yourself.

```csharp
var p = System.IO.Path.Combine(Connector.ExportsDirectory, "rooms.csv");
System.IO.File.WriteAllText(p, "Name,Area\n");
Connector.Publish(p);
return "ok";
```

**You → Revit.** Put the file in `imports/` and read it with ordinary `System.IO`:

```csharp
return System.IO.File.ReadAllText(System.IO.Path.Combine(Connector.ImportsDirectory, "rooms.csv"));
```

Don't know the path yet? `return Connector.ImportsDirectory;` first, then write your file there.

## Discovering the API

Reflect over Revit's real installed assemblies rather than guessing names. Everything you find is
directly callable against the `Document` global — look up exact signatures and overloads before
writing one; cheaper than a round trip through a `CS1503`. This covers **this connector's own
functions too**, indexed like any add-in's and ranked below Revit's: anything under
`Eichler.Connectors.Revit` is reached through the `Connector` global, so
`Eichler.Connectors.Revit.Connector.Publish` is written `Connector.Publish(path)`.

- **`search_howtos`** — try this first for a task (`"create a floor from a closed loop on a
  level"`): each hit is a complete, harness-verified script for one Revit feature or connector
  mechanism, with the pitfalls it avoids. Pass `instance_id` (or `revit_version` when nothing is
  connected yet); hits verified on that version lead, and each says `verified_here`. Then
  **`describe_howto`** with the `id` for the script and pitfalls — read the pitfalls, run the script,
  adapt. A hit with `source: local` is the user's own, unreviewed: read its script in full first.
- **`search_functions`** — when no how-to covers it and you know *what* you want, not the name. Ranking fuses a
  sentence-embedding pass with a keyword pass, then a cross-encoder reranks, so write `query` as **one
  plain sentence naming the element type and the operation** (`"move an element to a new location"`,
  not `"move"`); a suspected type or member name in it also scores; `namespace` filters before ranking.
  A weak or empty result does **not** mean the API is absent — rephrase, or browse with
  `list_functions`. Each response says which `ranker` answered (`keyword-fallback` while a
  just-connected instance's index builds) plus `guidance`.
- **`submit_howto`** — after a task that needed a reworded query or got past a pitfall, once the
  script ran: `title`, one-sentence `task`, `script`, `members`, `pitfalls` (symptom → cause → fix).
  Saves to the user's local how-to corpus; `confirm_submission: true` scrubs it into a review-queue
  issue, filed by the connector if the user set a GitHub token, else by you from the returned `issue`
  fields with your GitHub tool (ask first). If a how-to you followed was wrong or missed a pitfall,
  submit the fix as `id` + `change_note`: that is how the next agent avoids what you just hit.
- **`list_functions`** — drill down. Omit `namespace` for the namespace list; pass `namespace` for its
  types; pass `namespace` + `type_name` for that type's members.
- **`describe_function`** — full signature, parameters and docs for one member
  (`{"member": "Autodesk.Revit.DB.Wall.Create"}`). Overloaded? You get an overload list back —
  re-call with just its `member_id` (or one from `search_functions`).

`list_functions` and `search_functions` paginate: pass the `cursor` from the previous response.

**Discovery needs a connected Revit and is version-specific.** With instances of different Revit
versions connected, omitting `instance_id` fails rather than guessing:

```json
{"code":"ambiguous-instance-version",
 "detail":{"candidates":[{"instance_id":"5faf...","revit_version":"2025"},
                         {"instance_id":"eb81...","revit_version":"2027"}]}}
```

Pick one from `candidates` and pass its `instance_id`. Every discovery response reports the
`revit_version` it reflected.

## When something isn't working

**`list_instances` reports successful connections only** — a Revit that never connected is simply
absent. For the why, a human can click **MCP Bridge → Status** on the Revit ribbon.

| Symptom | Most likely cause | What to do |
|---|---|---|
| `instances[]` empty | Revit not running, the add-in didn't load, or Revit is registered with a *different* broker (its Status shows `MCP Server: REMOTE` while yours is local, or vice versa) | Wait a few seconds and re-check (the add-in retries on a backoff). If it stays empty, ask the user to confirm Revit is open and check **MCP Bridge → Status**; the **Reconnect** button beside it forces a fresh attempt, and the **MCP Server: Local/REMOTE** toggle moves Revit to the other broker. |
| Instance present, `documents[]` empty | Document still opening, or a modal dialog is blocking Revit | Wait and re-check. A dialog needs a human to dismiss it. |
| Script stays `pending` | Revit's UI thread is blocked — usually a modal dialog, or the user is mid-edit | Don't retry; a second call just returns `busy`. Ask the user to check for an open dialog. |
| `status: "unresponsive"` | Revit stopped answering heartbeats | Wait; if it persists, Revit needs attention from the user. |
| `status: "unrecoverable"` | A prior script ignored cancellation | Nothing you send will run. Revit must be restarted; the instance gets a new `instance_id`. |
| Script fails with `CS0103`/`CS1503`/`CS0246` | A compile error, not infrastructure | Fix the script. `CS0246` usually means an unqualified Revit type — only `System` is imported. Check overloads with `describe_function` rather than guessing at a `CS1503`. |
| Error `code` is `script-write-outside-transaction` | A write outside any `WithTransaction` block | Wrap the write in `Connector.WithTransaction(doc, () => { … })` — see "Writing to a document". |
| Error `code` is `script-target-must-not-be-modifiable` | A self-transacting API (`LoadFamily`, `RequestViewChange`, an `EditScope`) called inside a block | Move it between blocks — see "Writing to a document". |
| Error `code` is `script-api-denied` | Something flatly rejected — most often your own `Transaction` | Nothing ran. No argument lifts this; the script has to change. |
| Error `code` is `script-lifecycle-confirmation-required` | A gated lifecycle/worksharing member without confirmation | Nothing ran. If genuinely intended, resend the **identical** call with `confirm_lifecycle_actions: true`. |
| `NewRoom` returns a room with no boundary segments and `Area == 0`, walls look correct | The room computation plane must fall *inside* the wall bodies and doesn't: `Level.Create` leaves `LEVEL_ROOM_COMPUTATION_HEIGHT` at 0, the level's own elevation — fine for walls flush on it, not if they have a base offset, are based on a lower level, or are shorter than that height. Revit only warns "Room is not in a properly enclosed region". | Set the level's `BuiltInParameter.LEVEL_ROOM_COMPUTATION_HEIGHT` to a height crossing the walls (e.g. `4.0` feet), then `Document.Regenerate()`; the existing room picks up its boundaries. It is **per-level** and recomputes every room on that level, so pick a height that suits all of them. |
| Changing a placed sheet's title block does nothing useful | `ViewSheet.SheetTitleBlockId` holds the **placed instance's** id, while `ViewSheet.Create(doc, titleBlockTypeId)` takes a **type** id. Both are `ElementId`, so reusing the symbol id compiles, is accepted with no validation, and silently leaves the sheet pointing at a `FamilySymbol`. A fatal Revit crash followed this once; that link is unconfirmed, the corruption is not. | Never assign a **type** id to it — if you already have, assigning the placed instance's id back restores the sheet. To change the title block, retype the instance: `doc.GetElement(sheet.SheetTitleBlockId)` is it (or a `FilteredElementCollector(doc, sheet.Id)` on `OST_TitleBlocks` with `WhereElementIsNotElementType()` when that id is already wrong), then `instance.ChangeTypeId(symbolId)`, which works across families. `GetValidTypes()` gates nothing. `ChangeTypeId` returns `InvalidElementId` (`-1`) on success, so read the instance's `Symbol` back instead. |

For a human debugging deeper: the add-in writes `connection.log` and `startup-errors.log` to
`%LOCALAPPDATA%\Connectors\Revit\` on the machine running Revit. `broker.json` lives there too in
local mode; in remote mode it moves to the shared drive — ask a human where that's configured.

## Quick reference

| Tool | Needs Revit? | Use it for |
|---|---|---|
| `list_instances` | no | what's connected; get `instance_id` / `document_id` |
| `execute_script` | yes | run C# against a document; writes inside `Connector.WithTransaction` |
| `poll_execution` | yes | finish a long-running script |
| `cancel_execution` | yes | stop one cooperatively |
| `undo` / `redo` | yes | revert / restore Revit's most recent action — global stack, may be a person's (`confirm: true`) |
| `search_functions` | yes | find an API member by intent |
| `list_functions` | yes | enumerate namespaces / types / members |
| `describe_function` | yes | signature + docs for one member |
| `search_howtos` | no | a verified script for a task; needs `instance_id` or `revit_version` |
| `describe_howto` | no | one how-to's script, pitfalls and verification |
| `submit_howto` | no | record a how-to, or fix one that misled you |
| `get_skills` | no | this document |

**Starting from nothing:** `list_instances` → pick an instance and document → `search_howtos` →
`execute_script`.
