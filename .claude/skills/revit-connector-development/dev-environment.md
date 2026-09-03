# Dev environment: Mac host + Parallels Windows VM

This project's own dev-environment tooling and its failure modes. Not product tooling, and not
required to understand the connector — read it before driving the VM, deploying the add-in, or
diagnosing "my change didn't take". Process guidance lives in `SKILL.md`.

The shape: source and the Go broker build on the Mac; the C# add-in builds and Revit runs inside a
Parallels Windows VM, which sees the repo through a shared folder.

**The VM is Windows on ARM64, and only an ARM64 .NET is installed** (`C:\dotnet10`; no
`C:\Program Files\dotnet`). Revit and `RevitAPI.dll` are x64, running under emulation. So a .NET test
host here **cannot `Assembly.LoadFrom` any Revit assembly** — it throws "The assembly architecture is
not compatible with the current process architecture", and no TFM or `RollForward` setting changes
that. To reflect over the real `RevitAPI.dll` outside Revit, load it for *metadata only* via
`MetadataLoadContext` (see `RealRevitApiLoader` in `MCPBridge.Discovery.Tests`); discovery never
executes Revit code, so metadata is all it ever needed. Note this also rules out
`GetCustomAttribute(typeof(T))` on the reflected members — use `GetCustomAttributesData()` and match
the attribute by name.

## When something looks wrong

Environment failures here nearly all present as the same symptom — **"I changed something and nothing
changed"** — with a different cause each time. The ordered checks that separate them, along with the
other overloaded symptoms (`documents: []`, a `busy` instance, a stalled live run) live in
`caveats.md`. Go there first; the mechanics below explain *why* each cause exists.

**Take a screenshot early: `prlctl capture "<vm>" --file screen.png`, then read the PNG.** You cannot
drive this VM's UI (see below), but you can see it, and treating "can't drive" as "must work blind"
has cost hours here.

**If Revit VANISHED, our logs are not the evidence — the Windows Application log is.** A crash takes
`connection.log` down with the process, so it ends mid-sentence and says nothing about why; the add-in
never gets to write a final line. Read Revit's own crash record instead:

```powershell
Get-WinEvent -LogName Application -MaxEvents 60 |
  Where-Object { $_.ProviderName -match 'Revit|.NET Runtime|Application Error' -or $_.LevelDisplayName -eq 'Error' } |
  Select-Object TimeCreated, ProviderName, Id, LevelDisplayName, Message | Format-List
```

Run it through `prlctl exec ... powershell -Command`, which is fine here because reading the event log
needs no interactive session (unlike anything touching a window). `Application Error` carries the
faulting module, which is what distinguishes "our add-in threw" from "Revit died in its own native
code" — the distinction issue #113 turned on.

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

Signals, by file extension; content is the payload:

| Signal | Content | Effect |
|---|---|---|
| `*.close` | ignored | graceful `CloseMainWindow`, then force-kills whatever is still up |
| `*.launch` | empty, or 1–3 lines | line 1 = alternate Revit exe (optional), line 2 = pristine source `.rvt`, line 3 = working copy actually opened. With all three it re-copies 2 → 3 every launch, and **aborts** rather than opening a possibly-tainted working copy. Empty means "launch with no document" |
| `*.startbroker` | optional exe path | starts the broker inside the agent's own session |
| `*.runexe` | exe path | runs it **blocking** (`Start-Process -Wait`), draining any pending reconnect first |

`*.close` is handled before `*.launch` each tick, so a launch-then-close pair dropped moments apart
can't land in the wrong order.

- **Drop every signal file atomically** — write under a non-matching name, then rename into place. A
  create-then-write drop can be read empty, and an empty `*.launch` legitimately means "launch with
  no document", so Revit starts with no file open and `register` correctly reports `documents: []` —
  indistinguishable from a document-tracking bug. Check Revit's journal and the process command line
  to settle it.
- **It holds a stale environment snapshot.** It was started once and never re-reads the registry, so
  any env var changed afterwards is invisible to every Revit it launches. Restart the agent after any
  `MCPBRIDGE_*` change — and after changing the agent script itself, which means copying the repo's
  `launcher-agent.ps1` over `C:\dev\launcher-agent.ps1` first.
  - **How:** kill the old process, then `Start-ScheduledTask -TaskName 'MCPBridgeDevLauncherAgent'`.
    Try this first — it works cleanly most of the time — but it can leave the task sitting in
    `Queued` instead of running, so confirm the task reports `Running` **and** that a NEW pid exists.
    Task state alone is not evidence.
  - **If it doesn't take**, a foreign-context trigger into an `AtLogOn` task hasn't attached. The
    fallback is a genuine interactive trigger: ask the user to click **Run** on the task in Task
    Scheduler, or to run `powershell -File C:\dev\launcher-agent.ps1` from an already-open shell.
  - **Then verify it loaded the code you deployed** — a new pid proves nothing about which version it
    read. Drive a probe signal through (a `*.launch` naming a deliberately nonexistent exe exercises
    the parse-and-log path without starting anything) and check `C:\dev\launcher-agent.log`.
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
- **In PowerShell, anything a function writes to the output stream IS part of its return value.**
  A `Write-Output` used for progress inside a value-returning function both fails to stream and
  pollutes the returned value — live, that turned a timeout into a spurious PASS. Use
  `[Console]::Out.WriteLine` for progress from inside such a function.
- **Assume Windows PowerShell 5.1** on this VM — PowerShell 7+ cmdlets (`Join-String`) fail as
  *non-terminating* errors, silently producing empty output rather than erroring.
- **UI automation is a dead end here.** `EnumWindows`/`SendKeys` find zero top-level windows anywhere,
  even from the agent's own session; `CloseMainWindow()` returns false though the window resolves.
  Don't spend time on it — use a journal replay or ask the user for the click.

## Two Revit versions, and why only one was ever tested

Both **Revit 2025 and 2027 are installed** on this VM, and the add-in multi-targets for both
(`net8.0-windows` / `net10.0-windows`). Tier 1 runs both legs. **The live harness had never once run
against 2025**, and the reason was tooling, not choice: `deploy-and-verify.ps1` always wrote an empty
line 1 into its `*.launch` signal — the alternate-exe slot the launcher agent has always honoured — so
it could only ever relaunch 2027. `--revit-exe` now exposes it:

```sh
revit/dev-tooling/deploy-and-verify.sh --build \
  --revit-exe 'C:\Program Files\Autodesk\Revit 2025\Revit.exe' \
  --tfm net8.0-windows --revit-version 2025 --doc-dest '<a 2025-saved .rvt>'
```

Two things that bite immediately:

- **Each version needs its own fixture `.rvt`.** Revit is forward-incompatible and says so plainly:
  *"The file work.rvt was saved in a later version of Revit and cannot be retrieved in this version."*
  `C:\dev\fixtures\work.rvt` is 2027-saved, so a 2025 run needs a separate document saved by 2025 —
  and there is no way to produce one without a human, because a harness run needs a document open
  before it can create anything.
- **Revit's trial splash is NOT modal — it does not block anything, and it HIDES what does.** The
  "24 DAYS LEFT / Dig into your trial" panel floats; the idle loop keeps running behind it. It is
  large and opaque, so the real modal — a memory warning, a file-version refusal, a link-reload
  prompt — sits underneath and is invisible in a screenshot. Attributing a `pending` execution to the
  splash is a misdiagnosis that costs a whole cycle, and it was made repeatedly in one session:
  a Revit 2025 launch registering 0 documents was blamed on the splash when the actual blocker
  beneath it was *"The file work.rvt was saved in a later version of Revit and cannot be retrieved
  in this version."*

  **So do not diagnose a block from a screenshot alone.** §07's v1 window inventory — already in the
  timeout notice's `detail.windows` — enumerates every top-level window the Revit process owns
  regardless of z-order, so it lists the hidden modal the screenshot cannot show. Read that first;
  use the screenshot to confirm, not to conclude. If you only have a screenshot, move or dismiss the
  splash and look again before believing what you saw.

## Deployment

**Revit recognizes exactly two manifest locations per version, and silently ignores everything else —
no error, no dialog, `OnStartup` never runs:**

- All-users: `C:\Program Files\Autodesk\Revit\Addins\<version>\`
- Per-user: `%APPDATA%\Autodesk\Revit\Addins\<version>\` — i.e.
  `C:\Users\<user>\AppData\Roaming\Autodesk\Revit\Addins\<version>\`. Spell the real user's
  path out in full; `%APPDATA%` already includes `AppData\Roaming`, and under `prlctl exec` it
  resolves to SYSTEM's profile anyway.

`C:\ProgramData\Autodesk\Revit\Addins\<version>\` is **not** valid, despite looking plausible and
accepting copies happily. **Pick one location and always deploy there** — a stale copy in the other
wins silently. Confirm which Revit accepted via its journal, which logs a
`won't be loaded. All-users Add-in manifest files must be installed to: ...` line.

**Revit's journal is the definitive record of what Revit itself was asked to do**, and several checks
in these files depend on reading it. It lives at
`%LocalAppData%\Autodesk\Revit\Autodesk Revit <version>\Journals\journal.NNNN.txt`. Search it with
`Select-String -Pattern '<YourDoc>' journal.NNNN.txt`, and pair it with
`(Get-CimInstance Win32_Process -Filter 'ProcessId=<pid>').CommandLine` — if a document path is on
neither, Revit was never asked to open it, and no add-in debugging will explain it.

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
- **A consequence of that non-standard root: .NET global tools install fine and then refuse to start.**
  `dotnet tool install --global <x>` succeeds, but the generated `<x>.exe` apphost resolves its runtime
  from the *default* location and the registry — neither of which knows about `C:\dotnet10` — and dies
  with "You must install .NET to run this application. .NET location: Not found". The install looks
  clean, so this reads as a broken tool rather than a broken PATH. Run it with `$env:DOTNET_ROOT =
  "C:\dotnet10"` set, or invoke the tool DLL through `dotnet` directly.
- **`prlctl exec` runs as SYSTEM, so `--global` installs into
  `C:\WINDOWS\system32\config\systemprofile\.dotnet\tools`** and is not on any PATH that session
  sees. Invoke tools by full path from there, not by name — and note the interactive user's own global
  tools are a *different* set, so "it works when I run it" and "it works from an agent session" are
  separate facts here, the same way env vars are.
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
- To use a worktree for Bridge work, share it in, then tell the deploy script which share to use:
  `prlctl set <vm> --shf-host-add <name> --path <worktree-path>`, then
  `deploy-and-verify.sh --share-name <name>`. Without the flag it resolves the *main* checkout's
  share and deploys that, which the script now refuses rather than reporting a PASS for a tree you
  did not change.
- **A share name containing a backslash-escape sequence corrupts an inline `-Command`** — a share
  called `redeploy-fix` makes `\\psf\redeploy-fix`, whose `\r` is eaten, giving `\psfedeploy-fix`.
  Use `-EncodedCommand` for any path built from a variable.

## Brokers for dev

- **Prefer local mode** (`BrokerDiscoveryOptions.Local()`,
  `%LOCALAPPDATA%\Connectors\Revit\broker.json`) for VM-local iteration; it sidesteps the shared
  folder entirely. Reserve remote mode for actually testing the Mac↔VM path.
- **`-shared-root` takes the full `...\Connectors\Revit` path**, not the root — in remote mode the
  broker writes `broker.json` directly into whatever it is given, and one level too high produces an
  endless `broker discovery failed` loop with no other symptom. (This flag was `-app-data-dir`, which
  still works as a deprecated alias but now warns; `-app-data-dir` proper overrides only the broker's
  *private* root — the models cache and how-to corpus, which since the two-roots split stay on the
  broker's own platform app-data and no longer land on the shared drive.)
- **A broker started for testing needs a stdin that stays open** — it speaks MCP over stdio and exits
  ~immediately otherwise, having already written `broker.json`, so it looks started while every
  connection is refused. Use `sleep 100000 | ./mcp-server-mac ...`.
- **Only the process that WINS the singleton lock writes `broker.json`.** A stray secondary from an
  earlier backgrounded harness run can beat a fresh primary to it, so confirm the pid in
  `broker.json` belongs to the process you started, and kill strays first.
- **Never restart or kill ANY broker process while your own session's `revit` MCP tool is connected
  — including one you believe belongs to someone else.** Two separate hazards, and the second is the
  surprising one: killing the process your tool depends on breaks it directly, but killing *any
  other* primary can break it too, because your tool runs a background secondary continuously
  retrying lock acquisition — kill a different primary and your own secondary may win the race and
  become primary, breaking the client session (`method "tools/call" is invalid during session
  initialization`) even though its process never died. The race is inherent; there is no careful way
  to do it. If a restart is genuinely necessary, treat it as a deliberate step that includes
  reconnecting afterwards (`/mcp`, user-issued — Claude Code does not auto-recover this), not a quick
  aside mid-task. Restarting to force a fresh registration snapshot is obsolete anyway: the add-in
  pushes one live.
- **The connected Revit is shared, and it may currently belong to another session.** There is one VM
  and one Revit; several Claude sessions reach it through their own brokers. **`pid` is the signal**:
  `list_instances` reports it, and a `pid` differing from the one you last saw means Revit was restarted
  under you and the session is not the one you left. `connected_since` corroborates. Do **not** read
  anything into a document list of unsaved `Project1..N` — those titles come from a session-wide counter
  shared by connector-created scratch documents, a person's own New Project clicks, and any saved
  `…\Project1.rvt`, which is exactly the collision that forced a `PathName == ""` filter into skill.md's
  close recipe. A fixture path (`C:\dev\fixtures\work.rvt`) is weak corroboration at best. Coordinate by
  message before anything that can wedge or restart the instance — a modal needing a human click, or a
  `.close` signal, hits every session on it, not just yours. Read-only probes are usually fine; anything
  that pops a dialog is not.
- **In REMOTE mode, the add-in registers with whichever broker `MCPBRIDGE_SHARED_ROOT` points at —
  NOT the one you just built.** (In local mode the share is never consulted: `BuildDiscoveryOptions`
  returns `BrokerDiscoveryOptions.Local()` before reading it, and the add-in uses
  `%LOCALAPPDATA%\Connectors\Revit\broker.json` — so if you took the "prefer local mode" advice
  above, this trap cannot bite you and its remedy will do nothing.) Found running tier 2 for issue
  #117 from a worktree. The share pointed at
  `\\Mac\connectors` (the main checkout), so the add-in attached to a broker built from `main` while
  the harness, talking to the worktree's broker, reported `no Revit instance connected`. The
  dangerous shape is the near miss: had the two brokers been closer in age, the suite would have run
  green against the WRONG binary — one with no `return_value` field at all — and "tier 2 passes"
  would have meant nothing. **Killing the contending primary does not fix it**: other sessions'
  background secondaries win the lock instantly (see the singleton hazard above). Point the add-in at
  your worktree's share, restart the launcher agent so it re-reads the environment, relaunch Revit —
  and put the share back afterwards. Confirm which broker you actually reached before trusting a
  harness result: `get_skills`' `build` field names the revision.
- **`bridge-config.json` outranks `MCPBRIDGE_*` (issue #185).** The ribbon's **Broker: Local / REMOTE**
  toggle writes `%LOCALAPPDATA%\Connectors\Revit\bridge-config.json`, and once that file states a
  valid `brokerMode` the `MCPBRIDGE_BROKER_MODE` variable is ignored (`MCPBRIDGE_SHARED_ROOT` still
  serves as the remote root when the file has no `sharedRoot`) — so a launcher-agent env change that
  "doesn't take" may be a saved config pinning the mode, not the stale-environment-snapshot problem
  above. Two consequences: to move Revit between brokers, click the toggle (it reconnects at once, no
  relaunch) rather than editing the environment; and `startup-errors.log`'s
  `broker mode decided by Config|Environment|Default` line says which source won. The **Reconnect**
  button re-reads `broker.json` immediately — use it after restarting a broker instead of waiting
  out the backoff or restarting Revit. The Status window's `Broker mode:` line names the exact
  `broker.json` being read.
- **The running broker is only as current as the last time something BUILT it.** `install-mac.sh` is
  a one-time setup step, so the binary `claude mcp add` registered can predate the repo by weeks
  while serving compiled-in content — `skill.md`, the tool schemas, the descriptions — as if it were
  current (issue #116). `deploy-and-verify.sh` now rebuilds and restarts it each run; ask a broker
  which source it is, with `mcp-server -version` or `get_skills`' `build` field, rather than assuming
  it matches the checkout you are reading.
- **`go build` stamps the source revision automatically — and gets it WRONG in a git worktree.** The
  toolchain finds the repository by walking up for a `.git` *directory*, and a worktree's `.git` is a
  file: a build inside a worktree nested in another checkout holds the WORKTREE's code and is stamped
  with the ENCLOSING checkout's revision (measured: worktree at `1b0d96c`, enclosing checkout at
  `34af007`, binary reports `34af007` — and its `vcs.modified=true` described the enclosing tree's
  stray untracked file while the worktree itself was clean, so the stamp is another tree's state
  entirely). A worktree outside any checkout is stamped with nothing. `go version -m <binary>` shows
  what a binary actually carries — read it off the binary rather than trusting the build command.
  Anything that needs the right answer must pass it explicitly
  (`-ldflags "-X .../internal/buildinfo.stampedRevision=$(git -C "$REPO_ROOT" rev-parse HEAD)"`, as
  both build scripts now do).
- **Run the test harness natively on the Mac** (`-broker-mode remote -broker-bind
  -broker-app-data-dir`) rather than cross-compiling and pushing it to the VM — one bundle (a single
  `-run TestX` target) runs in seconds instead of minutes this way; the FULL suite is minutes either
  way regardless of native-vs-cross-compiled (real Revit document-lifecycle latency dominates it, see
  `revit/test-harness/README.md`'s "Fast subset" section), but native-on-Mac is still strictly faster
  for the round trip and is what makes iterating on one bundle at a time practical at all.

## Scripts

- **`revit/dev-tooling/deploy-and-verify.sh`** (Mac entry point) + **`.ps1`** (VM side): the
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
