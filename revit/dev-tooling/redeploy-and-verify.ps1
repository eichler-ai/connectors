# One-shot redeploy+relaunch+verify, run entirely on the VM via a SINGLE `prlctl exec` call from
# the Mac (see redeploy-and-verify.sh, the wrapper that actually invokes this). Consolidates a
# sequence that used to take five-plus separate `prlctl exec` round trips per dev-loop cycle (kill,
# copy DLLs -- often retried by hand on a file lock, launch, then several manual polls of
# connection.log, sometimes a manual broker restart on top) into one call with a single PASS/FAIL
# result. Talks to the ALREADY-RUNNING launcher agent (revit/dev-tooling/launcher-agent.ps1) via its
# own signal files rather than reimplementing close/launch here, so the graceful-close/pristine-copy
# behavior that agent already gets right (issue #26) isn't duplicated or allowed to drift out of sync.
#
# Deploy: this file is read directly off the shared folder via `-File \\psf\connectors\...` --
# nothing to copy into place first, unlike launcher-agent.ps1 (which has to be a long-lived resident
# process, this one is not).
#
# Document-launch mode (-DocSource/-DocDest) additionally depends on the WRAPPER for the one thing
# this script cannot do from the VM side: forcing the add-in to re-register after the document
# finishes opening (the one-shot snapshot race, issue #30 -- and the VM launcher agent's own forced
# reconnect can't help either, it restarts the local-mode broker, a no-op for a remote-mode
# connection). When a fresh registration arrives short of -MinDocuments, this script emits a
# "[redeploy] STALE_REGISTRATION:" line; redeploy-and-verify.sh streams this output live and
# restarts the Mac-side broker each time it sees one, which drops the add-in's connection and
# produces a fresh registration (issue #32). Run standalone without the wrapper, document-launch
# mode will keep reporting STALE_REGISTRATION until something else forces a reconnect.

param(
    [string]$SrcRoot = '\\psf\connectors\revit\mcp-bridge\src',
    [string]$Tfm = 'net10.0-windows',
    [string]$AddinsVersion = '2027',
    [string]$DocSource = '',
    [string]$DocDest = '',
    [string]$Marker = '',
    # 150s default, not a rounder-looking 60-90s: a live-measured cold Revit launch on this VM took
    # ~49s just to reach RunConnectionLoop (before any connection attempt at all), confirmed via
    # C:\dev\launcher-agent.log's own started-pid timestamp vs. connection.log's RunConnectionLoop
    # line -- a tight timeout here fails on nothing but Revit's own ordinary startup time.
    [int]$TimeoutSec = 150,
    [int]$MinDocuments = -1,   # -1 = auto: 1 if DocDest given, else 0 (see resolution below)
    [switch]$SkipCopy,
    [switch]$SkipRelaunch,
    # The interactive user Revit/the add-in/the launcher agent actually run as. Deliberately NOT
    # $env:APPDATA/$env:LOCALAPPDATA -- this whole script runs via `prlctl exec`, which executes as
    # NT AUTHORITY\SYSTEM (SKILL.md's own documented gotcha), so those environment variables resolve
    # to SYSTEM's own profile, not nicholas's -- confirmed live the first time this script ran: it
    # silently deployed DLLs into SYSTEM's Addins folder and polled SYSTEM's own (nonexistent)
    # connection.log, timing out with zero explanation. Hardcoded to match
    # register-launcher-agent.ps1's own hardcoded user, for the same reason.
    [string]$InteractiveUser = 'nicholas'
)

$ErrorActionPreference = 'Stop'
$signalDir = 'C:\dev\.launcher-signals'
$userProfile = "C:\Users\$InteractiveUser"
$connectionLog = Join-Path $userProfile 'AppData\Local\Connectors\Revit\connection.log'
$addinsDir = Join-Path $userProfile "AppData\Roaming\Autodesk\Revit\Addins\$AddinsVersion"

if ($MinDocuments -lt 0) { $MinDocuments = if ($DocDest) { 1 } else { 0 } }

function Say([string]$msg) { Write-Output "[redeploy] $msg" }

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
            # for a moment after the main Revit.exe process itself is gone (SKILL.md's own
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
            if ($currentLength -le $startLength) {
                if ($timedOut) { return $null }
                continue
            }

            # FileShare.ReadWrite: the add-in process still has this file open for its own appends:
            # a plain Get-Content would be fine too, but an explicit share-mode open is the honest
            # way to say "I know another process is writing this concurrently."
            $stream = [System.IO.File]::Open($LogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            try {
                $stream.Seek($startLength, [System.IO.SeekOrigin]::Begin) | Out-Null
                $reader = New-Object System.IO.StreamReader($stream)
                $newContent = $reader.ReadToEnd()
            } finally {
                $stream.Dispose()
            }
            $startLength = $currentLength

            foreach ($line in ($newContent -split "`r?`n")) {
                if ($line -match 'connected: auth\+register succeeded.*,\s*(?<n>\d+) document') {
                    $n = [int]$Matches.n
                    if ($n -ge $MinDocuments) { return $line }
                    # A fresh registration arrived, but WITHOUT the expected document(s) -- the
                    # one-shot snapshot race (issue #30): the add-in connected before the document
                    # finished opening, and nothing refreshes the snapshot until the connection
                    # itself drops. This script cannot fix that from the VM side: in remote mode
                    # the add-in is connected to the MAC-side broker, which only the Mac can
                    # restart (the VM launcher agent's own forced reconnect, issue #26, restarts
                    # the local-mode broker -- a no-op for a remote-mode connection). So emit a
                    # machine-readable marker; redeploy-and-verify.sh streams this output live and
                    # restarts its broker each time it sees one, forcing the add-in to reconnect
                    # and re-register (issue #32). Harmless noise when nothing is listening.
                    #
                    # [Console]::Out, NOT Say/Write-Output: inside a function, the output stream IS
                    # the return value, so Write-Output here would (a) never reach the wrapper until
                    # the function returned -- defeating the live reaction entirely -- and (b) pollute
                    # $matchedLine at the call site, turning a timeout into a spurious PASS. Both
                    # happened on this fix's own first live run; direct console writes bypass the
                    # pipeline and stream immediately.
                    [Console]::Out.WriteLine("[redeploy] STALE_REGISTRATION: fresh registration reports $n document(s), need >= $MinDocuments -- a broker restart is required to force re-registration")
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
    foreach ($proj in 'MCPBridge.AddIn', 'MCPBridge.Core', 'MCPBridge.RevitAdapter') {
        $src = Join-Path $SrcRoot "$proj\bin\Debug\$Tfm\$proj.dll"
        if (-not (Test-Path $src)) {
            Say "FAIL: build output not found: $src -- run dotnet build first"
            Write-Output "REDEPLOY_RESULT: FAIL"
            exit 1
        }
        Copy-WithRetry -Src $src -Dst (Join-Path $addinsDir "$proj.dll")
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
    if ($DocSource -and $DocDest) {
        $lines = @('', $DocSource, $DocDest)
        Say "launching Revit with pristine-copy fixture doc: '$DocSource' -> '$DocDest'"
    } elseif ($DocDest) {
        $lines = @('', $DocDest)
        Say "launching Revit with document: '$DocDest'"
    } else {
        Say "launching Revit with no document"
    }
    Drop-Signal -Extension 'launch' -Lines $lines

    Say "waiting for a fresh registration (>= $MinDocuments document(s), timeout ${TimeoutSec}s)"
    $matchedLine = Wait-ForFreshConnection -LogPath $connectionLog -MinDocuments $MinDocuments -TimeoutSec $TimeoutSec

    if ($matchedLine) {
        Say "PASS: $matchedLine"
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
