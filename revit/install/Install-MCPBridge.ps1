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

param(
    [ValidateSet('User', 'AllUsers')]
    [string]$Scope = 'User',

    # Informational only for messaging purposes -- the idempotency check below behaves identically
    # whether or not this is passed; a plain re-run and an explicit -Update both just do the right
    # thing based on what's actually installed vs. actually latest.
    [switch]$Update,

    [switch]$Uninstall,

    # The ribbon's self-update click passes this: no interactive prompts (Revit is closed
    # automatically rather than asked about), no "already up to date" chatter on the common no-op case.
    [switch]$Silent,

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

function Test-RevitRunning {
    return @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue).Count -gt 0
}

# --- AllUsers self-elevation -------------------------------------------------------------------
# Re-invoke elevated rather than failing outright -- AllUsers is an explicit opt-in choice, not the
# default, so a user who asked for it should get a UAC prompt, not a cryptic access-denied from
# New-Item further down.
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($Scope -eq 'AllUsers' -and -not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $forwardArgs = @('-Scope', 'AllUsers')
    if ($Update) { $forwardArgs += '-Update' }
    if ($Uninstall) { $forwardArgs += '-Uninstall' }
    if ($Silent) { $forwardArgs += '-Silent' }
    if ($LocalPackagePath) { $forwardArgs += @('-LocalPackagePath', $LocalPackagePath) }
    Start-Process powershell -Verb RunAs -ArgumentList (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"") + $forwardArgs) -Wait
    exit $LASTEXITCODE
}

$addinsDirs = $SupportedRevitVersions | ForEach-Object { Get-AddinsDir $_ $Scope }
$appDir = Get-AppDir $Scope
$versionMarkerPath = Join-Path $appDir 'installed-version.json'
$selfCopyPath = Join-Path $appDir 'Install-MCPBridge.ps1'
$uninstallKeyPath = if ($Scope -eq 'User') {
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MCPBridge'
} else {
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MCPBridge'
}

# --- Uninstall ----------------------------------------------------------------------------------
if ($Uninstall) {
    foreach ($version in $SupportedRevitVersions) {
        $dir = Get-AddinsDir $version $Scope
        Remove-Item "$dir\MCPBridge.*" -Force -ErrorAction SilentlyContinue
        Remove-Item "$dir\Microsoft.CodeAnalysis*.dll" -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $appDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $uninstallKeyPath -Recurse -Force -ErrorAction SilentlyContinue
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        & claude mcp remove revit 2>$null | Out-Null
    }
    Write-Host 'MCP Bridge uninstalled.'
    return
}

# --- Resolve target release ----------------------------------------------------------------------
if ($LocalPackagePath) {
    # Test/dev path: no real tag to compare against, so treat every invocation as "an update is
    # available" -- the whole point of this escape hatch is exercising the deploy path, not the
    # version-comparison logic (which has nothing to reflect a local file's "version" against).
    $releaseTag = "local-$(Get-Date -Format 'yyyyMMddHHmmss')"
    $zipPath = $LocalPackagePath
    $skipChecksum = $true
} else {
    $release = Invoke-RestMethod "https://api.github.com/repos/$RepoSlug/releases/latest"
    $releaseTag = $release.tag_name
    $skipChecksum = $false
}

# --- Idempotency check ----------------------------------------------------------------------------
# Trust the ACTUAL deployed files, not just the version marker. A marker claiming "current" while a
# DLL is missing (deleted by hand, a failed prior run, AV quarantine, whatever) must trigger a
# repair, not a silent no-op that leaves a broken install unfixed forever -- see PRD §12
# "Self-upgrade" for the three-outcome reasoning this implements.
$installed = if (Test-Path $versionMarkerPath) { (Get-Content $versionMarkerPath | ConvertFrom-Json).version } else { $null }
$detectedVersions = @(Get-InstalledRevitVersions)
$allDllsPresent = $detectedVersions.Count -gt 0 -and -not ($detectedVersions | Where-Object {
    -not (Test-Path (Join-Path (Get-AddinsDir $_ $Scope) 'MCPBridge.AddIn.dll'))
})

if (-not $LocalPackagePath -and $installed -eq $releaseTag -and $allDllsPresent) {
    if (-not $Silent) { Write-Host "Already up to date ($installed)." }
    return
}
if ($installed -eq $releaseTag -and -not $allDllsPresent -and -not $LocalPackagePath) {
    Write-Host "Installed version marker says $installed but the add-in is missing for at least one detected Revit version -- repairing."
}

if ($detectedVersions.Count -eq 0) {
    throw "No supported Revit version found (checked: $($SupportedRevitVersions -join ', ')). Nothing to install."
}

# --- Download + verify ----------------------------------------------------------------------------
if (-not $LocalPackagePath) {
    $asset = $release.assets | Where-Object name -eq 'mcpbridge-release.zip'
    if (-not $asset) { throw "Release $releaseTag has no 'mcpbridge-release.zip' asset." }
    $zipPath = Join-Path $env:TEMP $asset.name
    Invoke-WebRequest $asset.browser_download_url -OutFile $zipPath

    $checksumsAsset = $release.assets | Where-Object name -eq 'checksums.txt'
    if (-not $checksumsAsset) { throw "Release $releaseTag has no checksums.txt asset -- refusing to install an unverifiable download." }
    $checksums = (Invoke-RestMethod $checksumsAsset.browser_download_url) -split "`n"
    $expectedLine = $checksums | Where-Object { $_ -match [regex]::Escape($asset.name) }
    if (-not $expectedLine) { throw "checksums.txt has no entry for $($asset.name)." }
    $expectedSha256 = ($expectedLine -split '\s+')[0]
    $actualSha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash
    if ($actualSha256 -ne $expectedSha256) {
        throw "Checksum mismatch for $($asset.name): expected $expectedSha256, got $actualSha256. Download may be corrupt or tampered -- aborting."
    }
}

$extractDir = Join-Path $env:TEMP "mcpbridge-extract-$([guid]::NewGuid())"
Expand-Archive $zipPath -DestinationPath $extractDir -Force

# --- Stop Revit if running --------------------------------------------------------------------
# The DLL is locked for Revit's whole session -- this is the one step neither a GUI installer nor
# pyRevit's own approach dodges either. -Silent (the ribbon self-update path) closes without asking;
# an interactive first-install/update asks first.
$wasRunning = Test-RevitRunning
if ($wasRunning) {
    if (-not $Silent) {
        $answer = Read-Host 'Revit is running and must close to finish installing. Close it now? [Y/n]'
        if ($answer -eq 'n') { throw 'Install cancelled -- close Revit and re-run.' }
    }
    Get-Process -Name 'Revit' -ErrorAction SilentlyContinue | ForEach-Object {
        $_.CloseMainWindow() | Out-Null
    }
    Get-Process -Name 'Revit' -ErrorAction SilentlyContinue | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
}

# --- Deploy, per detected+supported version ----------------------------------------------------
# PRD §12 "Multi-version installs": only touch versions we both detected AND shipped a build for in
# this release payload -- a detected-but-unshipped version is skipped with a note, not attempted.
$deployedVersions = @()
$skippedVersions = @()
foreach ($version in $detectedVersions) {
    $payloadDir = Join-Path $extractDir "addin-$version"
    if (-not (Test-Path $payloadDir)) {
        $skippedVersions += $version
        continue
    }
    $dir = Get-AddinsDir $version $Scope
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item "$payloadDir\*" $dir -Force -Recurse
    $deployedVersions += $version
}
if ($skippedVersions.Count -gt 0) {
    Write-Host "No shipped build for Revit $($skippedVersions -join ', ') in this release -- skipped."
}
if ($deployedVersions.Count -eq 0) {
    throw "Detected Revit version(s) $($detectedVersions -join ', ') but this release has no matching build for any of them."
}

New-Item -ItemType Directory -Force -Path $appDir | Out-Null
$serverPayloadDir = Join-Path $extractDir 'server'
if (Test-Path $serverPayloadDir) {
    Copy-Item "$serverPayloadDir\*" $appDir -Force -Recurse
}
@{ version = $releaseTag } | ConvertTo-Json | Out-File $versionMarkerPath -Encoding utf8
Copy-Item $PSCommandPath $selfCopyPath -Force

Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
if (-not $LocalPackagePath) { Remove-Item $zipPath -Force -ErrorAction SilentlyContinue }

# --- Register the MCP Server with Claude --------------------------------------------------------
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
    Write-Host "Claude Code CLI not found on PATH -- skipping MCP registration. Add manually: $serverExe --mode local"
}

# --- Programs & Features entry -------------------------------------------------------------------
# The one thing a raw script doesn't get for free vs. a real installer -- write it ourselves so
# uninstall is discoverable the normal Windows way, not "hunt down this script again."
New-Item -Force $uninstallKeyPath | Out-Null
Set-ItemProperty $uninstallKeyPath DisplayName 'Revit MCP Bridge'
Set-ItemProperty $uninstallKeyPath DisplayVersion $releaseTag
Set-ItemProperty $uninstallKeyPath UninstallString "powershell -NoProfile -ExecutionPolicy Bypass -File `"$selfCopyPath`" -Uninstall -Scope $Scope"

if ($wasRunning -and -not $Silent) {
    Start-Process "C:\Program Files\Autodesk\Revit $($deployedVersions[0])\Revit.exe"
}
Write-Host "Installed $releaseTag for Revit $($deployedVersions -join ', ')."
