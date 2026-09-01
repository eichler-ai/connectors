# One-shot redeploy+relaunch+verify, run entirely on the VM via a SINGLE `prlctl exec` call from
# the Mac (see redeploy-and-verify.sh, the wrapper that actually invokes this). Consolidates a
# sequence that used to take five-plus separate `prlctl exec` round trips per dev-loop cycle (kill,
# copy DLLs -- often retried by hand on a file lock, launch, then several manual polls of
# connection.log, sometimes a manual broker restart on top) into one call with a single PASS/FAIL
# result. Talks to the ALREADY-RUNNING launcher agent (revit/dev-tooling/launcher-agent.ps1) via its
# own signal files rather than reimplementing close/launch here, so the graceful-close/pristine-copy
# behavior that agent already gets right (issue #26) isn't duplicated or allowed to drift out of sync.
#
# Deploy: this file is read directly off the shared folder via `-File <unc-root>\revit\...` (the
# wrapper resolves the current \\psf\ / \\Mac\ alias fresh each run -- it can flip across VM
# restarts) -- nothing to copy into place first, unlike launcher-agent.ps1 (which has to be a
# long-lived resident process, this one is not).
#
# Document-launch mode (-DocSource/-DocDest) needs nothing special any more: the add-in pushes a
# live document-snapshot refresh ("register refreshed: N document(s)" in connection.log) the moment
# a document finishes opening (issue #30's fix), and this script's registration wait accepts that
# line the same as a connect-time register. The old flow -- a STALE_REGISTRATION marker emitted here,
# with redeploy-and-verify.sh reacting by force-restarting the Mac-side broker per marker (issue
# #32's workaround) -- is deleted; this file works standalone in document mode now.

# SCOPE, stated because it has already been guessed wrong: this file deploys the ADD-IN and
# nothing else. The mcp-server broker is a separate binary that runs on the MAC, and everything it
# serves an agent -- skill.md via get_skills, the tool schemas, the tool descriptions -- is compiled
# into that binary, with no Revit instance and no add-in in the path at all. So a stale skill.md or
# a stale tool schema is NEVER fixed by redeploying from here, and a hash check here could not
# detect one either: go:embed is resolved at compile time, so a built binary's embedded copy always
# matches the source file it was built from -- the only drift possible is binary vs repo. That is
# handled where the broker is actually built, in redeploy-and-verify.sh's broker-freshness step,
# and reported at runtime by get_skills' build field and `mcp-server -version` (issue #116).
#
# Output contract: every progress line is prefixed "[redeploy +<elapsed>s]"; the very last line is
# "REDEPLOY_RESULT: PASS" or "REDEPLOY_RESULT: FAIL", and the exit code matches (0/1). PASS in a
# document launch means a registration with the expected document count was actually observed in
# connection.log -- not merely that Revit started.
param(
    # Default is only a fallback for running this file by hand -- the wrapper always passes the
    # alias it resolved (\\psf\ vs \\Mac\ can flip across VM restarts).
    [string]$SrcRoot = '\\psf\connectors\revit\mcp-bridge\src',
    [string]$Tfm = 'net10.0-windows',
    [string]$AddinsVersion = '2027',
    [string]$DocSource = '',   # pristine fixture .rvt (VM path); copied fresh over DocDest on every launch
    [string]$DocDest = '',     # working-copy .rvt actually opened; given alone, opened directly (no pristine refresh)
    [string]$Marker = '',      # if set, FAIL unless the DEPLOYED MCPBridge.Core.dll contains this string (stale-build guard)
    # 150s default, not a rounder-looking 60-90s: a live-measured cold Revit launch on this VM took
    # ~49s just to reach RunConnectionLoop (before any connection attempt at all), confirmed via
    # C:\dev\launcher-agent.log's own started-pid timestamp vs. connection.log's RunConnectionLoop
    # line -- a tight timeout here fails on nothing but Revit's own ordinary startup time.
    [int]$TimeoutSec = 150,
    [int]$MinDocuments = -1,   # -1 = auto: 1 if DocDest given, else 0 (see resolution below)
    # Alternate Revit.exe to launch, e.g. 'C:\Program Files\Autodesk\Revit 2025\Revit.exe'. Empty
    # means the launcher agent's own default (2027). The agent has always supported this as line 1 of a
    # *.launch signal; this script hardcoded '' and so could only ever relaunch 2027, which is why the
    # net8.0-windows/Revit 2025 leg had no live path at all -- it built and unit-tested on both TFMs
    # while every live run was 2027. Pair with -Tfm net8.0-windows -RevitVersion 2025.
    [string]$RevitExe = '',
    [switch]$SkipCopy,         # skip the DLL deploy -- close/relaunch/verify only
    [switch]$SkipRelaunch,     # skip close/relaunch/wait -- deploy DLLs only, no verification
    # The interactive user Revit/the add-in/the launcher agent actually run as. Deliberately NOT
    # $env:APPDATA/$env:LOCALAPPDATA (or $env:USERNAME) -- this whole script runs via `prlctl exec`,
    # which executes as NT AUTHORITY\SYSTEM (dev-environment.md's own documented gotcha), so those environment
    # variables resolve to SYSTEM's own profile, not the interactive user's -- confirmed live the
    # first time this script ran: it silently deployed DLLs into SYSTEM's Addins folder and polled
    # SYSTEM's own (nonexistent) connection.log, timing out with zero explanation. Default '' means
    # auto-detect the console-logged-on user below (Win32_ComputerSystem.UserName, readable from
    # SYSTEM); pass it explicitly if that ever guesses wrong.
    [string]$InteractiveUser = ''
)

$ErrorActionPreference = 'Stop'

if (-not $InteractiveUser) {
    $consoleUser = (Get-CimInstance Win32_ComputerSystem).UserName
    if (-not $consoleUser) {
        throw 'Could not auto-detect the console-logged-on user (no one logged on?). Pass -InteractiveUser explicitly.'
    }
    $InteractiveUser = $consoleUser -replace '^.*\\', ''  # DOMAIN\user -> user
}
$signalDir = 'C:\dev\.launcher-signals'
$userProfile = "C:\Users\$InteractiveUser"
$connectionLog = Join-Path $userProfile 'AppData\Local\Connectors\Revit\connection.log'
$addinsDir = Join-Path $userProfile "AppData\Roaming\Autodesk\Revit\Addins\$AddinsVersion"

if ($MinDocuments -lt 0) { $MinDocuments = if ($DocDest) { 1 } else { 0 } }

# Elapsed-seconds prefix on every line: phase costs stay visible at a glance (cold Revit launch
# ~50s is the expected dominant chunk of any relaunch cycle -- see the wrapper's own timing notes).
$scriptStart = Get-Date
function Say([string]$msg) { Write-Output "[redeploy +$([int]((Get-Date) - $scriptStart).TotalSeconds)s] $msg" }

# Drops a signal file the way the launcher agent's own doc comment asks callers to (its SETTLE
# GUARD section): write under a non-matching name, then rename into place, so the agent's poll loop
# can never observe a half-written file and misread empty-so-far as deliberately empty (the exact
# "launched with no document because the payload hadn't landed yet" bug issue #26 root-caused).
function Drop-Signal([string]$Extension, [string[]]$Lines) {
    $base = "claude-redeploy-$PID-$(Get-Random)"
    $tmp = Join-Path $signalDir "$base.tmp"
    $final = Join-Path $signalDir "$base.$Extension"
    if ($Lines -and $Lines.Count -gt 0) {
        Set-Content -Path $tmp -Value $Lines
    } else {
        New-Item -ItemType File -Path $tmp -Force | Out-Null
    }
    Rename-Item -Path $tmp -NewName (Split-Path $final -Leaf)
}

function Copy-WithRetry([string]$Src, [string]$Dst, [int]$MaxAttempts = 8) {
    for ($i = 1; $i -le $MaxAttempts; $i++) {
        try {
            Copy-Item -Path $Src -Destination $Dst -Force -ErrorAction Stop
            return
        } catch {
            if ($i -eq $MaxAttempts) { throw }
            # RevitWorker.exe/RevitAccelerator.exe can hold a lock on a just-closed session's DLLs
            # for a moment after the main Revit.exe process itself is gone (dev-environment.md's own
            # documented gotcha) -- retry with backoff rather than failing the whole run on a
            # transient lock.
            Start-Sleep -Milliseconds (500 * $i)
        }
    }
}

# Confirms the DLL just deployed actually contains a caller-supplied marker string, decoded at BOTH
# byte alignments (the #US heap stores UTF-16 literals at arbitrary offsets -- SKILL.md's own
# documented gotcha: a single-alignment decode misses roughly half of them). Guards against the
# "verifying you're actually debugging the binary you just built" trap: an incremental build that
# silently no-ops, or a redeploy that races a build still in flight, produces a stale DLL with a
# fresh timestamp and no other symptom.
function Test-MarkerPresent([string]$DllPath, [string]$MarkerText) {
    $bytes = [System.IO.File]::ReadAllBytes($DllPath)
    $s0 = [System.Text.Encoding]::Unicode.GetString($bytes)
    $s1 = [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
    return ($s0.Contains($MarkerText) -or $s1.Contains($MarkerText))
}

# Waits for a fresh "connected: auth+register" line, reacting to actual file-append events
# (System.IO.FileSystemWatcher + Wait-Event) instead of sleeping in a fixed-interval loop --
# notified as soon as the add-in actually writes, rather than up to one poll-interval late.
#
# DELIBERATELY NOT TIMESTAMP COMPARISON, after an earlier version of this function used one and got
# it wrong on the very first live run: comparing a wall-clock "since" value against the log line's
# own parsed UTC timestamp is two independent things that both have to be right (capturing "since"
# at the correct point, AND parsing/normalizing the log's ISO-8601-with-offset format correctly) for
# what should be a simple question -- "did a NEW matching line show up". Tracking the log file's
# byte length before this call, then inspecting only the bytes APPENDED after that point, answers
# that question directly: anything found there is unambiguously new, no clock math involved at all.
function Wait-ForLogLine([string]$LogPath, [string]$Pattern, [int]$TimeoutSec, [long]$FromOffset) {
    # READS FROM AN OFFSET, and that is the whole correctness of this function: connection.log persists
    # across launches, so a whole-file search would match the PREVIOUS session's line and report success
    # instantly, every time. The caller captures the length before dropping the launch signal.
    #
    # Deliberately NOT reusing Wait-ForFreshConnection's FileSystemWatcher machinery: this wait is short
    # and its failure is a warning rather than a FAIL, so that bookkeeping would be cost without benefit.
    # It does keep that function's hard-won discipline of performing one more content check AFTER the
    # deadline passes -- a line landing in the final beat used to be missed entirely.
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $timedOut = (Get-Date) -ge $deadline

        if (Test-Path $LogPath) {
            $length = (Get-Item $LogPath).Length
            # Rotation at 5MB (issue #11) means the file is not monotonically growing; a shrink means a
            # fresh log, so every byte in it is new.
            $offset = if ($length -lt $FromOffset) { 0 } else { $FromOffset }
            try {
                # ReadWrite share -- the add-in holds this file open for writing.
                $fs = [IO.File]::Open($LogPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                try {
                    $fs.Seek($offset, [IO.SeekOrigin]::Begin) | Out-Null
                    $reader = New-Object IO.StreamReader($fs)
                    $text = $reader.ReadToEnd()
                } finally { $fs.Dispose() }

                $hit = $text -split "`n" | Where-Object { $_ -match $Pattern } | Select-Object -Last 1
                if ($hit) { return $hit.Trim() }
            } catch {
                # A transient sharing/IO error is not an answer; keep waiting.
            }
        }

        if ($timedOut) { return $null }
        Start-Sleep -Milliseconds 500
    }
}

function Wait-ForFreshConnection([string]$LogPath, [int]$MinDocuments, [int]$TimeoutSec) {
    $startLength = 0
    if (Test-Path $LogPath) { $startLength = (Get-Item $LogPath).Length }

    $watchDir = Split-Path $LogPath -Parent
    New-Item -ItemType Directory -Force -Path $watchDir | Out-Null
    $watcher = New-Object System.IO.FileSystemWatcher($watchDir, (Split-Path $LogPath -Leaf))
    $watcher.EnableRaisingEvents = $true
    $sourceId = "RedeployLogWatch-$PID"
    Register-ObjectEvent -InputObject $watcher -EventName Changed -SourceIdentifier $sourceId | Out-Null

    try {
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        # DELIBERATELY "check content, THEN check deadline" order, not the reverse -- an earlier
        # version bailed out (`if remaining -le 0, return`) at the TOP of the loop, before ever
        # re-reading the file on that final iteration. Confirmed live: a real connect line landed
        # with ~3s to spare before a 60s timeout, and this function still reported failure, because
        # its last content check happened a beat before the write and the loop then exited on the
        # deadline check without ever looking again. Always perform one more content check after the
        # deadline has passed, and only give up after THAT comes back empty.
        while ($true) {
            $remaining = ($deadline - (Get-Date)).TotalSeconds
            $timedOut = $remaining -le 0

            # Also re-check on a bounded wait even without an event -- covers the file not existing
            # yet at all (the watcher can't watch a file that isn't there), and any change notification
            # the OS coalesces or drops (documented FileSystemWatcher behavior under bursty writes).
            if (-not $timedOut) {
                Wait-Event -SourceIdentifier $sourceId -Timeout ([Math]::Min(3, $remaining)) | Out-Null
                Get-Event -SourceIdentifier $sourceId -ErrorAction SilentlyContinue | Remove-Event -ErrorAction SilentlyContinue
            }

            if (-not (Test-Path $LogPath)) {
                if ($timedOut) { return $null }
                continue
            }
            $currentLength = (Get-Item $LogPath).Length
            # connection.log ROTATES at 5MB (issue #11), so it is no longer monotonically growing --
            # the byte-offset scheme this function is built on assumed it was. A shrink means the log
            # was renamed to .old and a fresh one started, so every byte in the current file is new:
            # restart from the top rather than waiting forever for it to grow past a length it will
            # never reach again. Without this the loop skips every iteration and the script reports
            # FAIL on a deploy that worked -- the exact opaque failure its comments exist to prevent.
            if ($currentLength -lt $startLength) { $startLength = 0 }
            if ($currentLength -le $startLength) {
                if ($timedOut) { return $null }
                continue
            }

            # FileShare.ReadWrite: the add-in process still has this file open for its own appends:
            # a plain Get-Content would be fine too, but an explicit share-mode open is the honest
            # way to say "I know another process is writing this concurrently."
            # Delete is in the share mask alongside ReadWrite because the add-in ROTATES this file
            # (issue #11) with File.Move, which needs delete-sharing on every open handle. Without it
            # this reader silently blocks the rotation it is watching for, and the log runs over its
            # cap for as long as the script is polling -- this tooling defeating the very cap it helped
            # verify.
            $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
            $stream = [System.IO.File]::Open($LogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
            try {
                $stream.Seek($startLength, [System.IO.SeekOrigin]::Begin) | Out-Null
                $reader = New-Object System.IO.StreamReader($stream)
                $newContent = $reader.ReadToEnd()
                # Advance by what was actually CONSUMED (stream position after ReadToEnd), not the
                # Get-Item length snapshotted before the read (PR #33 review finding): the log can
                # grow between that snapshot and the read completing, and advancing only to the
                # stale snapshot would re-read the overlap on the next iteration -- duplicate
                # processing of lines already judged. And if the read caught a PARTIAL last line
                # (writer mid-append), hold
                # that fragment back: advance only past the last complete line and re-read the
                # fragment next iteration, so a registration line can never be half-consumed and
                # thereby missed entirely.
                $consumed = $stream.Position
                $lastNewline = $newContent.LastIndexOf("`n")
                if ($lastNewline -lt $newContent.Length - 1) {
                    $partialTail = $newContent.Substring($lastNewline + 1)
                    $consumed -= [System.Text.Encoding]::UTF8.GetByteCount($partialTail)
                    $newContent = $newContent.Substring(0, $lastNewline + 1)
                }
                $startLength = $consumed
            } finally {
                $stream.Dispose()
            }

            foreach ($line in ($newContent -split "`r?`n")) {
                # Two line shapes satisfy the wait, both carrying a document count:
                #   connected: auth+register succeeded ... N document(s)   -- the connect-time register
                #   register refreshed: N document(s) ...                  -- the live snapshot push the
                # add-in sends on document open/close/create/activate (issue #30's fix). The second is
                # what usually completes a document-launch cycle now: the add-in connects with 0
                # documents while the .rvt is still opening, then pushes the refresh the moment the
                # open completes -- no broker restart involved. (This function once emitted a
                # STALE_REGISTRATION marker here for redeploy-and-verify.sh to react to with forced
                # Mac-broker restarts -- issue #32's workaround for the one-shot snapshot race. That
                # scaffolding is deleted, as its own comments always said it should be once the add-in
                # pushed live snapshot updates.)
                if ($line -match 'connected: auth\+register succeeded.*,\s*(?<n>\d+) document' -or
                    $line -match 'register refreshed:\s*(?<n>\d+) document') {
                    $n = [int]$Matches.n
                    if ($n -ge $MinDocuments) { return $line }
                }
            }
            if ($timedOut) { return $null }
        }
    } finally {
        Unregister-Event -SourceIdentifier $sourceId -ErrorAction SilentlyContinue
        Get-Event -SourceIdentifier $sourceId -ErrorAction SilentlyContinue | Remove-Event -ErrorAction SilentlyContinue
        $watcher.Dispose()
    }
}

if (-not $SkipRelaunch) {
    Say "closing Revit via launcher agent (graceful -- avoids stamping any open .rvt 'in use')"
    Drop-Signal -Extension 'close' -Lines @()
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Name Revit, RevitWorker, RevitAccelerator -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
    if (Get-Process -Name Revit, RevitWorker, RevitAccelerator -ErrorAction SilentlyContinue) {
        Say "WARNING: Revit-family process(es) still running 20s after the close signal; DLL copy below may hit a lock"
    }
}

if (-not $SkipCopy) {
    Say "deploying DLLs to $addinsDir (TFM=$Tfm)"
    New-Item -ItemType Directory -Force -Path $addinsDir | Out-Null
    # This list is explicit rather than a wildcard because it deploys ONTO a live Revit install and a
    # stray copy is how a shadowing DLL gets there (see caveats.md). It therefore has to be extended
    # when a project is added: Eichler.Connectors.Revit was added by issue #91 and its absence here
    # was caught only by the --marker check, which is exactly what that check is for. Without it Revit
    # loads an add-in whose Core references an assembly that is not present.
    #
    # install.ps1 needs no equivalent change: it copies the AddIn's whole build output ($payloadDir\*),
    # which already carries both files.
    foreach ($proj in 'MCPBridge.AddIn', 'MCPBridge.Core', 'MCPBridge.RevitAdapter', 'Eichler.Connectors.Revit') {
        $src = Join-Path $SrcRoot "$proj\bin\Debug\$Tfm\$proj.dll"
        if (-not (Test-Path $src)) {
            Say "FAIL: build output not found: $src -- run dotnet build first"
            Write-Output "REDEPLOY_RESULT: FAIL"
            exit 1
        }
        Copy-WithRetry -Src $src -Dst (Join-Path $addinsDir "$proj.dll")

        # The XML-doc sidecar, where one exists. LOAD-BEARING for Eichler.Connectors.Revit, not a
        # nicety: DiscoveryReflector joins each synced assembly against its sidecar and treats a
        # MISSING file as "everything is documented", so deploying the DLL without the .xml makes the
        # connector's own API discoverable with empty summaries -- which looks like working discovery
        # and is worse than none. Only that project sets GenerateDocumentationFile, so this is a no-op
        # for the others; written generically so the next one to set it is covered without a fix.
        $docSrc = Join-Path $SrcRoot "$proj\bin\Debug\$Tfm\$proj.xml"
        if (Test-Path $docSrc) {
            Copy-WithRetry -Src $docSrc -Dst (Join-Path $addinsDir "$proj.xml")
        }
    }

    if ($Marker) {
        $corePath = Join-Path $addinsDir 'MCPBridge.Core.dll'
        if (-not (Test-MarkerPresent -DllPath $corePath -MarkerText $Marker)) {
            Say "FAIL: marker '$Marker' not found in deployed $corePath -- deploy likely stale (see this function's own doc comment)"
            Write-Output "REDEPLOY_RESULT: FAIL"
            exit 1
        }
        Say "marker '$Marker' confirmed present in deployed MCPBridge.Core.dll"
    }
}

if (-not $SkipRelaunch) {
    $lines = @()
    $which = if ($RevitExe) { $RevitExe } else { 'the default Revit' }
    if ($DocSource -and $DocDest) {
        $lines = @($RevitExe, $DocSource, $DocDest)
        Say "launching $which with pristine-copy fixture doc: '$DocSource' -> '$DocDest'"
    } elseif ($DocDest) {
        $lines = @($RevitExe, $DocDest)
        Say "launching $which with document: '$DocDest'"
    } elseif ($RevitExe) {
        # A bare exe still needs the line, so the signal cannot be mistaken for "no payload".
        $lines = @($RevitExe)
        Say "launching $which with no document"
    } else {
        Say "launching $which with no document"
    }
    # Captured BEFORE the launch so the warm-line wait below cannot match a previous session's.
    $logOffsetBeforeLaunch = 0
    if (Test-Path $connectionLog) { $logOffsetBeforeLaunch = (Get-Item $connectionLog).Length }

    Drop-Signal -Extension 'launch' -Lines $lines

    Say "waiting for a fresh registration (>= $MinDocuments document(s), timeout ${TimeoutSec}s)"
    $matchedLine = Wait-ForFreshConnection -LogPath $connectionLog -MinDocuments $MinDocuments -TimeoutSec $TimeoutSec

    if ($matchedLine) {
        Say "PASS: $matchedLine"

        # REGISTRATION IS NOT READINESS. The add-in registers on its connection thread while Roslyn's
        # cold start (assembly JIT + reference-metadata load, seconds) is still running on a
        # threadpool thread, so a harness sweep started the instant this said PASS had its FIRST case
        # race that warmup and fail on a wire timeout -- three times in one session, each time read as
        # a regression and re-diagnosed from scratch. The add-in now logs when the pipeline is warm;
        # wait for it.
        #
        # A WARNING, NOT A FAILURE, when it does not appear: WarmupCompile is best-effort and silent by
        # contract, so a warmup that failed logs nothing and the first script simply pays the cold start
        # as it always did. Failing the deploy over that would turn a performance nicety into a
        # deployment gate, which it is not -- but saying nothing would put us back to guessing.
        $warmSeconds = 60
        Say "waiting for the script pipeline to warm (timeout ${warmSeconds}s)"
        $warmLine = Wait-ForLogLine -LogPath $connectionLog -Pattern 'script pipeline warm' -TimeoutSec $warmSeconds -FromOffset $logOffsetBeforeLaunch
        if ($warmLine) {
            Say "warm: $warmLine"
        } else {
            Say "WARNING: no 'script pipeline warm' line within ${warmSeconds}s. The add-in is connected and"
            Say "         usable, but the first execute_script may pay Roslyn's cold start -- if a harness"
            Say "         run's first case fails on a wire timeout, re-run it before believing it."
        }

        Write-Output "REDEPLOY_RESULT: PASS"
        exit 0
    }

    Say "FAIL: no matching registration within ${TimeoutSec}s. Last log lines:"
    Get-Content $connectionLog -Tail 5 -ErrorAction SilentlyContinue | ForEach-Object { Say "  $_" }
    Say "If a document was expected and registration shows 0, a blocking dialog (e.g. the trial splash) may be wedging Revit's idle loop -- capture the VM screen from the Mac side (prlctl capture) to check."
    Write-Output "REDEPLOY_RESULT: FAIL"
    exit 1
}

Write-Output "REDEPLOY_RESULT: PASS"
exit 0
