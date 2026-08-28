# Persistent dev-loop helper: runs inside the interactive user's OWN session (started via an
# AtLogOn scheduled task -- no /it impersonation trickery involved, since this task runs AS the
# user at THEIR OWN logon, not injected from a foreign SYSTEM context). Watches a signal
# directory for drop files from SYSTEM-context automation (prlctl exec) and performs actions
# that genuinely need a real interactive desktop session -- starting Revit -- from inside its
# own already-legitimate context instead of trying to teleport a process into this session from
# outside it, which is what the previous approach (schtasks /it) did and was unreliable at.
#
# Brought into version control from C:\dev\launcher-agent.ps1 (issue #26) -- previously this
# script existed only on the VM, hand-edited and never reviewed. Deploy a change here by copying
# this file over C:\dev\launcher-agent.ps1 and restarting the agent (see the skill file's stale-
# snapshot gotcha -- a running agent never re-reads its own script).
#
# Signal files (dropped into $signalDir, deleted once handled):
#   *.launch      -- contents (optional): one line = a document path to open (Revit 2027, the
#                    default exe below, launches with no document if the file is empty); two
#                    lines = line 1 is an alternate Revit exe path (empty = default 2027), line
#                    2 is the document path (empty = none) -- lets a signal target a specific
#                    installed version (e.g. Revit 2025) for multi-version smoke testing
#                    without hardcoding a second exe path into this script permanently. THREE
#                    lines (issue #26, "pristine-fixture-copy-per-launch"): line 3 is a working-
#                    copy destination path -- line 2 is then treated as a PRISTINE source that
#                    gets copied (overwriting the destination) to line 3 before opening line 3,
#                    rather than opening line 2 directly. This is what makes a launch immune to
#                    whatever a PRIOR *.close left behind in the file it force-killed against:
#                    every launch starts from the untouched original, never the tainted copy.
#                    After a successful launch that opened a document, this agent also schedules
#                    a forced broker restart $reconnectDelaySec later (issue #26, "forced
#                    reconnect") to sidestep the one-shot register-snapshot race documented in
#                    the skill file, rather than waiting for a caller to notice `documents: []`
#                    and force one manually.
#   *.close       -- closes Revit.exe (and RevitWorker.exe/RevitAccelerator.exe). Issue #26,
#                    "graceful close instead of force-kill": tries CloseMainWindow() first (with
#                    a bounded wait) so a clean exit never stamps the .rvt "in use" the way a bare
#                    Stop-Process -Force always does; anything still alive after the wait (a
#                    worker/accelerator process, or a main window that didn't close -- e.g. a
#                    "save changes?" prompt with nothing to answer it) is still force-killed, so
#                    this signal is never left half-done. Combined with the pristine-copy-per-
#                    launch handling above, a taint left by the force-kill fallback no longer
#                    poisons the *next* launch either way -- belt and suspenders, not either/or.
#   *.startbroker -- contents (optional) are the mcp-server exe path; defaults to the dev
#                    worktree binary below. Starts it hidden, in THIS interactive session, so
#                    its broker.json lands under nicholas's profile -- the same reason Revit
#                    itself has to launch from here rather than from a bare `prlctl exec`. Issue
#                    #26, "broker-lock-race guard": after starting, this agent now reads
#                    broker.json back and confirms its `pid` field actually matches the process
#                    it just started, retrying (and killing whatever impostor won instead) if
#                    not -- see Start-BrokerAsPrimary below for why this race exists at all.
#   *.sendkeys    -- contents are a SendKeys-format string sent to the Revit window after
#                    activating it. Same session-isolation reason as the above two: SendKeys
#                    targets the foreground window of the CALLING session, so this only works
#                    from inside the real interactive session, never from a bare `prlctl exec`.
#   *.runexe      -- line 1 = exe path, line 2 (optional) = one argument string. Runs it in THIS
#                    session and waits for exit -- needed for anything (e.g. the test-harness
#                    binary) that must see the real broker.json/Revit connections under
#                    nicholas's profile, which a bare `prlctl exec` (SYSTEM) cannot. Output:
#                    C:\dev\runexe-{out,err}.log, exit code in C:\dev\runexe-exit.txt once done.

$signalDir = "C:\dev\.launcher-signals"
$revitExe = "C:\Program Files\Autodesk\Revit 2027\Revit.exe"
$defaultBrokerExe = "\\psf\connectors\revit\mcp-server\mcp-server-win.exe" # UNC, not Z: -- drive-letter mappings aren't guaranteed to exist in every session context (see the skill's "Shared-folder fragility" section)
$agentLog = "C:\dev\launcher-agent.log"
# Where local-mode `-mode local` (this agent's own default -- see $defaultBrokerExe's fixed
# '-mode','local' argument below) writes broker.json -- singleton.AppDataDir()'s Windows branch,
# %LOCALAPPDATA%\Connectors\Revit. Only used by the lock-race guard to read the file back; never
# passed to the broker itself, so a *.startbroker signal that overrides -app-data-dir (not
# currently supported by this agent, content is exe-path-only today) would need this constant
# updated to match if that ever changes.
$brokerJsonPath = Join-Path $env:LOCALAPPDATA 'Connectors\Revit\broker.json'
# Issue #26, "forced reconnect": how long after a *.launch that opened a document to force a
# broker restart. Not measured precisely -- the race this works around (the add-in's one-shot
# document snapshot beating the document actually finishing its open) was observed resolving well
# before Revit itself finishes starting, so this is deliberately generous rather than tuned tight;
# a restart that fires a little late costs nothing (the broker was already correct by then), one
# that fires too early just repeats and is caught by whatever polls `register` afterward anyway.
$reconnectDelaySec = 75
$script:pendingReconnectAt = $null
New-Item -ItemType Directory -Force -Path $signalDir | Out-Null

# Every consumed signal and every launch outcome gets a line here. This exists because the
# alternative -- a silently-swallowed Start-Process failure, which is what this script used to do --
# is indistinguishable from a dozen unrelated failure modes downstream (Revit started but the add-in
# didn't load; the add-in connected but the document didn't open; ...). A caller polling `tasklist`
# can see THAT nothing happened but never WHY. See PRD 01's observability-over-silence principle.
function Write-AgentLog([string]$message) {
    try { Add-Content -Path $agentLog -Value "$(Get-Date -Format o) $message" -ErrorAction SilentlyContinue } catch { }
}

# Issue #26, "broker-lock-race guard". The singleton lock (PRD §05) is a real OS-level exclusive
# lock -- whichever process asks for it first wins, unconditionally -- so when the intended
# primary is killed for a restart, a harness-spawned SECONDARY that's already alive and already
# retrying its own lock acquisition (mcp-server's secondary loop retries every 500ms, see
# cmd/mcp-server/main.go) can win the race against a freshly-started replacement primary, which
# has to pay full process-launch cost before it ever calls AcquireLock for the first time. The
# lock itself can't distinguish "the process I meant to become primary" from "any process that
# happened to ask first" -- that's a property this agent has to check for itself, after the fact,
# by reading broker.json back and confirming the pid it names is the one this call actually
# started. Not foolproof (a second race could in principle land between our read and our kill),
# but turns a silent wrong-primary into a logged, retried, self-correcting one instead of a
# multi-minute confusing debugging session, which is what issue #26 was actually about.
function Start-BrokerAsPrimary([string]$BrokerExe) {
    $maxAttempts = 3
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($BrokerExe)) -ErrorAction SilentlyContinue | ForEach-Object {
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                # Review finding: starting the new process immediately after this, redirected to the
                # SAME fixed log paths the just-killed one had open, can throw IOException if the
                # kernel hasn't finished tearing down its file handles yet. Give it a moment.
                Wait-Process -Id $_.Id -Timeout 5 -ErrorAction SilentlyContinue
            }
            $p = Start-Process $BrokerExe -ArgumentList '-mode', 'local' -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput 'C:\dev\broker-launch-out.log' -RedirectStandardError 'C:\dev\broker-launch-err.log'

            # Review finding: only a process that WINS AcquireLock ever writes broker.json -- a
            # secondary writes nothing at all. The original version of this loop only trusted
            # broker.json once its mtime moved past a "before" snapshot, which means it could never
            # detect the exact case this guard exists for: a stray secondary that was ALREADY
            # primary, with a broker.json that was never going to be rewritten by anyone, ours
            # included (since ours just lost the race and became a secondary too). So: read whatever
            # broker.json currently says on every poll, regardless of whether it just changed, and
            # only act on it once the pid it names is confirmed to belong to a still-LIVE process --
            # otherwise it's stale content from a primary that has since exited (including a PREVIOUS
            # attempt's own victim in this same loop), and waiting a bit longer for OUR write is the
            # right call, not reacting to a ghost pid.
            $info = $null
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 300
                if (-not (Test-Path $brokerJsonPath)) { continue }
                $candidate = $null
                try { $candidate = Get-Content $brokerJsonPath -Raw -ErrorAction Stop | ConvertFrom-Json } catch { continue }
                if (-not $candidate) { continue }
                if ($candidate.pid -eq $p.Id) { $info = $candidate; break }
                if (Get-Process -Id $candidate.pid -ErrorAction SilentlyContinue) { $info = $candidate; break }
            }

            if (-not $info) {
                Write-AgentLog "startbroker attempt $attempt`: no live primary (ours or otherwise) confirmed via broker.json within 10s; retrying"
                try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
                continue
            }
            if ($info.pid -eq $p.Id) {
                Write-AgentLog "startbroker attempt $attempt`: pid=$($p.Id) confirmed as primary via broker.json"
                return $true
            }

            # Someone else won the lock this process just lost -- almost always a stray
            # harness-spawned secondary that never got cleaned up (see the function comment).
            # Review finding: only kill the pid broker.json names if it's still alive AND looks like
            # a broker -- a bare Stop-Process on an unverified pid risks hitting an unrelated process
            # that happens to have reused it (e.g. a RevitWorker spawned by a concurrent *.launch).
            $victim = Get-Process -Id $info.pid -ErrorAction SilentlyContinue
            if ($victim -and $victim.ProcessName -like 'mcp-server*') {
                Write-AgentLog "startbroker attempt $attempt`: LOCK RACE -- started pid=$($p.Id) but broker.json names pid=$($info.pid) ($($victim.ProcessName)) as primary instead; killing both and retrying"
                Stop-Process -Id $victim.Id -Force -ErrorAction SilentlyContinue
            } else {
                Write-AgentLog "startbroker attempt $attempt`: broker.json names pid=$($info.pid) as primary but that process is gone or doesn't look like a broker (found: '$($victim.ProcessName)'); not killing it, just retrying our own start"
            }
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        } catch {
            Write-AgentLog "startbroker attempt $attempt FAILED: $($_.Exception.ToString())"
        }
    }
    Write-AgentLog "startbroker: gave up after $maxAttempts attempts without confirming a primary -- check $brokerJsonPath and running mcp-server*/test-harness.test processes by hand"
    return $false
}

while ($true) {
    # *.close is handled FIRST, before *.launch, in every iteration -- deliberately, not
    # alphabetically. Review finding: with launch handled first, a launch dropped just before a
    # close (e.g. the caller changes their mind moments after starting one) could both land in the
    # SAME iteration once the launch's settle guard has elapsed -- Revit starts, then is immediately
    # killed by the close in the same tick, re-tainting the very working copy the launch may have
    # just refreshed, while a reconnect the launch scheduled stays armed for a Revit that no longer
    # exists. A close is always a "make the world quiet" instruction; running it first means the
    # worst a same-tick launch+close can do is start Revit and immediately close it again (as
    # intended), never launch-then-kill in the wrong order relative to what the caller asked for.
    Get-ChildItem $signalDir -Filter "*.close" -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
        # Review finding: a reconnect scheduled by an earlier launch must not survive a close --
        # otherwise it fires later against a broker with no matching Revit instance to resync,
        # disrupting whatever's running BY THEN for no benefit to anything that's gone.
        $script:pendingReconnectAt = $null
        # Issue #26, "graceful close instead of force-kill": a forced Stop-Process stamps the open
        # .rvt "in use" INSIDE the file itself, which then blocks the NEXT launch against that same
        # path with a modal "File Opened By Another User" prompt -- confirmed live as the single
        # most expensive item in a dev-loop cycle. CloseMainWindow() requests an ordinary close
        # (same as clicking the window's X) and, when Revit has nothing to prompt about, exits
        # clean with no taint. Stop-Process -Force below is still the unconditional backstop for
        # whatever's left standing, so this signal always finishes with Revit gone regardless.
        #
        # CONFIRMED LIVE ON THIS VM: CloseMainWindow() returns false here even though
        # MainWindowHandle resolves to a real, non-zero handle and MainWindowTitle reads correctly
        # ("Autodesk Revit 2027 - TRIAL ... - [Home]") -- this is NOT the same "zero visible
        # windows" cause as the documented SendKeys/EnumWindows dead end (that finds no window at
        # all; this finds the window fine and still can't close it), so don't conflate the two if
        # investigating further. Only Wait-Process on a process whose CloseMainWindow() actually
        # returned true -- waiting the full timeout on one that returned false synchronously (as
        # every process does today, on this VM) is pure dead time added to every close cycle, the
        # opposite of what issue #26 is about. This still gives a real, working graceful close on
        # any OTHER environment where the return value is true (e.g. a normal interactive desktop
        # session, unlike this VM's AtLogOn session) -- it's this specific VM's session that can't
        # use it, not the mechanism itself.
        $mainProcs = Get-Process -Name 'Revit' -ErrorAction SilentlyContinue
        $acceptedClose = @()
        foreach ($proc in $mainProcs) {
            try { if ($proc.CloseMainWindow()) { $acceptedClose += $proc } } catch { }
        }
        if ($acceptedClose.Count -gt 0) {
            $acceptedClose | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
        }
        $stillRunning = Get-Process -Name 'Revit', 'RevitWorker', 'RevitAccelerator' -ErrorAction SilentlyContinue
        if ($stillRunning) {
            # Join-String is PowerShell 7+ only -- this agent runs under Windows PowerShell 5.1
            # (confirmed live: $PSVersionTable.PSVersion is 5.1.26100.9168 on this VM), where an
            # unrecognized command is a non-terminating error that yields empty output rather than
            # failing the statement, so this silently logged an empty name list instead of erroring
            # loudly. -join is a language operator, not a cmdlet, and works on every version.
            $names = ($stillRunning | ForEach-Object { $_.ProcessName } | Sort-Object -Unique) -join ', '
            Write-AgentLog "close signal: force-killing $names after graceful close left them running (or they had no window to close gracefully)"
            $stillRunning | Stop-Process -Force -ErrorAction SilentlyContinue
        } else {
            Write-AgentLog "close signal: Revit closed gracefully, no force-kill needed"
        }
    }

    Get-ChildItem $signalDir -Filter "*.launch" -ErrorAction SilentlyContinue | ForEach-Object {
        # SETTLE GUARD -- do not remove without reading this.
        #
        # A signal dropped as "create the file, then write its content" (two steps, e.g. a
        # `New-Item` followed by a `Set-Content`, or any non-atomic write from outside this
        # session) can be observed by this loop in between those steps, i.e. EMPTY. The old code
        # consumed such a file immediately, got zero lines, and fell through to the no-document
        # branch below -- silently launching Revit with NO document argument. That failure is
        # invisible from the caller's side: Revit really does start, the add-in really does load
        # and connect, and `register` really does report `documents: []` -- which reads exactly
        # like a document-tracking bug in the add-in or broker rather than a signal that lost its
        # payload before it was ever read. This cost a full live-validation session to root-cause
        # (the giveaway was Revit's own journal recording no document on its command line).
        #
        # Two defenses, deliberately both:
        #   1. Callers SHOULD drop signals atomically -- write the content under a name this
        #      filter does not match (e.g. `x.tmp`), then `Rename-Item` it to `x.launch`. Rename
        #      within a directory is atomic, so the file can never be seen half-written.
        #   2. This guard, for callers that don't: ignore a signal until it has stopped changing,
        #      so a two-step drop is read only after its second step has landed.
        #
        # The wait is TWO-TIER, and the empty tier is the whole point. An empty *.launch is genuinely
        # ambiguous -- it means both "launch with no document" (a legitimate, documented use) and
        # "the payload hasn't been written yet". A flat 1s wait was measured failing exactly this
        # way: a create-then-write drop with a 3s gap was consumed at the 1s mark with zero lines and
        # launched Revit with no document -- the original bug, reproduced by its own fix. A file with
        # content is trustworthy quickly; an EMPTY one has to sit still much longer before we believe
        # emptiness was intentional rather than a payload still in flight.
        $fileLength = $_.Length
        $ageMs = ((Get-Date) - $_.LastWriteTime).TotalMilliseconds
        $settleMs = if ($fileLength -eq 0) { 15000 } else { 1000 }
        if ($ageMs -lt $settleMs) { return }

        $lines = @(Get-Content $_.FullName -ErrorAction SilentlyContinue)
        Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
        $exePath = $revitExe
        $docPath = $null
        $pristineSource = $null
        if ($lines.Count -ge 3) {
            # Issue #26, "pristine-fixture-copy-per-launch": line 2 is an untouched source, line 3
            # is the working copy actually opened -- copied fresh every launch so a *.close taint
            # (or any other accumulated state) from a PRIOR run against the same working path can
            # never carry forward, regardless of how that prior run ended.
            if (-not [string]::IsNullOrWhiteSpace($lines[0])) { $exePath = $lines[0] }
            $pristineSource = $lines[1]
            $docPath = $lines[2]
        } elseif ($lines.Count -eq 2) {
            if (-not [string]::IsNullOrWhiteSpace($lines[0])) { $exePath = $lines[0] }
            $docPath = $lines[1]
        } elseif ($lines.Count -eq 1) {
            $docPath = $lines[0]
        }
        Write-AgentLog "launch signal '$($_.Name)': $($lines.Count) line(s); exe='$exePath'; doc='$docPath'; pristineSource='$pristineSource'"
        if ($lines.Count -eq 0) {
            # Distinguish a deliberate no-document launch (an intentionally empty signal) from the
            # lost-payload case above, which otherwise look identical in this log. Review finding:
            # this used to hardcode "15s" regardless of which settle tier was actually taken (a
            # non-empty file whose Get-Content nonetheless failed -- e.g. a transient shared-folder
            # read error -- would print a wait duration that never happened, in the one log whose
            # whole job is being trustworthy about what actually occurred), so log the real values.
            Write-AgentLog "  NOTE: signal read as empty ($($settleMs)ms settle tier, file was $fileLength byte(s) at settle time); launching with NO document. If a document WAS intended, either the drop was not atomic and its payload never landed (see the settle guard above), or Get-Content failed to read a non-empty file."
        }

        if ($pristineSource) {
            # Review finding: the original version fell through to Start-Process on ANY failure here
            # (missing source, Revit still holding a lock on $docPath, a copy error) -- silently
            # opening the STALE/tainted working copy and reproducing exactly the "File Opened By
            # Another User" stall this whole feature exists to prevent, while still reporting the
            # launch as a success. Treat every failure here as a reason to skip the launch entirely
            # instead: a launch that doesn't happen is cheaper than one that opens a tainted file.
            if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
                Write-AgentLog "  ABORT: Revit is still running -- a pristine copy over '$docPath' could collide with a lock it's still holding on that path; drop *.close first, then retry this launch"
                return
            }
            if (-not (Test-Path $pristineSource)) {
                Write-AgentLog "  ABORT: pristine source does not exist: '$pristineSource' -- not launching against a possibly-stale or missing working copy"
                return
            }
            try {
                Copy-Item $pristineSource $docPath -Force -ErrorAction Stop
                Write-AgentLog "  copied pristine '$pristineSource' -> working copy '$docPath'"
            } catch {
                Write-AgentLog "  ABORT: PRISTINE COPY FAILED: $($_.Exception.ToString()) -- not launching against whatever's left at '$docPath'"
                return
            }
        }

        try {
            if ([string]::IsNullOrWhiteSpace($docPath)) {
                $p = Start-Process $exePath -PassThru
            } else {
                if (-not (Test-Path $docPath)) { Write-AgentLog "  WARNING: document path does not exist: '$docPath'" }
                $p = Start-Process $exePath -ArgumentList "`"$docPath`"" -PassThru
                # Issue #26, "forced reconnect": only meaningful when a document was actually
                # opened -- a no-document launch has nothing for the add-in's snapshot race to get
                # wrong. Overwrites any earlier pending reconnect rather than queuing one per
                # launch; a second launch this close together supersedes the first's reconnect
                # need anyway (its own document is the one that'll actually be open by then).
                $script:pendingReconnectAt = (Get-Date).AddSeconds($reconnectDelaySec)
                Write-AgentLog "  scheduled forced broker reconnect at $($script:pendingReconnectAt.ToString('o'))"
            }
            Write-AgentLog "  started pid=$($p.Id)"
        } catch {
            Write-AgentLog "  START-PROCESS FAILED: $($_.Exception.ToString())"
        }
    }

    Get-ChildItem $signalDir -Filter "*.startbroker" -ErrorAction SilentlyContinue | ForEach-Object {
        $brokerExe = (Get-Content $_.FullName -ErrorAction SilentlyContinue | Select-Object -First 1)
        Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
        if ([string]::IsNullOrWhiteSpace($brokerExe)) { $brokerExe = $defaultBrokerExe }
        Start-BrokerAsPrimary $brokerExe | Out-Null
    }

    Get-ChildItem $signalDir -Filter "*.sendkeys" -ErrorAction SilentlyContinue | ForEach-Object {
        $keys = (Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue)
        Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
        try {
            Add-Type -AssemblyName Microsoft.VisualBasic
            Add-Type -AssemblyName System.Windows.Forms
            $proc = Get-Process -Name 'Revit' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($proc) {
                [Microsoft.VisualBasic.Interaction]::AppActivate($proc.Id)
                Start-Sleep -Milliseconds 500
                [System.Windows.Forms.SendKeys]::SendWait($keys)
            }
        } catch {
            Set-Content -Path 'C:\dev\sendkeys-exception.log' -Value $_.Exception.ToString()
        }
    }

    # *.runexe -- runs an arbitrary exe in THIS interactive session and waits for it to exit.
    # Same session-isolation reason as .launch/.startbroker: a test-harness binary spawned via a
    # bare `prlctl exec` runs as SYSTEM, which has its OWN, separate app-data profile
    # (C:\WINDOWS\system32\config\systemprofile\...) -- so it can never see the real broker.json
    # or any Revit instance connected under nicholas's own profile. Confirmed live: run this way,
    # the harness became its own isolated "primary" broker with zero connected instances instead
    # of proxying to the real one.
    #
    # Content: line 1 = exe path, line 2 (optional) = a single argument string passed as-is to
    # Start-Process -ArgumentList. Output goes to C:\dev\runexe-out.log / runexe-err.log
    # (overwritten each run -- one in flight at a time is the expected usage), and the exit code
    # to C:\dev\runexe-exit.txt once the process has actually finished, so a caller can poll for
    # that file's existence rather than guessing how long the run takes.
    Get-ChildItem $signalDir -Filter "*.runexe" -ErrorAction SilentlyContinue | ForEach-Object {
        $ageMs = ((Get-Date) - $_.LastWriteTime).TotalMilliseconds
        $settleMs = if ($_.Length -eq 0) { 15000 } else { 1000 }
        if ($ageMs -lt $settleMs) { return }

        $lines = @(Get-Content $_.FullName -ErrorAction SilentlyContinue)
        Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
        Remove-Item 'C:\dev\runexe-exit.txt' -Force -ErrorAction SilentlyContinue
        if ($lines.Count -eq 0) {
            Write-AgentLog "runexe signal '$($_.Name)' had no exe path after settle wait; ignoring."
            return
        }
        $exePath = $lines[0]
        $argString = if ($lines.Count -ge 2) { $lines[1] } else { $null }
        Write-AgentLog "runexe signal '$($_.Name)': exe='$exePath'; args='$argString'"

        # Review finding: *.runexe blocks this whole loop (Start-Process -Wait below) -- the
        # harness itself is routinely run this way, for minutes at a time. A reconnect scheduled by
        # an earlier launch and left pending would sit frozen behind that block and only fire AFTER
        # the run finishes, which is the worst possible timing: the stale-snapshot race it exists to
        # fix is exactly what would make the run's own results unreliable, and firing right after
        # just disrupts the broker at the one moment nothing needs it disrupted. Drain it now,
        # before blocking, rather than letting it arrive late or not at all.
        if ($script:pendingReconnectAt) {
            Write-AgentLog "runexe signal '$($_.Name)': draining pending broker reconnect before this blocking run so it can't fire mid-run or arrive right after"
            $script:pendingReconnectAt = $null
            Start-BrokerAsPrimary $defaultBrokerExe | Out-Null
        }
        try {
            $params = @{
                FilePath = $exePath
                Wait = $true
                PassThru = $true
                RedirectStandardOutput = 'C:\dev\runexe-out.log'
                RedirectStandardError = 'C:\dev\runexe-err.log'
            }
            if ($argString) { $params.ArgumentList = $argString }
            $p = Start-Process @params
            Set-Content -Path 'C:\dev\runexe-exit.txt' -Value $p.ExitCode
            Write-AgentLog "  runexe finished, exit=$($p.ExitCode)"
        } catch {
            Write-AgentLog "  RUNEXE FAILED: $($_.Exception.ToString())"
            Set-Content -Path 'C:\dev\runexe-exit.txt' -Value '-1'
        }
    }

    # Issue #26, "forced reconnect" -- the other half of the *.launch handling above. Checked on
    # every loop iteration (every 2s, same cadence as every signal type here) rather than blocking
    # the loop for $reconnectDelaySec, so this agent keeps handling other signals in the meantime.
    if ($script:pendingReconnectAt -and (Get-Date) -ge $script:pendingReconnectAt) {
        Write-AgentLog "forced reconnect: restarting broker $reconnectDelaySec`s after last document launch"
        $script:pendingReconnectAt = $null
        Start-BrokerAsPrimary $defaultBrokerExe | Out-Null
    }

    Start-Sleep -Seconds 2
}
