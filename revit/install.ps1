# Installs, updates, or removes the Revit MCP Bridge add-in + MCP Server broker (PRD §12).
#
# Deliberately a script, not a packaged GUI installer -- see PRD §12 "Installation UX" for the full
# reasoning. The short version: the add-in is a single signed, compiled DLL (no pyRevit-style
# git-pull-your-extensions update path exists for it), so a GUI wizard buys nothing a script doesn't
# already do, and this script's self-upgrade path is the SAME code path as first install (this file,
# re-invoked with -Update -Silent from the ribbon's "Update available" click) rather than a second
# thing to build and keep in sync.
#
# Idempotent by construction (PRD §12 "Self-upgrade"): re-running with nothing to do costs one GitHub
# API call and a version-string comparison, nothing else. Three outcomes, not two -- see the
# version-check block below for why "the marker says current" alone is not enough to skip work.
#
# Primary invocation is piped (irm .../install.ps1 | iex, PRD §12) -- $PSCommandPath is empty in that
# mode (no file on disk), so this script never references it directly; see $ScriptPath below.

param(
    [ValidateSet('User', 'AllUsers')]
    [string]$Scope = 'User',

    # Informational only for messaging purposes -- the idempotency check below behaves identically
    # whether or not this is passed; a plain re-run and an explicit -Update both just do the right
    # thing based on what's actually installed vs. actually latest.
    [switch]$Update,

    [switch]$Uninstall,

    # The ribbon's self-update click passes this: no interactive prompts (a running Revit is closed
    # automatically rather than asked about), no "already up to date" chatter on the common no-op case.
    [switch]$Silent,

    # Internal -- invoked by the scheduled task a deferred update registers (see "Deferred updates"
    # below), never by a user directly. Checks whether any Revit version with a staged update has
    # since closed, and if so applies it; re-arms itself (by leaving the task running) otherwise.
    [switch]$ApplyPendingUpdate,

    # Testing/offline escape hatch: PRD §12's own "Known gap" -- there is no release pipeline yet
    # producing real signed GitHub Release artifacts this script can download. This lets the deploy
    # mechanics (path resolution, per-version detection, idempotency, registry writes, MCP
    # registration) be exercised live against a real Revit install NOW, against a hand-built local zip
    # matching the same expected layout, without waiting on that pipeline. Production installs never
    # pass this -- its presence is itself a signal this is a dev/test invocation.
    [string]$LocalPackagePath
)

$ErrorActionPreference = 'Stop'

$RepoSlug = 'eichler-ai/connectors'

# Review finding: $PSCommandPath is empty/null under the script's own PRIMARY documented invocation
# (irm .../install.ps1 | iex, PRD §12) -- there's no file on disk to point at. Every downstream use
# of "this script's own path" (elevation re-invoke, the self-copy used for the uninstall string and
# for the deferred-update watcher task) goes through $ScriptPath instead, which is always a real
# file: materialize our own source to one when piped, since $MyInvocation.MyCommand.Definition inside
# an iex'd scriptblock still holds the literal source text even though $PSCommandPath doesn't.
# $BootstrapCreated tracks whether we own that temp file, so it gets cleaned up on exit either way --
# review finding: a fixed, never-cleaned-up bootstrap filename both accumulates in %TEMP% forever and
# risks two concurrent piped invocations clobbering each other mid-read; GUID-suffixed avoids the
# second, the top-level try/finally below (wrapping everything after this point) avoids the first.
$ScriptPath = $PSCommandPath
$BootstrapCreated = $false
if (-not $ScriptPath) {
    $ScriptPath = Join-Path $env:TEMP "mcpbridge-install-bootstrap-$([guid]::NewGuid()).ps1"
    $MyInvocation.MyCommand.Definition | Out-File $ScriptPath -Encoding utf8 -Force
    $BootstrapCreated = $true
}

# Only 2027 ships a build today (PRD §11: it's the only version with a verified .NET requirement,
# net10.0-windows). The detection/deploy loop below is written to cover every year in this list, not
# just the first one, specifically so adding 2025/2026 (Phase 6, PRD §15) is "add a year + a matching
# addin-<year>/ build to the release payload," not a rewrite of this script.
$SupportedRevitVersions = @('2027')

function Get-AddinsDir([string]$RevitVersion, [string]$InstallScope) {
    if ($InstallScope -eq 'User') {
        return "$env:AppData\Autodesk\Revit\Addins\$RevitVersion"
    }
    # NOT C:\ProgramData\... -- that path looks like a plausible all-users location and silently
    # fails (Revit's AddInLoader doesn't recognize it at all, no error). See the
    # revit-connector-development skill's own hard-won note on this exact mistake.
    return "C:\Program Files\Autodesk\Revit\Addins\$RevitVersion"
}

function Get-AppDir([string]$InstallScope) {
    if ($InstallScope -eq 'User') {
        return "$env:LocalAppData\Programs\MCPBridge"
    }
    return 'C:\Program Files\MCPBridge'
}

function Get-InstalledRevitVersions {
    # Detect every installed version, not just the first found -- PRD §12 "Multi-version installs":
    # deploy to everything detected that we also ship a build for, no "which version?" prompt.
    $SupportedRevitVersions | Where-Object {
        Test-Path "C:\Program Files\Autodesk\Revit $_\Revit.exe"
    }
}

# Review finding: the original per-process-name check couldn't tell 2026's Revit.exe apart from
# 2027's, so "is Revit running" force-closed EVERY installed version whenever ANY one of them
# triggered an update -- directly contradicting PRD §12's "must not force-close every open Revit
# across every installed version" requirement. Match on the process's own exe path instead, which
# does encode the version (it's literally the per-version install directory). Review finding: .Path
# resolves via MainModule and can throw for a bitness-mismatched process -- guarded per-item so one
# inaccessible process doesn't abort the whole check for every other running Revit.
function Get-RevitProcess([string]$RevitVersion) {
    Get-Process -Name 'Revit' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -like "*\Revit $RevitVersion\Revit.exe" } catch { $false }
    }
}

function Stop-RevitProcessGracefully($Process) {
    $Process | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    $Process | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
}

# --- Deferred updates -------------------------------------------------------------------------------
# When a running Revit version's update is deferred (user declined to close it now, or it's simply
# still open), the update it would have received is staged to $appDir\pending-update\addin-<version>\
# and a small repeating Scheduled Task is registered to watch for that specific version's Revit.exe
# actually exiting -- applying the staged files (and un-registering itself) the moment it does, rather
# than requiring the user to remember to re-run this script by hand. This is what makes "it'll finish
# updating once you close it" (the message shown when deferring) an actually-true claim rather than
# an aspirational one -- see PRD §12 "Self-upgrade" and this PR's own review history for why this
# needed to be a real mechanism, not just corrected copy.
function Get-PendingUpdateDir([string]$InstallScope) { Join-Path (Get-AppDir $InstallScope) 'pending-update' }
function Get-PendingUpdateManifestPath([string]$InstallScope) { Join-Path (Get-PendingUpdateDir $InstallScope) 'manifest.json' }
function Get-PendingUpdateTaskName([string]$InstallScope) { "MCPBridge-PendingUpdate-$InstallScope" }

function Register-PendingUpdateWatcher([string]$InstallScope, [string]$SelfPath, [int[]]$RevitProcessIds) {
    # Immediate path: block on the specific running process(es) actually exiting -- event-driven via
    # Wait-Process (Process.WaitForExit under the hood), not polling -- so the update applies the
    # instant Revit closes, not up to N minutes later. Considered and rejected: a periodic-poll
    # Scheduled Task (simple, but adds real latency for no benefit -- Wait-Process is the established
    # pattern precisely because Windows already tells you the moment a process exits, no need to ask
    # repeatedly) and MOVEFILE_DELAY_UNTIL_REBOOT (the mechanism Windows Update/most installers use
    # for locked files -- doesn't fit: it stages a swap for the next *system reboot*, not "next time
    # this one process closes," which is both too coarse and unnecessary here -- Revit itself is the
    # only thing holding the lock, so the file is already free the moment Revit exits).
    if ($RevitProcessIds.Count -gt 0) {
        $idList = $RevitProcessIds -join ','
        # Found via live testing, not just review: this call originally omitted -ExecutionPolicy
        # Bypass (unlike the Scheduled Task action below, which already had it) -- on any machine with
        # the default Restricted/AllSigned execution policy (most end-user machines, not just dev
        # boxes with Bypass already configured), the background watcher would fail immediately with
        # "running scripts is disabled on this system," silently never applying the deferred update.
        Start-Process powershell -WindowStyle Hidden -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
            "Wait-Process -Id $idList -ErrorAction SilentlyContinue; & `"$SelfPath`" -ApplyPendingUpdate -Scope $InstallScope"
        ) | Out-Null
    }

    # Durability fallback: the background waiter above dies if the machine reboots or the user logs
    # off before Revit closes. A one-shot-per-logon Scheduled Task re-checks at every subsequent
    # logon -- ApplyPendingUpdate is itself idempotent (a no-op if nothing's pending or Revit's still
    # running), so it's safe to leave this registered and firing at every logon indefinitely.
    $taskName = Get-PendingUpdateTaskName $InstallScope
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { return }
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$SelfPath`" -ApplyPendingUpdate -Scope $InstallScope"
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $principal = if ($InstallScope -eq 'AllUsers') {
        New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    } else {
        New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
    }
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
}

# Only copies $ScriptPath onto $selfCopyPath when they aren't already the same file -- review
# finding: when this script IS the deployed copy re-invoking itself (the ribbon's -Update -Silent
# self-update path, or this watcher task), $PSCommandPath and $selfCopyPath resolve to the identical
# file, and Copy-Item onto itself is a terminating error under $ErrorActionPreference = 'Stop',
# silently aborting everything after it (MCP re-registration, the registry version bump, relaunching
# Revit) while still having already deployed the new files and written the version marker.
function Copy-SelfIfNeeded([string]$Source, [string]$Destination) {
    $resolvedSource = (Resolve-Path $Source).Path
    $resolvedDest = if (Test-Path $Destination) { (Resolve-Path $Destination).Path } else { $null }
    if ($resolvedSource -ne $resolvedDest) {
        Copy-Item $Source $Destination -Force
    }
}

try {

# --- AllUsers self-elevation ----------------------------------------------------------------------
# Re-invoke elevated rather than failing outright -- AllUsers is an explicit opt-in choice, not the
# default, so a user who asked for it should get a UAC prompt, not a cryptic access-denied from
# New-Item further down.
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($Scope -eq 'AllUsers' -and -not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Review finding: every forwarded argument is individually quoted, not just the script path --
    # an unquoted -LocalPackagePath containing spaces would otherwise split across arguments in the
    # elevated child (Start-Process's -ArgumentList does not itself re-quote array elements safely
    # across all PowerShell/Windows versions).
    $forwardArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$ScriptPath`"", '-Scope', 'AllUsers')
    if ($Update) { $forwardArgs += '-Update' }
    if ($Uninstall) { $forwardArgs += '-Uninstall' }
    if ($Silent) { $forwardArgs += '-Silent' }
    if ($ApplyPendingUpdate) { $forwardArgs += '-ApplyPendingUpdate' }
    if ($LocalPackagePath) { $forwardArgs += @('-LocalPackagePath', "`"$LocalPackagePath`"") }

    # Review finding: Start-Process does not set $LASTEXITCODE (that only reflects a native-exe
    # invocation via the call operator) -- the prior version's `exit $LASTEXITCODE` propagated a
    # stale/unrelated code, so a caller checking this process's exit status (e.g. the ribbon's silent
    # self-update) could never actually detect an elevated install failure. -PassThru + .ExitCode is
    # the real result.
    $elevated = Start-Process powershell -Verb RunAs -ArgumentList $forwardArgs -Wait -PassThru
    exit $elevated.ExitCode
}

$appDir = Get-AppDir $Scope
$versionMarkerPath = Join-Path $appDir 'installed-version.json'
$selfCopyPath = Join-Path $appDir 'install.ps1'
$uninstallKeyPath = if ($Scope -eq 'User') {
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MCPBridge'
} else {
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MCPBridge'
}

# --- Apply a previously-deferred update, if this is the watcher task invoking us ------------------
if ($ApplyPendingUpdate) {
    $manifestPath = Get-PendingUpdateManifestPath $Scope
    if (-not (Test-Path $manifestPath)) {
        Unregister-ScheduledTask -TaskName (Get-PendingUpdateTaskName $Scope) -Confirm:$false -ErrorAction SilentlyContinue
        return
    }
    $manifest = Get-Content $manifestPath | ConvertFrom-Json
    $stillPending = @()
    foreach ($version in $manifest.versions) {
        if (Get-RevitProcess $version) { $stillPending += $version; continue }
        $payloadDir = Join-Path (Get-PendingUpdateDir $Scope) "addin-$version"
        if (Test-Path $payloadDir) {
            $dir = Get-AddinsDir $version $Scope
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            Copy-Item "$payloadDir\*" $dir -Force -Recurse
            Remove-Item $payloadDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if ($stillPending.Count -gt 0) {
        @{ version = $manifest.version; versions = $stillPending } | ConvertTo-Json | Out-File $manifestPath -Encoding utf8
    } else {
        @{ version = $manifest.version } | ConvertTo-Json | Out-File $versionMarkerPath -Encoding utf8
        Remove-Item (Get-PendingUpdateDir $Scope) -Recurse -Force -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName (Get-PendingUpdateTaskName $Scope) -Confirm:$false -ErrorAction SilentlyContinue
    }
    return
}

# --- Uninstall ------------------------------------------------------------------------------------
if ($Uninstall) {
    # Review finding: the original version deleted files unconditionally and reported "uninstalled"
    # even when a running Revit had one of them locked, silently leaving it behind. Same
    # stop-if-running treatment Install gives each version, not a separate weaker path.
    $leftoverVersions = @()
    foreach ($version in $SupportedRevitVersions) {
        $proc = Get-RevitProcess $version
        if ($proc) {
            if ($Silent) {
                Stop-RevitProcessGracefully $proc
            } else {
                $answer = Read-Host "Revit $version is running and must close to fully uninstall. Close it now? [Y/n]"
                if ($answer -ne 'n') { Stop-RevitProcessGracefully $proc }
            }
        }
        $dir = Get-AddinsDir $version $Scope
        Remove-Item "$dir\MCPBridge.*" -Force -ErrorAction SilentlyContinue
        Remove-Item "$dir\Microsoft.CodeAnalysis*.dll" -Force -ErrorAction SilentlyContinue
        if (Test-Path (Join-Path $dir 'MCPBridge.AddIn.dll')) { $leftoverVersions += $version }
    }
    Unregister-ScheduledTask -TaskName (Get-PendingUpdateTaskName $Scope) -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item $appDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $uninstallKeyPath -Recurse -Force -ErrorAction SilentlyContinue
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        & claude mcp remove revit 2>$null | Out-Null
    }
    if ($leftoverVersions.Count -gt 0) {
        Write-Host "Revit MCP Bridge uninstalled. Revit $($leftoverVersions -join ', ') still has some files locked -- close it and re-run this uninstaller to finish cleaning up."
    } else {
        Write-Host 'Revit MCP Bridge uninstalled.'
    }
    return
}

# --- Confirm there's a supported Revit install before doing anything else, including hitting the
# network -- review finding: the original order wasted a GitHub API call on the most common
# "ran this on the wrong machine" error case. --------------------------------------------------------
$detectedVersions = @(Get-InstalledRevitVersions)
if ($detectedVersions.Count -eq 0) {
    throw "No supported Revit version found on this machine (checked for: $($SupportedRevitVersions -join ', ')). Install Revit first, then re-run this installer."
}

if (-not $Silent) { Write-Host 'Installing Revit MCP Bridge -- checking for the latest release...' }

# --- Resolve target release ------------------------------------------------------------------------
if ($LocalPackagePath) {
    # Test/dev path: no real tag to compare against, so treat every invocation as "an update is
    # available" -- the whole point of this escape hatch is exercising the deploy path, not the
    # version-comparison logic (which has nothing to reflect a local file's "version" against).
    $releaseTag = "local-$(Get-Date -Format 'yyyyMMddHHmmss')"
    $zipPath = $LocalPackagePath
} else {
    $release = Invoke-RestMethod "https://api.github.com/repos/$RepoSlug/releases/latest"
    $releaseTag = $release.tag_name
}

# --- Idempotency check -----------------------------------------------------------------------------
# Trust the ACTUAL deployed files, not just the version marker. A marker claiming "current" while a
# DLL is missing (deleted by hand, a failed prior run, AV quarantine, whatever) must trigger a
# repair, not a silent no-op that leaves a broken install unfixed forever -- see PRD §12
# "Self-upgrade" for the three-outcome reasoning this implements.
$installed = if (Test-Path $versionMarkerPath) { (Get-Content $versionMarkerPath | ConvertFrom-Json).version } else { $null }
$allDllsPresent = -not ($detectedVersions | Where-Object {
    -not (Test-Path (Join-Path (Get-AddinsDir $_ $Scope) 'MCPBridge.AddIn.dll'))
})

if (-not $LocalPackagePath -and $installed -eq $releaseTag -and $allDllsPresent) {
    if (-not $Silent) { Write-Host "Revit MCP Bridge is already up to date ($installed)." }
    return
}
if ($installed -eq $releaseTag -and -not $allDllsPresent -and -not $LocalPackagePath) {
    Write-Host "Found a previous install ($installed), but some files are missing -- repairing it now."
}

# --- Download, verify, extract, deploy -- one try/finally so a failure anywhere in here still cleans
# up temp state, instead of leaving a downloaded zip and/or an extracted temp dir behind forever
# (review finding: the original version only cleaned up on the success path). ------------------------
$zipDownloaded = $false
$extractDir = $null
$deployedVersions = @()
$deferredVersions = @()
$deferredProcessIds = @()
try {
    if (-not $LocalPackagePath) {
        $asset = $release.assets | Where-Object name -eq 'mcpbridge-release.zip'
        if (-not $asset) { throw "The latest release ($releaseTag) is missing its installer package. This is a release-publishing problem, not something on your machine -- please contact support or try again later." }
        $zipPath = Join-Path $env:TEMP $asset.name
        Invoke-WebRequest $asset.browser_download_url -OutFile $zipPath
        $zipDownloaded = $true

        $checksumsAsset = $release.assets | Where-Object name -eq 'checksums.txt'
        if (-not $checksumsAsset) { throw "The latest release ($releaseTag) is missing its checksum file, so the download can't be verified. Installation stopped for your safety -- please contact support." }
        $checksums = (Invoke-RestMethod $checksumsAsset.browser_download_url) -split "`n"
        $expectedLine = $checksums | Where-Object { $_ -match [regex]::Escape($asset.name) }
        if (-not $expectedLine) { throw "The checksum file for this release doesn't cover the downloaded installer package. Installation stopped for your safety -- this looks like a release-publishing problem; please contact support." }
        $expectedSha256 = ($expectedLine -split '\s+')[0]
        $actualSha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash
        if ($actualSha256 -ne $expectedSha256) {
            throw "The downloaded installer failed a security check and may be corrupt. Installation stopped for your safety -- try running this installer again; if it keeps happening, contact support. (expected $expectedSha256, got $actualSha256)"
        }
    }

    $extractDir = Join-Path $env:TEMP "mcpbridge-extract-$([guid]::NewGuid())"
    Expand-Archive $zipPath -DestinationPath $extractDir -Force

    # --- Deploy, per detected+supported version, only closing/redeploying versions actually running.
    # PRD §12 "Multi-version installs": don't force-close every open Revit across every installed
    # version just because one of them triggered this -- only touch a version whose Revit isn't
    # running, OR whose running instance the user (or -Silent) explicitly agreed to close now. A
    # version left running gets its update staged and finishes automatically once it's closed (see
    # "Deferred updates" above), not left to sit as a broken promise.
    $skippedVersions = @()
    foreach ($version in $detectedVersions) {
        $payloadDir = Join-Path $extractDir "addin-$version"
        if (-not (Test-Path $payloadDir)) {
            $skippedVersions += $version
            continue
        }

        $proc = Get-RevitProcess $version
        if ($proc) {
            if ($Silent) {
                Stop-RevitProcessGracefully $proc
            } else {
                $answer = Read-Host "Revit $version is running and must close to update it. Close it now? [Y/n]"
                if ($answer -eq 'n') {
                    $pendingDir = Join-Path (Get-PendingUpdateDir $Scope) "addin-$version"
                    New-Item -ItemType Directory -Force -Path $pendingDir | Out-Null
                    Copy-Item "$payloadDir\*" $pendingDir -Force -Recurse
                    Write-Host "Revit $version is still running -- it'll finish updating automatically as soon as you close it."
                    $deferredVersions += $version
                    $deferredProcessIds += @($proc | ForEach-Object { $_.Id })
                    continue
                }
                Stop-RevitProcessGracefully $proc
            }
        }

        $dir = Get-AddinsDir $version $Scope
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Copy-Item "$payloadDir\*" $dir -Force -Recurse
        $deployedVersions += $version
    }
    if ($skippedVersions.Count -gt 0) {
        Write-Host "This release doesn't include a build for Revit $($skippedVersions -join ', ') -- skipping it."
    }
    if ($deployedVersions.Count -eq 0 -and $deferredVersions.Count -eq 0) {
        throw "Found Revit $($detectedVersions -join ', ') on this machine, but this release doesn't support any of them yet. Check for a newer release or contact support."
    }

    New-Item -ItemType Directory -Force -Path $appDir | Out-Null
    $serverPayloadDir = Join-Path $extractDir 'server'
    if (Test-Path $serverPayloadDir) {
        Copy-Item "$serverPayloadDir\*" $appDir -Force -Recurse
    }

    if ($deployedVersions.Count -gt 0 -or $deferredVersions.Count -gt 0) {
        Copy-SelfIfNeeded $ScriptPath $selfCopyPath
    }
    # Only mark the release current once at least one version was actually fully deployed -- a
    # version that's only deferred isn't "installed" yet, it's pending (see below).
    if ($deployedVersions.Count -gt 0) {
        @{ version = $releaseTag } | ConvertTo-Json | Out-File $versionMarkerPath -Encoding utf8
    }
    if ($deferredVersions.Count -gt 0) {
        $manifestPath = Get-PendingUpdateManifestPath $Scope
        @{ version = $releaseTag; versions = $deferredVersions } | ConvertTo-Json | Out-File $manifestPath -Encoding utf8
        Register-PendingUpdateWatcher $Scope $selfCopyPath $deferredProcessIds
    }
} finally {
    if ($extractDir -and (Test-Path $extractDir)) { Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($zipDownloaded -and (Test-Path $zipPath)) { Remove-Item $zipPath -Force -ErrorAction SilentlyContinue }
}

if ($deployedVersions.Count -eq 0) {
    # Every detected+shippable version was deferred (still running, user declined to close it now) --
    # the watcher task takes it from here; nothing more to do this run.
    return
}

# --- Register the MCP Server with Claude ------------------------------------------------------------
# Local-mode (this machine runs both Revit and Claude Code) only -- the Mac+Parallels remote-mode
# case is a separate, shorter macOS/bash counterpart script that runs on the Mac host and points
# -mode remote at the VM's shared folder (PRD §12 "Mac + Parallels"), not this script's job.
$serverExe = Join-Path $appDir 'mcp-server.exe'
if ((Test-Path $serverExe) -and (Get-Command claude -ErrorAction SilentlyContinue)) {
    # Remove-then-add rather than checking for an existing entry first: idempotent by construction
    # (matches this whole script's own design principle) without needing to parse `claude mcp list`
    # output or assume a particular error shape from a re-add of an existing name.
    & claude mcp remove revit 2>$null | Out-Null
    & claude mcp add revit -- $serverExe --mode local
} elseif (Test-Path $serverExe) {
    Write-Host "Claude Code CLI not found on PATH -- skipping MCP registration. Add it manually: $serverExe --mode local"
}

# --- Programs & Features entry ------------------------------------------------------------------------
# The one thing a raw script doesn't get for free vs. a real installer -- write it ourselves so
# uninstall is discoverable the normal Windows way, not "hunt down this script again."
New-Item -Force $uninstallKeyPath | Out-Null
Set-ItemProperty $uninstallKeyPath DisplayName 'Revit MCP Bridge'
Set-ItemProperty $uninstallKeyPath DisplayVersion $releaseTag
Set-ItemProperty $uninstallKeyPath UninstallString "powershell -NoProfile -ExecutionPolicy Bypass -File `"$selfCopyPath`" -Uninstall -Scope $Scope"

if (-not $Silent -and $deployedVersions.Count -gt 0) {
    Start-Process "C:\Program Files\Autodesk\Revit $($deployedVersions[0])\Revit.exe"
}

$summary = "Revit MCP Bridge $releaseTag installed for Revit $($deployedVersions -join ', ')."
if ($deferredVersions.Count -gt 0) { $summary += " Revit $($deferredVersions -join ', ') will update automatically once you close it." }
Write-Host $summary

} finally {
    if ($BootstrapCreated -and (Test-Path $ScriptPath)) { Remove-Item $ScriptPath -Force -ErrorAction SilentlyContinue }
}
