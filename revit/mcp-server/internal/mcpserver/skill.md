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
a failure. On each connect the add-in sends a snapshot of the documents open *at that instant* — not
a live feed, so `documents[]` lags a document opened later.

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

`execute_script` takes `instance_id`, `document_id`, `script`, and optional `timeout_ms`,
`max_duration_ms` and `overwrite_output_files`. `return` a value and it comes back as `output`:

```csharp
return Document.Title;
```
```json
{"status":"success","execution_id":"exec-4927...","output":"MCPBridgeTest","files":[],"notices":[]}
```

### What's actually in scope

| In scope | Type / use |
|---|---|
| `Document` | `Autodesk.Revit.DB.Document` — the real one, full API |
| `UIDocument` | `Autodesk.Revit.UI.UIDocument`; may be null |
| `UIApplication` | `Autodesk.Revit.UI.UIApplication` |
| `CancellationToken` | check it in loops (see below) |
| `ExportsDirectory`, `ImportsDirectory` | absolute paths, see "Exchanging files" |
| `Publish(path, name?)` | copy a file into `exports/` and report it back; `name` renames it |
| `DialogResultOverrides` | per-dialog answer override, e.g. `DialogResultOverrides["TaskDialog_X"] = 1001` |
| the .NET BCL | `System.IO`, `System.Linq`, etc. — fully usable |

Only `System` is imported by default, so use fully-qualified names (`Autodesk.Revit.DB.Wall`,
`System.IO.File.ReadAllText`) or you'll get `CS0246`. Use `using` *directives* freely at the top of
your script if you'd rather: `using Autodesk.Revit.DB;`.

### What you may not do

A small denylist is enforced **at compile time**, before your script runs — so a rejected script
changes nothing, and the transaction it would have run in is rolled back cleanly. Rejections come
back as a failed execution whose error names `script-api-denied` and the exact member.

| Rejected | Why | Do this instead |
|---|---|---|
| `new Transaction(...)`, `new TransactionGroup(...)`, `new SubTransaction(...)` | Your script is already inside one, and Revit allows only one open transaction per document | Just make your changes; they commit on success, roll back on failure |
| `Document.Close`, `.Save`, `.SaveAs`, `.SynchronizeWithCentral`, `.Print` | Changes the document's lifecycle or worksharing state, not its content — on a file a person has open | Ask the person driving Revit |
| `WorksharingUtils.RelinquishOwnership` | Same reason | Ask the person driving Revit |

Everything else in the Revit API is fair game. This list is deliberately short and may grow; it is
about preventing accidental damage, not sandboxing — if you hit it, you wanted a different approach,
not a way around it.

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
- **`remedy` is actionable.** Follow it before retrying.
- **`notices[]` on a *successful* result** means something was auto-resolved for you. Dialogs your
  script triggers are auto-answered with the safe option and reported here (override per dialog with
  `DialogResultOverrides`). It worked — check what was papered over. A dialog already on screen when
  your script arrives is a different thing: it blocks Revit's UI thread and needs a human.
- `status: "cancelled"` is not an error; you asked for it.


## Exchanging files

Each document gets a workspace with `exports/` and `imports/`. Both directions go through the
filesystem, not through MCP, so size is not a constraint. **Never hard-code the paths** — read them
from the globals; the workspace root has changed before.

**Revit → you.** Write the file, then `Publish` it. Published files come back in the result's
`files[]` with a path you can open directly. Each entry carries its own `status`: publishing onto a
name that already exists **fails that file** unless you pass `overwrite_output_files: true`.

```csharp
var p = System.IO.Path.Combine(ExportsDirectory, "rooms.csv");
System.IO.File.WriteAllText(p, "Name,Area\n");
Publish(p);
return "ok";
```

**You → Revit.** Put the file in `imports/` and read it with ordinary `System.IO`:

```csharp
return System.IO.File.ReadAllText(System.IO.Path.Combine(ImportsDirectory, "rooms.csv"));
```

Don't know the path yet? Run `return ImportsDirectory;` first, then write your file there.

## Discovering the API

Reflect over Revit's real installed assemblies rather than guessing names. What you find here is
directly callable from a script against the `Document` global — use it to look up exact signatures
and overloads before writing one, which is far cheaper than a round trip through a `CS1503`.

- **`search_functions`** — start here when you know *what* you want, not the name.
  `{"query": "create wall"}` → ranked matches with summaries.
- **`list_functions`** — drill down. Omit `namespace` for the namespace list; pass `namespace` for its
  types; pass `namespace` + `type_name` for that type's members.
- **`describe_function`** — full signature, parameters and docs for one member.
  `{"member": "Autodesk.Revit.DB.Wall.Create"}`. If the member is overloaded you get the overload
  list back instead — re-call with a `member_id` from it.

`list_functions` and `search_functions` both paginate: pass the `cursor` from the previous response.

**Discovery needs a connected Revit**, and it is **version-specific**. With instances of different
Revit versions connected, omitting `instance_id` fails rather than guessing:

```json
{"code":"ambiguous_instance_version",
 "detail":{"candidates":[{"instance_id":"5faf...","revit_version":"2025"},
                         {"instance_id":"eb81...","revit_version":"2027"}]}}
```

Pick one from `candidates` and pass its `instance_id`. Every discovery response also reports the
`revit_version` it reflected, so you always know which surface answered.

## When something isn't working

**`list_instances` is your entry point**, but it reports *successful connections only* — a Revit that
never connected is simply absent, with no reason given. For that, ask a human to click
**MCP Bridge → Status** on the Revit ribbon: it shows that instance's id, whether it's connected and
to which broker, and the loaded build.

| Symptom | Most likely cause | What to do |
|---|---|---|
| `instances[]` empty | Revit not running, or the add-in didn't load | Wait a few seconds and re-check (the add-in retries on a backoff). If it stays empty, ask the user to confirm Revit is open and check **MCP Bridge → Status**. |
| Instance present, `documents[]` empty | Document still opening, a modal dialog is blocking Revit, or the snapshot predates the open | Wait and re-check. A dialog needs a human to dismiss it. |
| Script stays `pending` | Revit's UI thread is blocked — usually a modal dialog, or the user is mid-edit | Don't retry; a second call just returns `busy`. Ask the user to check for an open dialog. |
| `status: "unresponsive"` | Revit stopped answering heartbeats | Wait; if it persists, Revit needs attention from the user. |
| `status: "unrecoverable"` | A prior script ignored cancellation | Nothing you send will run. Revit must be restarted; the instance gets a new `instance_id`. |
| Script fails with `CS0103`/`CS1503`/`CS0246` | A compile error, not an infrastructure problem | Fix the script. `CS0246` almost always means an unqualified Revit type — only `System` is imported, so write `Autodesk.Revit.DB.Wall` or add `using Autodesk.Revit.DB;`. Use `describe_function` to check an overload rather than guessing at a `CS1503`. |
| Script fails naming `script-api-denied` | You used something on the denylist — most often opening your own `Transaction` | See "What you may not do". Nothing ran and nothing changed. |

For a human debugging deeper, the add-in always writes `connection.log` and `startup-errors.log` to
`%LOCALAPPDATA%\Connectors\Revit\` on the machine running Revit, regardless of local/remote mode. The
broker's own discovery file, `broker.json`, lives there too in local mode (Revit and the broker on the
same machine); in remote mode (broker on a different machine, e.g. this project's own Mac+Parallels
dev setup) `broker.json` moves to the shared drive instead — ask a human where that's configured if you
need it.

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
