# Dev environment: Mac host + Parallels Windows VM

This project's own dev-environment tooling and its failure modes. Not product tooling, and not
required to understand the connector — read it before driving the VM, deploying the add-in, or
diagnosing "my change didn't take". Process guidance lives in `SKILL.md`.

The shape: source and the Go broker build on the Mac; the C# add-in builds and Revit runs inside a
Parallels Windows VM, which sees the repo through a shared folder.

## The one diagnosis that matters

Almost every environment failure here presents as the same symptom — **"I changed something and
nothing changed"** — with a different cause each time. Work the preflight list in order rather than
guessing; each check is cheap and definitive.

1. **Is the source the VM sees actually your edit?** Live via the shared folder, yes. Via a local
   copy (`C:\dev\revit`), no — that is a snapshot, and a build against it succeeds silently.
2. **Did it recompile?** Build `--no-incremental`, or byte-grep the output for a string unique to
   your change.
3. **Did the deploy land, unlocked?** Kill `Revit.exe`, `RevitWorker.exe`, `RevitAccelerator.exe`
   first; `del /F` the target before copying so a lock fails loudly instead of silently no-opping.
   Byte-grep the *deployed* DLL, at both byte alignments, in *every* Revit version's Addins folder —
   they drift independently.
4. **Is an env var involved?** The process reading it must be fresh. For Revit that means relaunching
   it — but if it launches via the launcher agent, that agent must restart too (below).
5. **Read the log's own unconditional first line** rather than inferring from silence.
   `connection.log` opens with `RunConnectionLoop starting. Mode=... ConnectorRoot=...`; if that
   still shows the old values, the launcher agent is stale, not your code.

**And take a screenshot early: `prlctl capture "<vm>" --file screen.png`, then read the PNG.** You
cannot drive the VM's UI (below), but you can see it, and treating "can't drive" as "must work blind"
costs hours. Two blockers invisible in every log — a "File Opened By Another User" prompt and a
link-reload warning dialog — were obvious in one capture. Escalate as: capture → identify the exact
control → ask the user for that one click. A modal can hide *behind* another window, so if the
journal shows `ADialog::doModal start` with no dismissal, believe the journal over a clean-looking
screenshot.

## `prlctl`

`prlctl start|stop|restart <vm>` for lifecycle; `prlctl exec <vm> ...` to run commands in the guest.
(Not `prlsrvctl` — that configures the Parallels service, not a VM.)

- **`prlctl exec` runs as `NT AUTHORITY\SYSTEM`, not the interactive user.** Killing processes works;
  launching a GUI app does not (it lands in non-interactive Session 0). Don't try
  `prlctl exec -u <user>` — it prompts for a password.
  - **This also means env queries answer for the wrong account.** `GetEnvironmentVariable(...,'User')`
    via `prlctl exec` reads SYSTEM's environment, not the one Revit inherits — it will confidently
    report values that are not in play. Believe the add-in's own log instead. To set a variable for
    the interactive user, write the loaded hive: translate the account to a SID, then
    `Set-ItemProperty -Path "Registry::HKEY_USERS\<sid>\Environment" ...`. Then restart the launcher
    agent *and* Revit, in that order. A machine-scope value may also be supplying a different answer.
  - **And `$env:APPDATA` resolves to SYSTEM's profile**, so a deploy driven this way can "succeed"
    into a folder Revit never reads. Always spell the interactive user's path out in full.
- **Inline `-Command` strings get corrupted** — backslashes, `\r`, em-dashes, and nested quotes are
  all mangled, and `cmd /c` args suffer the same. For anything beyond a trivial one-liner, base64 the
  command as UTF-16LE and pass `-EncodedCommand`, or transfer a file and use `-File`.
- **There is a size ceiling past which `prlctl exec` HANGS rather than erroring** — measured fine at
  ~3000 characters, hung at ~8000. To move a file: base64 it, `split -b 2800`, one call per chunk
  (`Set-Content -NoNewline`, then `Add-Content -NoNewline`), decode on the VM, and **compare byte
  lengths on both sides**. ~2.8KB/call at ~1s/call — fine for scripts and zipped payloads, hopeless
  for a large build output.
- **A `Where-Object { $_.CommandLine -like '*x*' }` filter can match its own invoking process**,
  because the pattern appears in your own command line. Anchor on something that cannot self-match.
- **When `prlctl exec` gets slow or hangs**, check the host before suspecting the code: stuck
  `prlctl exec` processes accumulate silently (`ps aux | grep "prlctl exec"`, kill them), and
  `prl_vm_app` can be under sustained CPU pressure after long uptime. If neither resolves it, restart
  **Parallels Desktop entirely**, not just the VM — the symptom lives in the host-side processes.
  Dismiss any update dialog with "Remind Me Later"; a major upgrade is the user's decision.

## The launcher agent

`revit/dev-tooling/launcher-agent.ps1`, deployed to `C:\dev\launcher-agent.ps1`, runs continuously in
the interactive user's own logon session (an `AtLogOn` task, no impersonation) and polls
`C:\dev\.launcher-signals\` every 2s. Because it runs natively in that session, `Start-Process` from
inside it reaches the real desktop. `register-launcher-agent.ps1` recreates the task.

Signals: `*.close` (graceful `CloseMainWindow`, then force-kill stragglers), `*.launch` (optional
document path; a 3-line form copies a pristine source to a working copy per launch and aborts rather
than opening a possibly-tainted file), `*.startbroker`, `*.runexe`. `*.close` is handled before
`*.launch` each tick.

- **Drop every signal file atomically** — write under a non-matching name, then rename into place. A
  create-then-write drop can be read empty, and an empty `*.launch` legitimately means "launch with
  no document", so Revit starts with no file open and `register` correctly reports `documents: []` —
  indistinguishable from a document-tracking bug. Check Revit's journal and the process command line
  to settle it.
- **It holds a stale environment snapshot.** It was started once and never re-reads the registry, so
  any env var changed afterwards is invisible to every Revit it launches. Restart the agent after any
  `MCPBRIDGE_*` change. Then **verify it loaded the code you deployed** — a new pid proves nothing;
  drive a probe signal through and check `C:\dev\launcher-agent.log`.
- **Start the broker through `*.startbroker`, not a bare `prlctl exec`** — otherwise `broker.json`
  writes to SYSTEM's profile, invisible to the add-in, producing a connection-refused loop that looks
  exactly like a dead broker.
- **`DisallowStartIfOnBatteries` wedges the task in `Queued` forever**, reporting success. Parallels
  passes the Mac's battery state through. Check it before concluding anything about tokens:
  `Set-ScheduledTask -TaskName 'MCPBridgeDevLauncherAgent' -Settings (New-ScheduledTaskSettingsSet
  -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
  -ExecutionTimeLimit ([TimeSpan]::Zero))`.
- **Never clean up scheduled tasks with a wildcard.** A `*MCPBridge*` sweep once destroyed 13 tasks
  including the agent's own registration; the running process survived, so nothing failed visibly.
  Delete by exact name, only what you created.
- **Assume Windows PowerShell 5.1** on this VM — PowerShell 7+ cmdlets (`Join-String`) fail as
  *non-terminating* errors, silently producing empty output rather than erroring.
- **UI automation is a dead end here.** `EnumWindows`/`SendKeys` find zero top-level windows anywhere,
  even from the agent's own session; `CloseMainWindow()` returns false though the window resolves.
  Don't spend time on it — use a journal replay or ask the user for the click.

## Deployment

**Revit recognizes exactly two manifest locations per version, and silently ignores everything else —
no error, no dialog, `OnStartup` never runs:**

- All-users: `C:\Program Files\Autodesk\Revit\Addins\<version>\`
- Per-user: `%AppData%\Roaming\Autodesk\Revit\Addins\<version>\` (spell out the real user's path)

`C:\ProgramData\Autodesk\Revit\Addins\<version>\` is **not** valid, despite looking plausible and
accepting copies happily. **Pick one location and always deploy there** — a stale copy in the other
wins silently. Confirm which Revit accepted via its journal, which logs a
`won't be loaded. All-users Add-in manifest files must be installed to: ...` line.

**Never copy a DLL directly into `C:\Program Files\Autodesk\Revit <version>\`.** A same-named assembly
there shadows the real Addins-folder copy for every subsequent launch, with no indication — an
orphaned diagnostic DLL once cost multiple hours of `TypeLoadException` on every newly-added type
while dozens of correct rebuilds went nowhere. Diagnostic: log every loaded assembly matching the
project's name prefix with its `.Location` early in any `OnStartup` investigation, and compare against
the path you expect. If you must place one for a one-off, delete it in the same sitting.

**A multi-targeted build produces one output folder per TFM** — `net8.0-windows` → Addins\2025,
`net10.0-windows` → Addins\2027. Deploying the wrong leg fails silently.

**Dev-loop signing**: `MCPBridge.AddIn.csproj` auto-signs the built DLL with a local self-signed cert
so Revit stops prompting "unverified publisher" on every rebuild. One-time setup:
`powershell -ExecutionPolicy Bypass -File tools\New-DevSigningCert.ps1`. Use `LocalMachine` cert
stores, not `CurrentUser` — builds run as SYSTEM while Revit runs as the interactive user. Opt out
with `-p:MCPBridgeSignDevBuild=false`. Not the PRD §12 production signing plan.

## Assembly loading under Revit's plugin model

Revit loads the add-in via `Assembly.LoadFrom`, not a `deps.json`-driven host, so a referenced
assembly's own transitive dependencies are not reliably probed from its directory.

- **Any new `PackageReference` on `MCPBridge.Core` needs its own resolution handler** — copy
  `RoslynAssemblyIsolation`'s shape and change the name-prefix filter.
  `CopyLocalLockFileAssemblies` gets the *files* there; it does not make the CLR find them.
- **Native dependencies need a separate fix** — flatten `runtimes/<rid>/native/*.dll` into the same
  directory via an MSBuild `<Copy>` target (see `MCPBridgeFlattenSqliteNative`). A package can need
  both fixes at once, for unrelated reasons.

**A `TypeLoadException` from a method's body is only catchable by its *caller*.** The JIT verifies
every type referenced anywhere in a method — including unreached branches — before executing any of
it, so the method's own try/catch is never entered. Two consequences for probe code: inlining a probe
into a fragile caller makes that whole method fail to JIT (bypassing your handlers entirely), and
calling several probes in one unwrapped sequence means the first poisoned one aborts all of them. Wrap
each probe call individually and mark them `[MethodImpl(MethodImplOptions.NoInlining)]`.

## Toolchain

**One .NET SDK on the VM: `C:\dotnet10` (10.0.400), on machine `PATH`.** The project multi-targets
`net10.0-windows` (Revit 2027) and `net8.0-windows` (Revit 2025); the .NET 10 SDK cross-targets both.
**Do not install a .NET 8 SDK.**

- A second `dotnet.exe` on `PATH` produces either `NETSDK1045` or "no .NET SDKs were found" depending
  on order. Check `where.exe dotnet` (expect one line) and `dotnet --list-sdks` before blaming code.
- **A runtime is not an SDK, but a runtime installed to the default location is invisible to this
  SDK** — a host resolves frameworks only from its own root, and multi-level lookup was removed in
  .NET 7+. The .NET 8 `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App` folders are
  hand-copied into `C:\dotnet10\shared`, so `dotnet --list-runtimes` reports a runtime that
  Add/Remove Programs does not show, and it receives no servicing. **This is a workaround, not a
  pattern** — the clean fix is one root (move the SDK to `C:\Program Files\dotnet`).
- **`RollForward=LatestMajor` silently undoes a runtime install** by preferring the highest major even
  when the requested one is present. It is deliberately absent from these projects.
- `dotnet test` has no `--no-incremental` (that is `dotnet build`); build first, then
  `dotnet test --no-build`.

## Shared folders

`\\Mac\connectors` and `\\psf\connectors` are aliases for the repo share; **the alias itself can flip
across a Parallels restart**, and drive letters are unstable, so always reference by UNC and
re-resolve rather than hardcoding either. `robocopy` from `cmd /c` mangles a `\\psf\...` source into
`C:\psf\...`; `\\Mac\...` works.

- **The share can become inaccessible to SYSTEM specifically while the interactive session is fine.**
  Check both contexts before reaching for a workaround.
- **A wrong *mapping* presents identically to a caching problem or an alias flip.** Only
  `prlctl list <vm> --info | grep -A2 "Host Shared Folders"` distinguishes them — it has been left
  pointing at a stale worktree path before.
- A one-time `robocopy` to `C:\dev\revit` is a legitimate stopgap when SYSTEM access is broken, but it
  is a snapshot that drifts — say so out loud when you reach for it, and prefer restoring real access.
- An intermittent mid-build write failure on the share (`CS0016 ... unexpected network error`) can
  leave one TFM leg with no output while the other reports clean. `Test-Path` the per-TFM test DLL.
- To use a worktree for Bridge work, share it in:
  `prlctl set <vm> --shf-host-add <name> --path <worktree-path>`.

## Brokers for dev

- **Prefer local mode** (`BrokerDiscoveryOptions.Local()`,
  `%LOCALAPPDATA%\Connectors\Revit\broker.json`) for VM-local iteration; it sidesteps the shared
  folder entirely. Reserve remote mode for actually testing the Mac↔VM path.
- **`-app-data-dir` takes the full `...\Connectors\Revit` path**, not the root — the broker writes
  `broker.json` directly into whatever it is given, and one level too high produces an endless
  `broker discovery failed` loop with no other symptom.
- **A broker started for testing needs a stdin that stays open** — it speaks MCP over stdio and exits
  ~immediately otherwise, having already written `broker.json`, so it looks started while every
  connection is refused. Use `sleep 100000 | ./mcp-server-mac ...`.
- **Only the process that WINS the singleton lock writes `broker.json`.** A stray secondary from an
  earlier backgrounded harness run can beat a fresh primary to it, so confirm the pid in
  `broker.json` belongs to the process you started, and kill strays first.
- **Run the test harness natively on the Mac** (`-broker-mode remote -broker-bind
  -broker-app-data-dir`) rather than cross-compiling and pushing it to the VM — the same suite runs in
  seconds instead of minutes.

## Scripts

- **`revit/dev-tooling/redeploy-and-verify.sh`** (Mac entry point) + **`.ps1`** (VM side): the
  consolidated build → close → deploy → relaunch → verify cycle. Resolves the share alias fresh,
  ensures a healthy Mac broker, optionally byte-grep-verifies the deploy (`--marker`), and waits for a
  fresh `connected: auth+register` line with the expected document count.
- **`revit/install.ps1`** — installs the add-in and registers the broker with Claude's MCP client
  config. **`revit/install-mac.sh`** — Mac-side counterpart; builds the broker from source.
- **`revit/dev-tooling/`** also holds the launcher agent, its registration script, and
  `launch-revit-discovery.bat`. Dev-environment only, never shipped.
- **Use this connector's own `revit` MCP tools for Revit API research** — one `execute_script` or
  `search_functions` call beats a full build/deploy/launch cycle when the open question is a script's
  C# correctness rather than the connector's behaviour. If the tool failed to connect at session
  start, `/mcp` re-attempts it without restarting the session.
