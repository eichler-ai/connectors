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
  "documents": [{"document_id": "doc-B2C2...", "title": "Tower", "workshared": false, "active": true}]
}]}
```

- `instance_id` is stable for that Revit **process**; it changes when Revit restarts.
- `document_id` is `doc-<hash>` for a saved file (derived from its path — stable across reopen) or
  `tmp-<guid>` for an unsaved one (session-only; don't persist it).
- `status` is `idle` / `pending` / `busy` / `unresponsive` / `unrecoverable`. Only `idle` starts work
  immediately. `unrecoverable` means that instance needs Revit restarted — nothing you send will run.

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
Revit's own writes too.

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

**What you get is headless**: in memory, no window, no open view, never the active document — writable
by *script*, not visible to the person, who sees nothing appear. Making it visible takes **three calls**,
and the reason is `SaveAs`, not activation: `UIApplication.OpenAndActivateDocument` needs a path, and a
connector-created document cannot be saved in the run that created it (below). So: create, `SaveAs` from
a second call, activate from a third. Route that last one at any document **other than the currently
active one** — activation is refused only while the *active* document is modifiable, and your call's own
target always is.

**The raw `UIApplication.Application.NewProjectDocument`/`NewFamilyDocument` still work but return a
document nothing has opened for writing** — writing to it throws
`ModificationOutsideTransactionException`. Pass it to `Connector.OpenForWriting` first, or just use the
`Connector` calls above, which do both in one step.

Ask Revit for template paths rather than guessing: `Application.DefaultProjectTemplate` is a full `.rte`
path, and `Application.FamilyTemplatePath` is the **root of the family-template tree**, not a flat
folder of `.rft` files — templates sit in subdirectories, so search recursively
(`SearchOption.AllDirectories`).

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

**Close your scratch documents — just not in the run that created them.** For the length of that run the
connector holds the document's transaction open, and **Revit itself** refuses both `Close` ("Close is not
allowed when there is any open sub-transaction, transaction or transaction group") and `SaveAs` ("Unable
to close all open transaction phases!"); no flag lifts either. The transaction closes when your script
returns, so the **next** call does both normally with `confirm_lifecycle_actions: true`. Have the creating
run `return doc.Title;` so the follow-up matches a title it actually saw:

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
is working in — which surfaces as a modal "Virtual Memory - High Usage" box that wedges Revit until a
human clicks it. (Revit also refuses to `Close` the *active* document: from a call routed at anything
except the active document, activate the one you want to keep, then close the other on the next call.)

### What you may not do without saying so

Two different things here, and the difference matters. Both are caught **before your script runs**, by a
semantic check over the compiled code — so a refused script changes nothing and the transaction it would
have run in is rolled back cleanly.

**1. Flatly rejected — `new Transaction(...)`, `new TransactionGroup(...)`, `new SubTransaction(...)`.**
Your script is already inside one, and Revit allows only one open transaction per document, so your own
can never work. There is no flag for this. Just make your changes directly; they commit on success and
roll back on failure. The error record's `code` is `script-api-denied`. The refusal ignores *which*
document you meant it for, including one you just created. That is not a gap to work around: use
`Connector.CreateProjectDocument`/`Connector.CreateFamilyDocument` above and the connector owns that document's transaction
for you, so there is never a reason to construct one.

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

- **`search_functions`** — start here when you know *what* you want, not the name.
  `{"query": "create wall"}` → ranked matches with summaries.
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
| Error `code` is `script-api-denied` | You used something flatly rejected — most often opening your own `Transaction` | See "What you may not do". Nothing ran and nothing changed; no argument lifts this, the script has to change. |
| Error `code` is `script-lifecycle-confirmation-required` | A gated lifecycle/worksharing member without confirmation | Nothing ran. If genuinely intended, resend the **identical** call with `confirm_lifecycle_actions: true`. The `message` names every gated member used. |

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
