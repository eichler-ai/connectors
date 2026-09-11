# Rhino connector — caveats, by symptom

Read the symptom that matches before theorising. Each entry says what it is likely to be, how to
tell, and what to do. Sources: the phase 1a spikes (`rhino/docs/spikes/`) and phase 1 development.

## Symptom: "I deployed and nothing changed"

Rhino loaded the previous build. Likely causes, in order:

1. **Rhino was not restarted.** yak installs into the package folder; Rhino scans it at startup only.
   `deploy-plugin.sh` restarts unless `--no-restart` was passed.
2. **The restart never happened** because the quit hung on the keep/delete sheet and the surrounding
   command chain broke, then ran the tests against the old build. Run the deploy script on its own;
   read its output; do not put it inside an `&&` chain with the tests.
3. **The `.rhp` was copied by hand** into a package folder without yak's `manifest.txt`; Rhino ignores
   it. Use `yak install`.

Tell: `MCPBridgeStatus` prints the bridge version with the source revision; `lsof -p <rhino pid> |
grep MCPBridge` shows which file is mapped.

## Symptom: `list_instances` is empty but Rhino is running

- **No document open.** A fresh Rhino sits at the template chooser; the plug-in still listens and
  registers, but with `documents: []`. Click New Model (the restart helper does).
- **The plug-in did not load.** `startup-errors.log` under the connector root has the exception.
- **Wrong app-data path on one side.** The plug-in writes `~/Library/Application Support/Connectors/
  Rhino/instances/`; the server scans the same rule from `internal/appdata`. A server started with
  `-app-data-dir` scans elsewhere by design.
- **The instance file was deleted as stale** because the server's liveness check said the pid was
  dead. It is recreated only at plug-in load; restart Rhino.
- **The screen is locked**: see below.

## Symptom: a live step produced nothing, no error, and the old output is still there

**The screen is locked.** Rhino receives script requests and does not run them; System Events does
nothing; nothing errors. `ioreg -n Root -d1 -a | grep -A1 CGSSessionScreenIsLocked`. Everything
observed during a lock is invalid; rerun after unlocking. The harness preflight fails on this.

## Symptom: `CodeLanguageNotFoundException: Can not determine language for <guid>`

Not a shebang problem. **The Python language is not loaded yet** — a fresh Rhino registers only the
built-in text/JSON/YAML/dotfile languages. Call
`RhinoCode.Languages.WaitStatusComplete(LanguageSpec.Python3)` once (≈1.75 s); the plug-in does this
at load. It worked "sometimes" during the spikes only because a `rhinocode script` run had loaded the
language first (method §13). **At plug-in load the call returns immediately with nothing loaded**
(63 ms, then this exception on the first run): the registry has not queued Python yet. The host
polls `QueryLatest(Python3)` until it is non-null, then `Status.WaitReady()`; the connection.log line
`python warm-up done in N ms` (≈2400 ms) confirms it.

## Symptom: on Windows, `python warm-up failed … Python 3 was not registered by RhinoCode within 180 s`

**Windows RhinoCode does not register Python 3 on its own** (verified 2026-09-11, Rhino 8.35 on Windows
11 ARM64 under x64 emulation; [[issue #287]]). Unlike the Mac — where the language registers ≈2.4 s
after load — on Windows a normal launch never registers Python 3, never deploys the CPython runtime
(`~/.rhinocode/py39-rh8` stays absent), and never starts the RhinoCode remote-pipe server, so
`WaitStatusComplete`/`QueryLatest` poll until the 3-minute timeout. **Engaging the ScriptEditor once**
(`_ScriptEditor`, e.g. in the launch runscript) triggers all three at once: CPython deploys, the next
`python warm-up done` follows (≈28–100 s), and `rhinocode.exe` can then see the instance. A trivial
`RhinoCode.RunScript("#! python 3\n…")` at warm-up does **not** substitute — it needs the language
already registered, so it throws "can't determine language" before deployment (ruled out live). Until
#287 lets the plug-in activate RhinoCode headlessly, the Windows harness/live setup must open the
ScriptEditor once per session. C# (Roslyn) is unaffected and warms up with no ScriptEditor.
Tell: `rhinocode list` empty while Rhino is up = the RhinoCode server is not engaged.

Two further facts for the #287 fix, found on the reruns: **engaging the ScriptEditor is not a reliable
workaround** — a *fast/cached* warm-up (≈10 s, runtime already deployed) can finish with `scriptcontext`
and the rest of Rhino's Python module path still off `sys.path`, where a *slow* first-time warm-up
(≈100 s) leaves them available; and because `PythonScriptRunner.Prefix` imports `scriptcontext`
**unconditionally**, an absent `scriptcontext` makes *every* Python run hard-fail with
`No module named 'scriptcontext'`, even a `Rhino.Geometry`-only script that never touches it. The fix
should both drive RhinoCode's full init (not just deploy the runtime) and make that preamble import
best-effort. Until then the Windows harness **skips the live-Python cases** (`skipPythonExecutionOnWindows`),
keeping only the compile/analysis-only ones.

## Symptom: on Windows the build stalls for minutes, or `yak uninstall` says "Access denied"

**Rhino is running and holding the plug-in DLLs open.** `dotnet build` then retries the copy of
`Rhino.MCPBridge.Core.dll`/`RhinoAdapter.dll`/`Eichler.Connectors.Rhino.dll` into the output for a long
time (a 1-minute build was seen taking 16), and `yak uninstall rhino-mcp-bridge` fails with "Access
denied. If Rhino is running, close it and try again." **Kill Rhino before building or reinstalling on
Windows** (`Stop-Process -Name Rhino -Force`); there is no live-reload, the package is only rescanned at
startup anyway.

## Symptom: Python tracebacks point at the wrong line, or at a `~/.rhinocode/stage/…` file

The runner prefixes two lines (shebang + `scriptcontext.doc = doc`) and RhinoCode stages the text
as a file. `PythonScriptRunner.ShiftTraceback` rewrites the script's own frames to `<script>` with
the prefix subtracted and leaves library frames alone; a syntax error arrives as an
`ExecuteException("Compile Error")` with `invalid syntax (Error CPYC01) file:///…/stage/x.y:[4:1]`
on stderr, mapped to `script-compilation-failed`. If numbers are off, the prefix line count and
`PrefixLines` have drifted apart.

## Symptom: `Undo()` returned true and nothing was reverted

**Undo is deferred to the end of the current command**, and a mid-command `Undo()` then discards the
entry without reverting (spikes §3). `RhinoDoc.UndoActive` means "an undo is pending", not "the stack
has entries". Rollback is `RhinoApp.ExecuteCommand(doc, "_Undo")` after the run's command returns.
`RhinoApp.RunScript("_Undo")` from outside a command context returns true and does nothing.

## Symptom: an extra copy of the last command's objects appeared

`RhinoApp.SendKeystrokes(..., true)` appended an Enter, and Enter on Rhino's command line **repeats the
last command**. Do not use keystroke injection; use `ExecuteCommand`.

## Symptom: `execute_script` returns `wire-call-failed` / `context deadline exceeded` for a long script

The bridge could not answer `running`: `RhinoApp.InvokeOnUiThread` **blocks the caller** until the
action completes, so a launcher that calls it on the dispatcher's thread cannot return until the
script ends. `RhinoRunLauncher` calls it from a pool thread. If this reappears, something else on the
response path is waiting on the main thread.

## Symptom: every call answers `busy` with the same old execution_id, and Rhino will not quit

A script is still running on the main thread -- most likely an earlier case's loop that outlived a
failed wire call (`max_duration_ms` is 10 minutes by default). Cancel it: any server can, even one
that did not start it (`cancel_execution` with the id from the `busy` answer). Then redeploy. And
note the deploy script's exit code is invisible behind `| grep`; run it on its own.

## Symptom: a script that calls a plain-looking API never returns (Export, Import, Print…)

It opened a **command-line options prompt or a dialog** inside the run. `Document.Export("x.obj")`
prompts for OBJ options. Escape in Rhino's window ends it (`osascript … key code 53`). Use the
non-interactive form (`Write3dmFile` with `FileWriteOptions.SuppressDialogBoxes`), and add the
member to the PRD §08 notes.

## Symptom: Rhino crashed (SIGABRT) during a harness run

Twice on 2026-09-10, same signature in `~/Library/Logs/DiagnosticReports/Rhinoceros-*.ips`: main
thread, `libpython3.9 … _Py_FatalError_TstateNULL ← PyEval_RestoreThread`, under a Python script
frame -- Rhino's embedded CPython aborting on a null thread state. Both times the harness was driving
Rhino with `rhinocode script <file.py>` (Python) interleaved with the connector's own main-thread
runs. The mechanism is not pinned down; the correlation was enough to remove every `rhinocode
script` call from the harness (the document-creation case now uses `execute_script` with the
lifecycle flag, and the history read is gone). Two full runs afterwards: no crash. If it recurs
without Python in play, that is new information -- capture the `.ips` and the RhinoCode log
(`~/.rhinocode/logs/rhinocode_<pid>`). Relevant to PR 4 (the Python host): the bridge will drive
CPython itself, on the main thread, and must never touch it from another thread.

## Symptom: `RhinoDoc.Create(null)` documents pile up and nothing closes them

On the Mac each `Create` is a tab in the merged document window. `RhinoDoc` has no `Close`
member; a nested `_Close` inside our run command is refused; `rhinocode command _-Close` and Cmd+W
did not remove them in testing. The deploy script's restart clears them. A close tool is a PR 5
question.

## Symptom: a Python script ran on the wrong thread and touched the document

`RhinoCode.RunScript` **does not marshal**: called from a background thread it runs there. Only the
main-thread executor may call the host.

## Symptom: `dotnet` not found, or the wrong SDK

Homebrew's `dotnet@8` is keg-only. `DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec` and
`/opt/homebrew/opt/dotnet@8/bin` on `PATH`. The deploy script sets both.

## Symptom: `go mod tidy` in a server module upgrades hugot / the Go directive

The shared module pins its versions to what the Revit server shipped with; a tidy against a go.mod
with no `require` lines resolves to the latest. Copy the pins first (or `go mod edit -require=…`),
then tidy. It also takes ~10 minutes cold on this network.

## Symptom: `gh pr merge` refuses a PR that touches `.github/workflows/`

The environment's `GITHUB_TOKEN` lacks the `workflow` scope. `env -u GITHUB_TOKEN gh pr merge …` uses
the keyring login, which has it (memory: `gh-workflow-scope`).

## Symptom: xunit reports the memory test failing on the Mac

`Process.PrivateMemorySize64` is 0 on macOS. Assert the working set; guard private-bytes assertions
with `OperatingSystem.IsWindows() || OperatingSystem.IsLinux()`.

## Symptom: tests are green but prove nothing (or the executed count changes between runs)

Same trap as Revit's, a different mechanism, and it happened on the second day: `dotnet test`
printed `Passed! … Total: 218` while `--list-tests` listed 289. **The test host had crashed** with
`DllNotFoundException: Unable to load shared library 'rhcommon_c'` — RhinoCommon's native core —
and the runner reported whatever had completed before the crash as a pass. The trigger was a fake
that materialised a `RhinoDoc` (`RuntimeHelpers.GetUninitializedObject`), which runs `RhinoDoc`'s
type initializer, which calls native.

Rules: tier 1 must never instantiate a RhinoCommon type whose initializer or constructor calls
native — `RhinoDoc`, `RhinoObject`, anything under `Rhino.DocObjects`. Core's execution seam passes
the document as an opaque `RunDocument.Raw` (null in tests) for exactly this reason. Geometry value
types (`Point3d`, `Sphere`) are pure managed and safe. CI compares the trx executed count with the
`--list-tests` count and fails on a shortfall; run the same check locally when a count looks odd:

```
dotnet test --list-tests | grep -c '^\s*Rhino\.MCPBridge\.Core\.Tests\.'   # discovered
dotnet test --logger trx …                                          # trx total must match
```

Also: every platform-conditional test must assert something on both branches; a leg that returns
early is a vacuous pass on the platform the matrix exists for.

## Techniques index

- Read Rhino state from a script that writes JSON; read the file after the last step.
- Name objects per run and compare names, not counts.
- `screencapture -x` after activating Rhino to see what automation is looking at.
- Walk a sheet's `entire contents` by index to find a button System Events cannot name.
- `rhinocode command <cmd>` to run a plug-in command from a shell; `rhinocode script` for a probe.
- Reflection-dump an undocumented API (`Rhino.Runtime.Code`) to a file from a spike command before
  designing against it; the spike plug-in source shows how.
- Once the C# host runs, reflect from the connector itself: a throwaway harness case that sends a
  `PROBE_SCRIPT` file as `csharp` and logs the result is faster than a spike plug-in
  (`typeof(X).GetConstructors()` found `RunContext`'s optional-parameter constructor this way).

## Every script stays `pending`; capture_view answers `instance-busy` forever

Rhino is sitting inside an interactive command (a `Trim` prompt was found once after a restart,
probably from a stray click on the toolbar). The launcher retries while `Command.InCommand` is
true, so the run never starts and everything queued behind it reports busy. Screenshot Rhino
(`screencapture -x` after activating it) and look at the command line; press Escape twice via
System Events (`key code 53`) and re-run.

## `TypeLoadException: Could not load type 'System.Drawing.Imaging.ImageCodecInfo'`

Rhino's System.Drawing.Common on the Mac is a shim without `ImageCodecInfo` /
`EncoderParameters`, and tier 1 cannot catch it (the NuGet reference assembly has the type).
Use `Bitmap.Save(Stream, ImageFormat)` only; JPEG quality is the encoder's default.

## `Command.LastCommandId == MCPBridgeRun` does not mean the top undo entry is ours

A read-only run of ours (the harness's `objectNames`, any `return doc.Objects.Count`) also runs the
`MCPBridgeRun` command and leaves it as Rhino's last command, while adding no undo entry. So "last
command was ours" is fine for the executor's immediate rollback (nothing else can have run in that
window) but useless for the undo tool minutes later. The tool's gate is the `ChangeClock` instead:
the adapter reports every document change on every document, the executors mark their own work so
their changes are not counted as foreign, and the ledger keeps the last run that CHANGED each document
(a read-only run since does not replace it). A change staged from INSIDE one of our runs, even via a
nested `RhinoApp.RunScript`, is connector work; to stage a person's change in a test use
`rhinocode command "_Point 5,5,0"`.

## `list_instances` says `busy` (or `idle`) when the plug-in disagrees

The register snapshot used to capture the execution state when it was built (on a document event,
often mid-run) and freeze it. The state is now computed when the message is sent, and every ping
carries `execution_state`; the registry keeps the last non-empty value. After a grace-period expiry
the server learns `unrecoverable` on the next ping, a few seconds later -- poll, do not assert once.

## A wedged Rhino ignores the restart helper's quit

The helper's polite `quit` AppleEvent needs the main thread. After the destructive harness case (or
any non-cooperating script) `kill -9 <pid>` first, then run the helper to relaunch; the instance file
of the dead pid is deleted by the next server scan.
