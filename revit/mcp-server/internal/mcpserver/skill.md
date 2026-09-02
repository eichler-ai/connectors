# Working with Revit through this connector

You drive one or more **live Revit sessions**. There is no fixed catalog of "create wall" /
"export IFC" tools — you run C# with `execute_script`, and three discovery tools let you read
Revit's own API documentation on demand.

Read this once at the start of a Revit task. It is orientation, not reference.

> **Read this first — current capability.** `execute_script` compiles real C#, and the `Document`
> global **is** `Autodesk.Revit.DB.Document` — the real thing, not a wrapper. Pass it straight
> into any Revit API that wants a document:
> ```csharp
> var walls = new FilteredElementCollector(Document)
>     .OfClass(typeof(Wall)).GetElementCount();
> var level = Level.Create(Document, 42.0);   // writes work too
> return new { walls, levelId = level.Id.Value };
> ```
> `UIApplication` and `UIDocument` are likewise the real `Autodesk.Revit.UI` types (`UIDocument`
> may be null). Only `System` is imported, so either fully-qualify (`Autodesk.Revit.DB.Wall`) or
> nothing will resolve — see "Running a script" below.
>
> **You never open a transaction.** Every script already runs inside a `Transaction` and
> `TransactionGroup` this connector opens for you: your changes commit automatically if the script
> succeeds and roll back if it throws. Revit allows only one open transaction per document, so
> constructing your own is **rejected before your script runs** — see "What you may not do" below.

---

## How the pieces fit

```
  you  ──stdio/MCP──▶  MCP Server (broker)  ──TCP/JSON──▶  MCP Bridge (add-in) ──▶ Revit
                        one process,                        one per running
                        routes by instance                  Revit session
```

Two facts that shape everything below:

- **Scripts run on Revit's UI thread, one at a time per instance.** A long script blocks that
  instance. That is why `execute_script` can hand back a `pending`/`running` status instead of a
  result.
- **The broker knows what's connected; only the add-in can touch a document.** So `list_instances`
  and `get_skills` answer instantly, while anything script-shaped needs a live, idle Revit.

**The add-in dials out and retries on its own**, so start order doesn't matter and a broker restart
heals itself. **If something isn't connected, wait a few seconds and re-check** rather than reporting
a failure. `documents[]` is live: the add-in pushes a fresh snapshot on every document
open/close/create/activate, so a just-opened document appears within moments.

## Addressing: instances and versions

Every script call targets `{instance_id, document_id}`. Get both from `list_instances`:

```json
{"instances": [{
  "instance_id": "eb81f92b-...", "revit_version": "2027", "pid": 10652, "status": "idle",
  "memory": {"private_mb": 4096, "working_set_mb": 1800, "managed_mb": 520},
  "documents": [{"document_id": "doc-B2C2...", "title": "Tower", "workshared": false, "active": true}]
}]}
```

- `instance_id` is stable for that Revit **process**; it changes when Revit restarts.
- `document_id` is `doc-<hash>` for a saved file (derived from its path — stable across reopen) or
  `tmp-<guid>` for an unsaved one (session-only; don't persist it).
- `status` is `idle` / `pending` / `busy` / `unresponsive` / `unrecoverable`. Only `idle` starts work
  immediately. `unrecoverable` means that instance needs Revit restarted — nothing you send will run.
- `memory` (MB, updated each heartbeat) is the Revit process's own use; watch `private_mb`. It climbs
  across create/write/close cycles (mostly Revit's document memory, never released until exit), so on a
  long session restart Revit once it reaches multiple GB.

**Several Revit versions can be connected at once**, and 2025 and 2027 have genuinely different API
surfaces. Scripts are always explicitly targeted, so they're unaffected. Discovery is not — see below.

## Running a script

`execute_script` takes `instance_id`, `document_id` and `script`; its own schema lists the optional
timeout, duration, overwrite and confirmation parameters. `document_id` routes: the script runs
against that document — active or background — and its workspace follows it. An unknown id fails
with `document-not-found` and an `open_documents` list; omitted means the active document.
`UIDocument` is null unless the routed document is the active one; use `Document` for a background
one. `return` a value and it comes back as `return_value` — strings verbatim, collections and
anonymous types as JSON, anything else as a self-explaining `<...>` marker. `output` is stdout,
Revit's own writes too. A successful run that changed anything also carries **`mutations`** (net
`created`/`modified`/`deleted` across every document it touched, plus `by_category`), so skip the
read-after-write check. A short **`label`** ("create L1 walls") names the run's entry in Revit's Undo
history, the person's backstop if you got it wrong; omitted, one is derived from what changed.

```csharp
return Document.Title;
```
```json
{"status":"success","execution_id":"exec-4927...","return_value":"MCPBridgeTest"}
```

### What's actually in scope

| In scope | Type / use |
|---|---|
| `Document` | `Autodesk.Revit.DB.Document` — the real one, full API |
| `UIDocument` | `Autodesk.Revit.UI.UIDocument`; may be null |
| `UIApplication` | `Autodesk.Revit.UI.UIApplication` |
| ↳ `UIApplication.Application` | reached *through* the above, not its own global: `Autodesk.Revit.ApplicationServices.Application` |
| `CancellationToken` | check it in loops (see below) |
| `Connector` | **this connector's own functions, not Revit's** — `Connector.Publish(path)` and the rest, indexed under `Eichler.Connectors.Revit` (see "Discovering the API") |
| the .NET BCL | `System.IO`, `System.Linq`, etc. — fully usable |

Only `System` is imported by default, so use fully-qualified names (`Autodesk.Revit.DB.Wall`,
`System.IO.File.ReadAllText`) or you'll get `CS0246`. Use `using` *directives* freely at the top of
your script if you'd rather: `using Autodesk.Revit.DB;`.

**Creating documents — use `Connector.CreateProjectDocument` / `Connector.CreateFamilyDocument`.**
These create the document *and* open a transaction the connector manages for it, so you can write to it
immediately; it commits when your script returns and rolls back if it throws, exactly like the active
document. No confirmation needed — nothing persists until you save, which is gated separately.

```csharp
var doc = Connector.CreateProjectDocument();     // blank, writable, from Revit's default template
Autodesk.Revit.DB.Level.Create(doc, 10.0);       // just write to it — no transaction of your own
```

**What you get is headless**: in memory, no window, never the active document — writable by *script*,
invisible to the person. Making it visible takes **two calls**:
`UIApplication.OpenAndActivateDocument` needs a path, so `Connector.Settle(doc, true)` then `SaveAs` in
the creating run, then activate from a second call routed at any document **other than the active one**
(activation is refused only while the *active* document is modifiable, and your call's target always is).

**The raw `UIApplication.Application.NewProjectDocument`/`NewFamilyDocument` still work but return a
document nothing has opened for writing** — writing to it throws
`ModificationOutsideTransactionException`. Pass it to `Connector.OpenForWriting` first, or just use the
`Connector` calls above, which do both in one step.

Ask Revit for template paths rather than guessing: `Application.DefaultProjectTemplate` is a full `.rte`
path; `Application.FamilyTemplatePath` is the **root of the family-template tree** — search it
recursively (`SearchOption.AllDirectories`).

However you made it, a created document is unsaved, so it gets a session-only **`tmp-<guid>`
`document_id`**: it appears in `list_instances` like any other open document and a later call can be
routed straight at it, in which case it is that call's own `Document`, writable, nothing more to do.
Reached the other way — by walking `UIApplication.Application.Documents` and matching `Title` — it comes
back not modifiable; `Connector.OpenForWriting(doc)` makes it writable for that script.

**With several documents in play**, all of them commit after your script returns — created ones first,
the active one last. If one commit fails the rest are rolled back, but a commit that already succeeded
cannot be undone, so an earlier one can keep its changes; you then get a
`script-partial-commit` notice naming which documents kept theirs and which did not; the ordering means
the active document is always the one rolled back.

**Close your scratch documents — nothing else will.** Revit refuses `Close` and `SaveAs` while the
connector holds anything open on the document, so finish it first with `Connector.Settle(doc, false)`
(discard) or `Connector.Settle(doc, true)` (keep), then close in the same run with
`confirm_lifecycle_actions: true`. From a *later* call the document is already unmanaged and closes
directly — have the creating run `return doc.Title;` so the follow-up matches a title it actually saw:

```csharp
var wanted = "Project7";         // whatever Title the creating call returned — don't guess one
Autodesk.Revit.DB.Document scratch = null;
foreach (Autodesk.Revit.DB.Document d in UIApplication.Application.Documents)
    if (d.PathName == "" && d.Title == wanted) { scratch = d; break; }
if (scratch == null) return "already gone";
scratch.Close(false);            // false = discard; never prompts
```

**Test `PathName == ""` as well as `Title`.** `Close(false)` discards unsaved work without asking, and
`Title` alone does not identify a scratch document: Revit auto-names unsaved ones `Project1`, `Project2`,
… so a person's own looks exactly like yours, and a *saved* model at `…\Project1.rvt` has
`Title == "Project1"` too. The empty-`PathName` test keeps you off anything on disk.

**If your script threw, whatever it created is still open** — transactions roll back, documents don't
disappear. `list_instances` lists unsaved ones as `tmp-` ids; those absent before your call are yours.

Nothing tidies up for you, and a run of scratch documents will exhaust the memory of the Revit a person
is working in — which surfaces as a modal "Virtual Memory - High Usage" box; the connector now
auto-dismisses that specific one and reports it in `notices[]`. (Revit also refuses to `Close` the *active* document: from a call routed at anything
except the active document, activate the one you want to keep, then close the other on the next call.)

### Calls that need their target *not* modifiable

Some Revit APIs manage their own transaction and refuse a target with one open: `UIDocument.RequestViewChange`/`ActiveView`, `Document.LoadFamily`,
`UIApplication.OpenAndActivateDocument`, and every `EditScope`. **The document your call is routed at is
modifiable for the whole run**, so against it they always fail ("must not be modifiable") — reported with
`code` `script-target-must-not-be-modifiable`.

Wrap them. The connector closes its transaction for the block and restores it afterwards (one that had
none stays non-modifiable), so your changes still roll back if the script throws:

```csharp
Connector.WithoutTransaction(Document, () => {
    UIApplication.ActiveUIDocument.RequestViewChange(someView);   // applies once your script returns
});
```

Don't write inside that block — the document isn't modifiable there. To write, nest
`Connector.WithTransaction` (it also returns a value: `var id = Connector.WithTransaction(doc, () =>
Level.Create(doc, 3.0).Id);`; `describe_function` lists its overloads — pass a `member_id`). That pair
is what makes **stairs** work, and the closing edge is the point:
an edit scope can't commit while a transaction is open.

```csharp
Connector.WithoutTransaction(doc, () => {
    var scope = new Autodesk.Revit.DB.StairsEditScope(doc, "stairs");
    var id = scope.Start(baseLevel.Id, topLevel.Id);
    Connector.WithTransaction(doc, () => {
        Autodesk.Revit.DB.Architecture.StairsRun.CreateStraightRun(
            doc, id, line, Autodesk.Revit.DB.Architecture.StairsRunJustification.Center);
    });
    scope.Commit(yourFailuresPreprocessor);
});
```

`LoadFamily` needs **both** documents non-modifiable — nest a block per document. Nesting the same
scope on the *same* document is refused, so nest by document, not by helper method.

**To `Close`, `Save` or `SaveAs` a document in the run that touched it**, finish it first with
`Connector.Settle(doc, keep:)` (itself gated on `confirm_lifecycle_actions`, like the members it enables) — Revit refuses those while the connector holds anything open.
`keep: true` makes everything written to that document so far **permanent immediately**: a later failure
will no longer undo it. `keep: false` discards it, which is what you want before closing a scratch
document. Either way you get a notice saying so. Writing again afterwards is fine and rolls back as
usual — but the document is unmanaged after a settle, so write through
`Connector.WithTransaction(doc, () => { ... })` rather than directly, or you get
`script-write-outside-transaction`. Nothing settled comes back.

### What you may not do without saying so

Two different things here, and the difference matters. Both are caught **before your script runs**, by a
semantic check over the compiled code — so a refused script changes nothing and the transaction it would
have run in is rolled back cleanly.

**1. Flatly rejected — `new Transaction(...)`, `new TransactionGroup(...)`.**
Your script is already inside one, and Revit allows only one open transaction per document, so your own
can never work. There is no flag for this. Just make your changes directly; they commit on success and
roll back on failure. The error record's `code` is `script-api-denied`. It applies to every document,
including one you just created — use `Connector.CreateProjectDocument`/`CreateFamilyDocument` above,
which own that document's transaction. A native `SubTransaction` **is** allowed as a savepoint inside
the open transaction, **but only held in a `using`** — `using (var st = new
Autodesk.Revit.DB.SubTransaction(doc)) { st.Start(); … st.Commit(); }` (or `RollBack()`); any other
construction is rejected. Disposal is the safety net: one still active when the enclosing transaction
closed (block end, `WithoutTransaction`, `Settle`, an exception) crashed Revit later.

**2. Allowed, but only if you confirm — the document-lifecycle and worksharing calls.**

| Gated | What it escapes to |
|---|---|
| `Document.Close`, `.Dispose` | the session a person has open in front of them |
| `Document.Save`, `.SaveAs` | the filesystem |
| `Document.SynchronizeWithCentral` | the shared central model your teammates see |
| `Document.SaveAsCloudModel` | a cloud project other people can open |
| `Document.Print`, `.PrintToFile`, `PrintManager.SubmitPrint` | a physical device |
| `UIDocument.SaveAndClose` | the filesystem, then that person's session |
| `UIApplication.PostCommand` | anything, after your script's transaction has already closed |
| `WorksharingUtils.RelinquishOwnership` | another user's ability to edit |
| `Connector.Settle` | this run's own rollback guarantee for that document — see above |

**Why these and nothing else:** everything else you change is covered by the transaction wrapped around
your script, so if the script throws, your changes are undone automatically. These are not — they act
outside this document's own content, and no exception takes them back. That one question ("would a thrown
exception actually undo this?") is the whole rule.

Call one without confirming and the run is refused before it starts. The error record's `code` is
`script-lifecycle-confirmation-required` — match on that field, not on the message text — its `message`
names every gated member you used, and its `remedy` spells out the resend. To go ahead, resend the
**same** call with the flag:

```json
{"instance_id":"...","document_id":"...","script":"Document.Save(); return \"saved\";",
 "confirm_lifecycle_actions": true}
```

It applies to that one call only — confirming once does not confirm the next one, even for identical
script text. So treat the refusal as a real question, not a step to skip: if saving/closing/syncing is
what was actually asked for, confirm and proceed; if it crept into a script written for some other
purpose, remove it instead.

Everything else in the Revit API is fair game. These two lists are deliberately short and may grow; they
are about preventing damage nothing can undo, not sandboxing.

**Long scripts.** If the call exceeds `timeout_ms` you get `{"status":"running","execution_id":...}`.
Call `poll_execution` with that id until a terminal status. `cancel_execution` requests a stop — but
cancellation is *cooperative*: check the token in any loop, or the script won't stop and the instance
ends up `unrecoverable`.

```csharp
foreach (var x in items) {
    CancellationToken.ThrowIfCancellationRequested();
    // ...
}
```

## Reading errors

Every failure uses one shape. Read `message` for what happened, `remedy` for what to do:

```json
{"severity":"error","code":"script-execution-failed","source":"mcp-bridge.core.execution",
 "message":"(1,8): error CS0103: The name 'doc' does not exist in the current context",
 "detail":{"execution_id":"exec-33a9..."},
 "remedy":["..."]}
```

- **`message` names the real condition** — a compiler error, an API exception. It is not a wrapper;
  read it literally. Compiler errors mean fix the script, not retry it.
- **`code` is what you match on**, not the message. Most script failures are the generic
  `script-execution-failed`; a refusal carries its own — `script-api-denied` (change the script) or
  `script-lifecycle-confirmation-required` (resend with the flag), see "What you may not do".
- **`remedy` is actionable.** Follow it before retrying.
- **`notices[]` on a *successful* result** means something was auto-resolved for you. Dialogs your
  script triggers are auto-answered with the safe option and reported here (override per dialog with
  `Connector.DialogResultOverrides`). It worked — check what was papered over. A dialog already on screen when
  your script arrives is a different thing: it blocks Revit's UI thread and needs a human.
- `status: "cancelled"` is not an error; you asked for it.


## Exchanging files

Each document gets a workspace with `exports/` and `imports/`. Both directions go through the
filesystem, not through MCP, so size is not a constraint. **Never hard-code the paths** — read them
from the globals; the workspace root has changed before. Beside them, per-run audit files:
`scripts/` (verbatim script) and `logs/` (NDJSON diagnostics), swept after 14 days.

**Revit → you.** Write the file, then `Connector.Publish` it. Published files come back in `files[]`, each
with its own `status`; publishing onto an existing name **fails that file** unless you pass
`overwrite_output_files: true`. Paths are Windows-native; in remote mode map them through the
shared folder yourself (path rewriting isn't built).

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

Don't know the path yet? Run `return Connector.ImportsDirectory;` first, then write your file there.

## Discovering the API

Reflect over Revit's real installed assemblies rather than guessing names. What you find here is
directly callable from a script against the `Document` global — look up exact signatures and
overloads before writing one, cheaper than a round trip through a `CS1503`.

This covers **this connector's own functions too**, indexed like any add-in's and ranked below Revit's.
Anything under `Eichler.Connectors.Revit` is reached through the `Connector` global —
`Eichler.Connectors.Revit.Connector.Publish` is written `Connector.Publish(path)`.

- **`search_functions`** — start here when you know *what* you want, not the name. Ranking fuses a
  sentence-embedding pass with a keyword pass, then a cross-encoder reranks — so write `query` as
  **one plain sentence naming the element type and the operation** (`"move an element to a new
  location"`, not `"move"`); a suspected type or member name in it also scores through the keyword pass; `namespace`
  filters before ranking. A weak or empty result does **not** mean the API is absent — rephrase, or
  browse with `list_functions`. Each response says which `ranker` answered (`keyword-fallback` while
  a just-connected instance's index builds) and repeats this as `guidance`.
- **`list_functions`** — drill down. Omit `namespace` for the namespace list; pass `namespace` for its
  types; pass `namespace` + `type_name` for that type's members.
- **`describe_function`** — full signature, parameters and docs for one member.
  `{"member": "Autodesk.Revit.DB.Wall.Create"}`. Overloaded? You get an overload list back —
  re-call with just its `member_id` (or one from `search_functions`) to disambiguate.

`list_functions` and `search_functions` both paginate: pass the `cursor` from the previous response.

**Discovery needs a connected Revit**, and it is **version-specific**. With instances of different
Revit versions connected, omitting `instance_id` fails rather than guessing:

```json
{"code":"ambiguous-instance-version",
 "detail":{"candidates":[{"instance_id":"5faf...","revit_version":"2025"},
                         {"instance_id":"eb81...","revit_version":"2027"}]}}
```

Pick one from `candidates` and pass its `instance_id`. Every discovery response also reports the
`revit_version` it reflected, so you always know which surface answered.

## When something isn't working

**`list_instances` reports successful connections only** — a Revit that never connected is simply
absent. For the why, a human can click **MCP Bridge → Status** on the Revit ribbon.

| Symptom | Most likely cause | What to do |
|---|---|---|
| `instances[]` empty | Revit not running, or the add-in didn't load | Wait a few seconds and re-check (the add-in retries on a backoff). If it stays empty, ask the user to confirm Revit is open and check **MCP Bridge → Status**. |
| Instance present, `documents[]` empty | Document still opening, or a modal dialog is blocking Revit | Wait and re-check. A dialog needs a human to dismiss it. |
| Script stays `pending` | Revit's UI thread is blocked — usually a modal dialog, or the user is mid-edit | Don't retry; a second call just returns `busy`. Ask the user to check for an open dialog. |
| `status: "unresponsive"` | Revit stopped answering heartbeats | Wait; if it persists, Revit needs attention from the user. |
| `status: "unrecoverable"` | A prior script ignored cancellation | Nothing you send will run. Revit must be restarted; the instance gets a new `instance_id`. |
| Script fails with `CS0103`/`CS1503`/`CS0246` | A compile error, not infrastructure | Fix the script. `CS0246` usually means an unqualified Revit type — only `System` is imported, so write `Autodesk.Revit.DB.Wall` or add a `using`. Check overloads with `describe_function` rather than guessing at a `CS1503`. |
| `NewRoom` returns a room with no boundary segments and `Area == 0`, walls look correct | The room computation plane must fall *inside* the wall bodies, and doesn't. `Level.Create` leaves `LEVEL_ROOM_COMPUTATION_HEIGHT` at 0, i.e. the level's own elevation — fine for walls sitting flush on it, but not if they have a base offset, are based on a lower level, or are shorter than the height you set. Revit only warns "Room is not in a properly enclosed region". | Set the level's `BuiltInParameter.LEVEL_ROOM_COMPUTATION_HEIGHT` to a height crossing the walls (e.g. `4.0` feet), then `Document.Regenerate()`; the existing room picks up its boundaries, no need to re-place it. It is a **per-level** setting and recomputes every room on that level, so pick a height that suits all of them. |
| Changing a placed sheet's title block does nothing useful | `ViewSheet.SheetTitleBlockId` holds the **placed instance's** id, while `ViewSheet.Create(doc, titleBlockTypeId)` takes a **type** id. Both are `ElementId`, so reusing the symbol id that worked in `Create` compiles, is accepted with no validation, and silently leaves the sheet pointing at a `FamilySymbol` instead of its title block. A fatal Revit crash followed this once; that link is unconfirmed, but the corruption is. | Don't assign a **type** id to it — and if you already have, assigning the placed instance's id back restores the sheet. To change the title block, retype the instance: `doc.GetElement(sheet.SheetTitleBlockId)` is it, or use a `FilteredElementCollector(doc, sheet.Id)` on `OST_TitleBlocks` with `WhereElementIsNotElementType()` when that id is already wrong. Then `instance.ChangeTypeId(symbolId)`, which works across families. `GetValidTypes()` enumerates candidates but gates nothing — it calls another family's symbol valid. `ChangeTypeId` returns `InvalidElementId` (`-1`) on success, so read the instance's `Symbol` back instead of testing it. |
| Error `code` is `script-api-denied` | You used something flatly rejected — most often opening your own `Transaction` | See "What you may not do". Nothing ran and nothing changed; no argument lifts this, the script has to change. |
| Error `code` is `script-lifecycle-confirmation-required` | A gated lifecycle/worksharing member without confirmation | Nothing ran. If genuinely intended, resend the **identical** call with `confirm_lifecycle_actions: true`. The `message` names every gated member used. |
| Error `code` is `script-target-must-not-be-modifiable` | A self-transacting API (`LoadFamily`, `RequestViewChange`, an `EditScope`) hit the connector's open transaction | Wrap the call in `Connector.WithoutTransaction` — see "Calls that need their target *not* modifiable". |
| Error `code` is `script-subtransaction-needs-transaction` | `SubTransaction.Start()` with no transaction open | Nest the code in `Connector.WithTransaction(doc, () => { … })`. |

For a human debugging deeper: the add-in writes `connection.log` and `startup-errors.log` to
`%LOCALAPPDATA%\Connectors\Revit\` on the machine running Revit. `broker.json` lives there too in
local mode; in remote mode it moves to the shared drive — ask a human where that's configured.

## Quick reference

| Tool | Needs Revit? | Use it for |
|---|---|---|
| `list_instances` | no | what's connected; get `instance_id` / `document_id` |
| `execute_script` | yes | run C# against a document |
| `poll_execution` | yes | finish a long-running script |
| `cancel_execution` | yes | stop one cooperatively |
| `search_functions` | yes | find an API member by intent |
| `list_functions` | yes | enumerate namespaces / types / members |
| `describe_function` | yes | signature + docs for one member |

**Starting from nothing:** `list_instances` → pick an instance and document → `execute_script`.
