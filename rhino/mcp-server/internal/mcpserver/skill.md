# Working with Rhino through this connector

You drive one or more **live Rhino 8 sessions**: you run **Python or C#** with `execute_script`, look
at any viewport with `capture_view`, and revert your own work with `undo`/`redo`. Read this once at
the start of a Rhino task; it is orientation, not reference.

**Three facts to carry into every script.**

1. **The globals are the real RhinoCommon types.** In both languages `doc` / `Document` **is** a
   `Rhino.RhinoDoc`, not a wrapper; pass it straight into any RhinoCommon call. Python also gets
   `rhinoscriptsyntax` (import it as `rs`) and `scriptcontext` already pointed at the routed document.
2. **You do not open or manage undo yourself.** The connector runs every script inside one command,
   so the whole run is **one entry in Rhino's Undo history**, and if the script raises the run is
   **reverted automatically**. Never call `RhinoDoc.Undo/Redo/BeginUndoRecord` or the `_Undo`/`_Redo` commands
   from a script — they are refused (`script-api-denied`), because they would destroy that entry.
3. **No prompting.** A script has no person at the keyboard, so the interactive getters
   (`rs.GetObject`, `rs.GetPoint`, `Rhino.Input.RhinoGet.*`, the `Rhino.Input.Custom.Get*` objects,
   `Rhino.UI.Dialogs.*`) are refused before the script runs. Select and build inputs in code instead:
   `doc.Objects.FindByLayer(...)`, explicit points, literal values.

```python
#! the shebang is added for you
import Rhino, rhinoscriptsyntax as rs
sphere = Rhino.Geometry.Sphere(Rhino.Geometry.Point3d.Origin, 10)
doc.Objects.AddSphere(sphere)              # a write; the whole run is one undo entry
result = {"objects": doc.Objects.Count}    # assign `result` to return a value
```
```csharp
var count = Document.Objects.Count;                                   // read
Document.Objects.AddSphere(new Rhino.Geometry.Sphere(Rhino.Geometry.Point3d.Origin, 10));
return new { objects = Document.Objects.Count };                      // return a value
```

---

## How the pieces fit

```
  you  ──stdio/MCP──▶  MCP Server (broker)  ──TCP/JSON──▶  MCP Bridge (plug-in) ──▶ Rhino
                        one per session,                    one per running
                        dials every instance                Rhino session
```

- **Scripts run on Rhino's main thread, one at a time per instance.** A long script blocks that
  instance, which is why `execute_script` can hand back `pending`/`running` instead of a result.
- **The broker knows what's connected; only the plug-in can touch a document.** `list_instances` and
  `get_skills` answer instantly; anything script-shaped needs a live, idle Rhino.
- **The plug-in listens and every broker dials in and retries on its own.** Start order doesn't
  matter and a broker restart heals itself. If something isn't connected, **wait a few seconds and
  re-check** rather than reporting a failure. `documents[]` is live — a just-opened document appears
  within moments. Two of your sessions are two brokers into the same plug-in; each sees the same
  answer, and any of them can `poll_execution` or `cancel_execution` any run.

## Addressing: instances, documents, platforms

Every script call targets `{instance_id, document_id}`, both from `list_instances`:

```json
{"instances": [{
  "instance_id": "eb81f92b-...", "rhino_version": "8.35...", "platform": "macos",
  "pid": 10652, "status": "idle", "memory": {"working_set_mb": 1800},
  "documents": [{"document_id": "doc-b2c2...", "title": "Tower", "active": true,
    "last_run": {"execution_id": "exec-...", "agent_client_id": "...", "finished_at": "...",
                 "status": "success", "changed_document": true}}]
}]}
```

- `instance_id` is stable for that Rhino **process**; it changes when Rhino restarts.
- `document_id` is `doc-<hash>` for a saved file (from its path, stable across reopen) or `tmp-<guid>`
  for an unsaved one (session-only; don't persist it). Omitted on a call means the active document; an
  unknown id fails loudly with `document-not-found` and the open ids, never a silent fallback.
- **Platforms differ.** On **macOS** one Rhino holds **many documents**, one window each — address the
  one you mean with `document_id`. On **Windows** one Rhino holds **one document**; a second file is a
  second instance, and `document_id` may be omitted. `platform` tells you which you are on.
- `status` is `idle` / `pending` / `busy` / `unresponsive` / `unrecoverable`. Only `idle` starts work
  at once. `unrecoverable` means that instance needs Rhino restarted — nothing you send will run.
- `last_run` per document is the connector's last completed run there, from any of your sessions
  (`agent_client_id` names which). It is how you notice another client acted since your last call.
  `working_set_mb` on macOS is the headline memory figure.

## Running a script

`execute_script` takes `instance_id`, `document_id`, **`language`** (`"python"` or `"csharp"`,
required — the two hosts differ and a script for one does not run in the other), and the `script` to
run. Optional: `timeout_ms`, `max_duration_ms`, `confirm_lifecycle_actions`, `label` (names the run's
Undo entry and rides on the result).

**Python is real CPython 3** (Rhino's own script host), not the legacy Python-2 editor. `import` works; `rhinoscriptsyntax`,
`scriptcontext`, `Rhino`, `System` are all available. A module has no `return`, so **assign a variable
named `result`** and it comes back as `return_value`. `print` goes to `output`.

**C# is a Roslyn script.** `return` a value. Only `System` is imported, so qualify RhinoCommon types
(`Rhino.Geometry.Sphere`) or add `using Rhino.Geometry;` at the top.

**What comes back.** `return_value` (strings verbatim, collections and objects as JSON), `output`
(stdout), `notices[]` (everything else the run wants to tell you, on failed runs too), and on a run
that changed the document **`mutations`**: `net_added`/`net_modified`/`net_deleted` plus
`by_object_type` and `by_layer`. Net, not activity — exactly what one Undo of the run would revert.
A successful read-only run carries no `mutations`. Skip any read-after-write check.

**Long scripts.** Past `timeout_ms` you get `{"status":"running","execution_id":...}`; call
`poll_execution` with that id until a terminal status. `cancel_execution` requests a stop, but
cancellation is **cooperative** in both languages — Rhino's script host cannot interrupt a running
script. Check for it in any loop, or the run never stops and the instance ends up `unrecoverable`:

```python
import time
while working:
    cancel.Check()          # raises when cancelled; cancel.IsRequested is the non-raising form
    time.sleep(0.1)
```
```csharp
while (working) { CancellationToken.ThrowIfCancellationRequested(); System.Threading.Thread.Sleep(100); }
```

### What's in scope

| Python | C# | what it is |
|---|---|---|
| `doc` | `Document` | the routed `Rhino.RhinoDoc`, full RhinoCommon API |
| `cancel` | `CancellationToken` | cooperative cancellation (above) |
| `connector` | `Connector` | **this connector's own functions, not Rhino's** — `BridgeVersion`, `RunLabel` |
| `rs`, `scriptcontext` | — | `rhinoscriptsyntax` and `scriptcontext.doc` (the routed document) |
| the BCL | the BCL | `System.IO`, LINQ, etc. — fully usable |

`ghdoc` (Python) is a Grasshopper document handle, `None` until Grasshopper support ships.

This is **not a sandbox**: full API access, one narrow denylist (below). Reflection can route around
it, and that is accepted — the denylist guards against the common accident, not a determined bypass.

## What is refused, and what needs your confirmation

The one test: **would the automatic undo actually revert this?** Anything that escapes that boundary
is gated.

- **Refused outright (`script-api-denied`), no opt-in:** the undo/redo members and `_Undo`/`_Redo`
  commands (fact 2 above); the interactive getters and dialogs (fact 3); `RhinoApp.Exit`; and, in
  Python, `exec`/`eval`/`__import__` and a computed `getattr` (the guard is a text walk and cannot see
  through them). Change the script; no argument lifts this.
- **Confirmation-gated (`script-lifecycle-confirmation-required`):** members that act **outside this
  document's content** and no undo reverts — `RhinoDoc.Save`/`SaveAs`/`Export`/`Write3dmFile`,
  `Open`/`Create`/`Import`, and the `_Save`/`_Export`/`_Open`/`_New`/`_Close`/`_Print`-class commands
  via `RhinoApp.RunScript` or `rs.Command`. The refusal names each member. If it is genuinely
  intended, **resend the identical call with `confirm_lifecycle_actions: true`**; otherwise remove the
  call.

A note on `RunScript`/`rs.Command`: a **computed** command string (`rs.Command(cmd_variable)`) is not
gated — those functions call Rhino directly, past the connector — so use a literal when you can.

## Looking at a viewport — `capture_view`

`capture_view` returns an image of a viewport so you can *see* the model to debug. `target` is
`"active"` (default), a viewport name (`Perspective`, `Top`, `Front`, `Right`), or `"all"` (one image
each). It changes nothing and takes no undo entry, but is serialised with scripts: `busy` while one
runs. Options: `display_mode` (`Shaded`, `Wireframe`, `Rendered`, …, restored after), `zoom`
(`extents`/`selected`), `width`/`height` (default ~1024 px long edge, cap 2048), `format`
(`jpeg` default, `png`), `transparent_background`. Use it after a geometry run to confirm the result,
or when a script's output is surprising.

## Undoing your own work — `undo` / `redo`

`undo` and `redo` run Rhino's undo on a document and report what they reverted. **You do not need
`confirm` to undo the connector's own run** — the plug-in tracks changes made outside its runs and
knows the top entry is yours (`undo-reverted-connector-work`, naming the run and its label). If the
document changed outside the connector since (a person drew something), the call is refused with
`undo-confirmation-required` naming the last command Rhino ran; resend with `confirm: true` only if
reverting *that* is intended (`undo-reverted-other-work`, a warning). For a mistake **inside** a
script, don't reach for these — raise/throw and the connector reverts the whole run. An undo is an
execution for the busy gate: `busy` while a script runs, and scripts are `busy` while it runs.

## Reading errors

Every failure is one record: `code` (kebab-case), `message`, and usually a `remedy`. Read the code,
not just the message. The ones you'll meet:

| code | means | do |
|---|---|---|
| `document-not-found` | no open document has that id | pick a `document_id` from `list_instances` |
| `language-not-available` | wrong/absent language, or the Python host still loading | use `csharp`/`python`; if loading, retry in a few seconds |
| `script-compilation-failed` | the script doesn't compile/parse | fix it; Python line numbers are the script as you sent it |
| `script-api-denied` | a refused member (above) | change the script |
| `script-lifecycle-confirmation-required` | a gated member (above) | resend with `confirm_lifecycle_actions: true` if intended |
| `script-execution-failed` | the script threw | read the message/traceback; the run was reverted |
| `instance-busy` / status `busy` | a run holds the instance | `poll_execution`, or wait |
| `instance-unrecoverable` | a non-cooperating run wedged the main thread | the person must restart Rhino |

A rolled-back run carries a `script-rolled-back` notice with the counts; `script-rollback-skipped`
(rare) means the connector did **not** revert because a person acted between the run and the undo —
inspect the document rather than assuming it's clean.

## When something isn't working

- **Nothing connected / empty `list_instances`.** The plug-in dials in on its own; wait a few seconds
  and re-check. A Rhino sitting at the template chooser with no document open runs nothing — a
  document must be open.
- **`pending` that never becomes `running`.** The main thread is busy — a modal dialog, or Rhino
  sitting inside an interactive command (a stray `Trim` prompt, say). The person needs to clear it.
- **`MCPBridgeStatus`** in Rhino's command line shows the port, connection count, instance id and
  bridge version — the human-readable check that the plug-in is loaded and which build it is.
- **Verify the build.** `get_skills` ends with a Provenance footer naming the build that served it; if
  it is missing, the broker predates that footer and may be older than your connector.

## Quick reference

| tool | for |
|---|---|
| `list_instances` | what Rhino sessions and documents exist; status, memory, `last_run` |
| `execute_script` | run Python or C#; `language` is required |
| `poll_execution` | wait on a `running` execution id for its result |
| `cancel_execution` | request a cooperative stop |
| `capture_view` | see a viewport (image) to debug |
| `undo` / `redo` | revert or restore the connector's own run |
| `get_skills` | this guide |
