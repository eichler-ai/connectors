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
language first (method §13).

## Symptom: `Undo()` returned true and nothing was reverted

**Undo is deferred to the end of the current command**, and a mid-command `Undo()` then discards the
entry without reverting (spikes §3). `RhinoDoc.UndoActive` means "an undo is pending", not "the stack
has entries". Rollback is `RhinoApp.ExecuteCommand(doc, "_Undo")` after the run's command returns.
`RhinoApp.RunScript("_Undo")` from outside a command context returns true and does nothing.

## Symptom: an extra copy of the last command's objects appeared

`RhinoApp.SendKeystrokes(..., true)` appended an Enter, and Enter on Rhino's command line **repeats the
last command**. Do not use keystroke injection; use `ExecuteCommand`.

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

## Symptom: tests are green but prove nothing

Same trap as Revit's, different mechanism. Here the C# test assembly always loads (RhinoCommon is
managed), so the risk is a test that *skips* on the platform it ran on — every platform-conditional
test must assert something on both branches, and CI asserts the executed count from the trx.

## Techniques index

- Read Rhino state from a script that writes JSON; read the file after the last step.
- Name objects per run and compare names, not counts.
- `screencapture -x` after activating Rhino to see what automation is looking at.
- Walk a sheet's `entire contents` by index to find a button System Events cannot name.
- `rhinocode command <cmd>` to run a plug-in command from a shell; `rhinocode script` for a probe.
- Reflection-dump an undocumented API (`Rhino.Runtime.Code`) to a file from a spike command before
  designing against it; the spike plug-in source shows how.
