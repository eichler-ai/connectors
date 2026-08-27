# Working with Revit through this connector

You drive one or more **live Revit sessions**. There is no fixed catalog of "create wall" /
"export IFC" tools — you run C# with `execute_script`, and three discovery commands let you read
Revit's own API documentation on demand.

Read this once at the start of a Revit task. It is orientation, not reference.

> **Read this first — current capability.** `execute_script` compiles real C#, but the objects it
> puts in scope are a **narrow seam**, not the Revit API. `Document` is not
> `Autodesk.Revit.DB.Document`; it exposes `Title` and nothing else. Revit API types are **not
> callable from a script today** — `new FilteredElementCollector(Document)` fails to compile with
> `CS0122 ... inaccessible due to its protection level`. What works today is document identity,
> file exchange, and the full .NET base class library. The discovery tools already reflect the real
> API so you can explore it, but you cannot yet *call* what you find. Don't spend turns trying;
> see "What's actually in scope" below.

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
  result, and why a second call against a busy instance returns `busy` rather than queueing.
- **The broker knows what's connected; only the add-in can touch a document.** So `list_instances`
  and `get_skills` answer instantly, while anything script-shaped needs a live, idle Revit.

**The add-in dials out to the broker, not the other way round.** On startup it reads the broker's
`broker.json` (port + auth token, written when the broker starts) and connects, retrying on a
backoff indefinitely if the broker isn't up yet. So order doesn't matter — start Revit first or the
broker first — and a broker restart heals itself within seconds without touching Revit. **If
something isn't connected, waiting a few seconds and re-checking is usually the correct first move**,
not an error to report.

On each successful connect the add-in sends a `register` snapshot: instance id, Revit version, and
the documents open *at that instant*. It is a snapshot, not a live feed — a document opened later
appears only after the next reconnect, which is why `documents[]` can lag reality.

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

`execute_script` takes `instance_id`, `document_id`, `script`, and optional `timeout_ms` /
`max_duration_ms`. The last expression is the result.

```csharp
return Document.Title;          // -> "MCPBridgeTest"
```

### What's actually in scope

| In scope | Type / use |
|---|---|
| `Document` | `Title` only |
| `UIDocument` | `.Document` → same as above; may be null |
| `UIApplication` | `.ActiveUiDocument` → may be null |
| `CancellationToken` | check it in loops (see below) |
| `ExportsDirectory`, `ImportsDirectory` | absolute paths, see "Exchanging files" |
| `Publish(path)` | copy a file into `exports/` and report it back to you |
| the .NET BCL | `System.IO`, `System.Linq`, etc. — fully usable |

Only `System` is imported by default, so use fully-qualified names (`System.IO.File.ReadAllText`)
or you'll get `CS0246`. Each run is wrapped in a transaction automatically; you cannot open your own.

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
- **`notices[]` on a *successful* result** means something was auto-resolved for you — a suppressed
  dialog, an auto-dismissed warning. It worked, but check what was papered over.
- `status: "cancelled"` is not an error; you asked for it.

**Empty `documents[]`** means no document is open *yet* — it may still be loading, or a modal dialog
may be blocking Revit (a dialog also stalls scripts at `pending`). Re-check `list_instances` rather
than retrying the script.

## Exchanging files

Each document gets a workspace with `exports/` and `imports/`. Both directions go through the
filesystem, not through MCP, so size is not a constraint. **Never hard-code the paths** — read them
from the globals; the workspace root has changed before.

**Revit → you.** Write the file, then `Publish` it. Published files come back in the result's
`files[]` with a path you can open directly:

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

Reflect over Revit's real installed assemblies rather than guessing names. Remember the caveat at the
top: this tells you what *exists*, which you can't call from a script yet — it's for planning and for
answering questions about the API.

- **`search_functions`** — start here when you know *what* you want, not the name.
  `{"query": "create wall"}` → ranked matches with summaries.
- **`list_functions`** — drill down. Omit `namespace` for the namespace list; pass `namespace` for its
  types; pass `namespace` + `type_name` for that type's members. Paginated — pass `cursor` for more.
- **`describe_function`** — full signature, parameters and docs for one member.
  `{"member": "Autodesk.Revit.DB.Wall.Create"}`.

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

**`list_instances` is your entry point** — it is the only tool that reports what is actually
connected, and it works with no Revit running. But note what it *cannot* tell you: it shows
successful connections only, so a Revit that never connected is simply absent, with no reason given.
For that, a human at the Revit machine clicks **MCP Bridge → Status** on the ribbon, which shows that
instance's id, whether it is connected and to which broker, and the loaded build. Ask for it rather
than guessing.

| Symptom | Most likely cause | What to do |
|---|---|---|
| `instances[]` empty | Revit not running, or the add-in didn't load | Wait a few seconds and re-check (the add-in retries on a backoff). If it stays empty, ask the user to confirm Revit is open and check **MCP Bridge → Status**. |
| Instance present, `documents[]` empty | Document still opening, a modal dialog is blocking Revit, or the snapshot predates the open | Wait and re-check. A dialog needs a human to dismiss it. |
| Script stays `pending` | Revit's UI thread is blocked — usually a modal dialog, or the user is mid-edit | Don't retry; a second call just returns `busy`. Ask the user to check for an open dialog. |
| `status: "unresponsive"` | Revit stopped answering heartbeats | Wait; if it persists, Revit needs attention from the user. |
| `status: "unrecoverable"` | A prior script ignored cancellation | Nothing you send will run. Revit must be restarted; the instance gets a new `instance_id`. |
| Script fails with `CS0103`/`CS0122`/`CS0246` | A compile error, not an infrastructure problem | Fix the script. Only `System` is imported; the Revit API is not reachable (see the note at the top). |

For a human debugging deeper, the add-in writes `connection.log` and `startup-errors.log` to
`%LOCALAPPDATA%\MCPBridge\`; the broker's discovery file is `%LOCALAPPDATA%\Connectors\Revit\broker.json`.

## Quick reference

| Tool | Needs Revit? | Use it for |
|---|---|---|
| `get_skills` | no | this document |
| `list_instances` | no | what's connected; get `instance_id` / `document_id` |
| `execute_script` | yes | run C# against a document |
| `poll_execution` | yes | finish a long-running script |
| `cancel_execution` | yes | stop one cooperatively |
| `search_functions` | yes | find an API member by intent |
| `list_functions` | yes | enumerate namespaces / types / members |
| `describe_function` | yes | signature + docs for one member |

**Starting from nothing:** `list_instances` → pick an instance and document → `execute_script`.
