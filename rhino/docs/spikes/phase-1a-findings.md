# Phase 1a spikes — findings

Live findings against **Rhino 8.35.26251.13002 for Mac (2026-09-08 build), macOS arm64**, on 2026-09-10. Everything below was observed, not read: the probe scripts ran through the `rhinocode` CLI against the running Rhino, and the plug-in probes ran from a throwaway `net8.0` plug-in (`phase-1a/PlugIn.cs`); the run order, dead ends and raw outputs are in [`phase-1a-method.md`](phase-1a-method.md) installed through `yak`. The plug-in code is kept as a record of what was tried, not as a starting point.

Each item names the PRD question it answers (§17 numbering) and the PRD section amended.

## 1. The CPython host (§17 item 1 → §06)

**Real CPython 3.9.10**, `sys.implementation.name == "cpython"`, hosted in-process. `Rhino.Runtime.PythonScript.Create()` (the public, documented API the most popular incumbent uses) is IronPython; the CPython host is `Rhino.Runtime.Code`.

**Languages load lazily and a plug-in must trigger it.** On a fresh Rhino, `RhinoCode.Languages` holds only the four built-ins (plain text, JSON, YAML, git dotfile) and `RunScript` fails with `CodeLanguageNotFoundException: Can not determine language for <guid>` — which reads like a shebang problem and is not one. `RhinoCode.Languages.WaitStatusComplete(LanguageSpec.Python3)` loads the language in **1.75 s** (warm runtime; the very first run on a machine also deploys the CPython runtime to `~/.rhinocode/py39-rh8/`, ~35 s, done once per machine). The plug-in does this at load, off the main thread, as its warm-up — the analogue of Revit's `WarmupCompile`.

**Running a script.** `RhinoCode.RunScript(text, RunContext)` with `#! python 3` as the first line. `RunContext.Inputs.Set(name, value)` binds globals; `Outputs.Set(name, null)` declares a variable to read back after the run with `Outputs.Get<T>(name)` (a Python `dict` came back as a .NET `Dictionary<object,object>`); `OutputStream`/`ErrorStream` are `Stream`s and capture `print` and the traceback. Timings: 44 ms first run after language load, 19–37 ms after. Also present and worth using: `RunContext.RecordDocumentUndo`, `ResetStreamsPolicy`, `ExclusiveStreams`, `LastRunTimeSpan`.

**A Python exception** surfaces as `Rhino.Runtime.Code.Execution.ExecuteException` whose `Message` is the Python message (`boom from python`) and whose traceback is on `ErrorStream`, not on the exception. Objects the script added before throwing stay in the document (see §3 below for why that is fine).

**The host does not marshal to the main thread.** `RunScript` called from a background thread ran the script *on that thread* (`threading.current_thread().name == "Dummy-1"`) and let it touch the document. The connector must marshal itself; `RhinoApp.InvokeOnUiThread` does, and the script then reports `MainThread`.

**No interruption API.** The full public surface of `Rhino.Runtime.Code` (dumped by reflection, ~1,000 members) has no cancel, interrupt, abort or stop for a running `Code`; `Code.IsExecuting` is the only runtime state. Cancellation for Python is therefore **cooperative only**, exactly as the PRD hedged: the connector's `cancel` global is polled by the script, and a script that ignores it resolves to `unrecoverable` after the grace period, as a non-cooperating C# script does.

## 2. Plug-in target framework (§17 item 2 → §13)

Rhino 8.35 for Mac hosts **.NET 8**: `dotnetstart.8.runtimeconfig.json` in the app bundle declares `net8.0` with `rollForward: LatestMinor`, and the loaded CLR reports 8.0.14. A plug-in built as `net8.0` with the Homebrew `dotnet@8` SDK (8.0.131) against the bundled `RhinoCommon.dll` and `Rhino.Runtime.Code.dll` (referenced by `HintPath`, `Private=false`) loaded at startup and registered its commands. No `net7.0` build was needed or tried. Windows is unverified and is phase 2's first check.

## 3. Undo records (§17 item 3 → §07)

This is the finding that changes the design, so it is spelled out.

- **Undo is deferred to the end of the current command.** Inside a running script or command, `RhinoDoc.Undo()` and the `_Undo` command both return `true` and revert nothing. `RhinoDoc.UndoActive` reads *true* once an undo is pending, not when the stack has entries — it is not a "can undo" flag.
- **A mid-command `Undo()` is worse than a no-op**: after the command ended, the record was gone and the objects were still there. Never call `Undo()` from inside the run.
- **Every command is one undo entry, named after the command.** Objects added inside a plug-in command — by C#, or by a Python script the command ran through `RhinoCode` — form one entry (`Undoing MCPSpikePyAdd` in the history). `BeginUndoRecord`/`EndUndoRecord` inside the command did not produce a separately-named entry; nested `BeginUndoRecord` returns 0 and `EndUndoRecord` on it returns false; an empty record leaves nothing.
- **`RhinoApp.RunScript` from outside a command context does nothing** (returns `true`), and `RhinoApp.SendKeystrokes("_Undo", true)` typed nothing but its Enter *repeated the last command*, adding two more objects. Neither is usable from a plug-in thread.
- **`RhinoApp.ExecuteCommand(doc, "<command>")` from `InvokeOnUiThread` is the mechanism that works.** It ran the plug-in's own `ScriptRunner`-style command from the connection thread, and `ExecuteCommand(doc, "_Undo")` afterwards reverted exactly the previous command's entry, `_Redo` restored it. Verified by object *names*, not counts, across two runs: after run1, run2, `_Undo`, `_Undo`, `_Redo` the document held run1's objects only.
- A Python script that **throws after adding objects** leaves them in place and its command's undo entry intact, so rollback-on-throw is `ExecuteCommand(doc, "_Undo")` issued by the executor immediately after the command returns with a failure.

**Design consequence (PRD §06/§07):** the executor runs every script inside a connector-owned command started with `ExecuteCommand`, one command per run; that is what makes a run one undo entry and what makes the post-run `_Undo` revert exactly that run. The entry's *name* is the command's name, so the `MCP: <label>` naming from the PRD needs either a per-run command name (Rhino registers commands statically, so this is not straightforward) or a different mechanism; deferred to phase 1 as an open detail. The "verify the top entry is ours before undoing" guard cannot read entry names; phase 1 verifies via Rhino's most-recent-command list instead.

## 4. Documentation sidecars (§17 item 7 → §09)

`RhinoCommon.xml` (6.6 MB), `Grasshopper.xml` and `GH_IO.xml` all ship inside the Mac app bundle (`RhCore.framework/Resources/` and `…/ManagedPlugIns/GrasshopperPlugin.rhp/`). No SDK download or embedding is needed on macOS. Windows to confirm in phase 2.

## 5. Distribution through yak (§17 item 9, partial → §15)

`yak build` in a folder holding `manifest.yml` + the `.rhp` produced `mcpspike-0.1.0-rh8_35-any.yak`; `yak install ./<file>.yak` installed it under `~/Library/Application Support/McNeel/Rhinoceros/packages/8.0/<name>/<version>/` and wrote a `manifest.txt` version marker beside it; Rhino loaded it at the next start. A hand-made copy of that layout **without** the marker was ignored. `yak uninstall <name>` removed it. Rhino's `_-PlugInManager` on the Mac has no `_Load` option (it opens the Plug-ins window), so yak is the scriptable install path on both platforms. Package size limits for a bundled server binary remain unverified.

## 6. Driving Rhino from outside, for the harness (→ §16)

- `rhinocode script <path>` runs a script in the running Rhino on its main thread; `rhinocode command <cmd>` runs a command. Neither relays stdout — a script reports by writing a file. Both were enough to drive every spike, and they are how the harness can bootstrap before the connector's own transport exists.
- `rhinocode` requires an open document for commands; a fresh launch sits at the template chooser with none. The chooser's "New Model" button is clickable through System Events.
- Quitting with an unsaved document raises a keep/delete sheet; its `Delete` button is reachable through System Events by walking the sheet's `entire contents` (it is not addressable by name). A restart helper that does quit-discard-relaunch-new-model is in the spike scratch and should become a harness fixture.
- **A locked screen stops both GUI automation and script execution** (scripts are received by the RhinoCode server and not run). Harness runs need an unlocked session, and a wrapper should check `CGSSessionScreenIsLocked` before starting.

## Still open after this pass

Items 4 (a pre-show dialog hook), 5 (Windows single-document, re-confirmed on 8.x), 6 (unsaved-title uniquification), 8 (off-main-thread `Phase` reads during a Grasshopper solve), and the size half of 9.
