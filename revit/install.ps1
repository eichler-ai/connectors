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
# Primary invocation is piped (irm https://raw.githubusercontent.com/eichler-ai/connectors/main/revit/install.ps1 | iex, PRD §12) -- $PSCommandPath is empty in that
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

    # Testing/offline escape hatch: exercise the deploy mechanics (path resolution, per-version
    # detection, idempotency, registry writes, MCP registration) against a hand-built local zip in
    # the release layout instead of a download. Production installs never pass this -- its presence
    # is itself a signal this is a dev/test invocation.
    [string]$LocalPackagePath,

    # Internal, for tests and the release workflow: define this script's functions and return
    # before doing anything (no elevation, no detection, no network). `. .\install.ps1
    # -LoadFunctionsOnly` then gives Pester (revit/install.tests.ps1) and release.yml
    # (New-PackageManifest) the same code an install runs, rather than a copy of it.
    [switch]$LoadFunctionsOnly
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 (what Revit machines ship with) throttles Invoke-WebRequest/-RestMethod to a
# small fraction of the real link speed while it renders the per-response progress bar -- the ~120 MB
# release download can crawl for many minutes, appearing hung on "Writing request stream...". Silencing
# progress restores full download speed. Set globally so it also applies to any re-launched (elevated)
# process, which re-reads this from the top.
$ProgressPreference = 'SilentlyContinue'

$RepoSlug = 'eichler-ai/connectors'

# Review finding: $PSCommandPath is empty/null under the script's own PRIMARY documented invocation
# (irm https://raw.githubusercontent.com/eichler-ai/connectors/main/revit/install.ps1 | iex, PRD §12) -- there's no file on disk to point at. Every downstream use
# of "this script's own path" (elevation re-invoke, the self-copy used for the uninstall string and
# for the deferred-update watcher task) goes through $ScriptPath instead, which is always a real
# file: materialize our own source to one when piped, since $MyInvocation.MyCommand.Definition inside
# an iex'd scriptblock still holds the literal source text even though $PSCommandPath doesn't.
# $BootstrapCreated tracks whether we own that temp file, so it gets cleaned up on exit either way --
# review finding: a fixed, never-cleaned-up bootstrap filename both accumulates in %TEMP% forever and
# risks two concurrent piped invocations clobbering each other mid-read; GUID-suffixed avoids the
# second, the top-level try/finally below (wrapping everything after this point) avoids the first.
#
# Issue #192: the paragraph above was wrong about $MyInvocation.MyCommand.Definition. Under
# `<text> | iex` it holds the CALLER's command line -- for the documented one-liner, literally
# "irm https://.../install.ps1 | iex" -- not the script text (reproduced with pwsh; see the issue).
# So every piped install wrote a 93-byte stub as its self-copy, and everything that later ran that
# copy with arguments (the ribbon's Update Now with -Update -Silent, the deferred-update watcher with
# -ApplyPendingUpdate, Apps & Features' -Uninstall) lost them: Update Now ran an interactive install in a
# hidden window and hung on Read-Host. Found live on the first Update Now against a real release.
# Get-InstallerSourceForBootstrap now uses the definition only if it IS the full script, and otherwise
# fetches the canonical script from the raw URL (what the stub did anyway, minus the argument loss),
# validating either way; Copy-SelfIfNeeded refuses to install anything but the full script.
$InstallerRawUrl = "https://raw.githubusercontent.com/$RepoSlug/main/revit/install.ps1"

# The one-liner stub is ~90 bytes with no param block; the real script declares -LoadFunctionsOnly and
# defines Copy-SelfIfNeeded. Both markers plus a size floor, so a truncated download fails too.
function Test-IsFullInstallerScript([string]$Text) {
    if (-not $Text) { return $false }
    return ($Text.Length -gt 10000) -and
        ($Text -match '(?m)^\s*\[switch\]\$LoadFunctionsOnly') -and
        ($Text -match 'function Copy-SelfIfNeeded')
}

function Get-InstallerSourceForBootstrap([string]$InvocationDefinition, [string]$Url) {
    if (Test-IsFullInstallerScript $InvocationDefinition) { return $InvocationDefinition }
    $text = (Invoke-WebRequest -Uri $Url -UseBasicParsing).Content
    if (-not (Test-IsFullInstallerScript $text)) {
        throw "Could not obtain install.ps1's own source for the installed copy: the download from $Url did not look like the installer (length $($text.Length)). Re-run from a saved copy of install.ps1 instead of the piped one-liner."
    }
    return $text
}

$ScriptPath = $PSCommandPath
$BootstrapCreated = $false
if (-not $ScriptPath -and -not $LoadFunctionsOnly) {
    $ScriptPath = Join-Path $env:TEMP "mcpbridge-install-bootstrap-$([guid]::NewGuid()).ps1"
    Get-InstallerSourceForBootstrap $MyInvocation.MyCommand.Definition $InstallerRawUrl | Out-File $ScriptPath -Encoding utf8 -Force
    $BootstrapCreated = $true
}

# 2025 and 2027 ship builds today (PRD §11: both have verified .NET requirements -- net8.0-windows and
# net10.0-windows respectively). 2026 remains unverified and isn't in this list yet. The detection/deploy
# loop below is written to cover every year in this list, not just the first one, specifically so adding
# 2026 (Phase 6, PRD §15) once verified is "add a year + a matching addin-<year>/ build to the release
# payload," not a rewrite of this script.
$SupportedRevitVersions = @('2025', '2027')

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

# Returns $true only if every passed process is ACTUALLY gone afterwards. The return value matters:
# CloseMainWindow() is a request, not a kill, and it routinely fails to close Revit -- the user hits
# Cancel on a "save changes?" prompt, a modal dialog owns the UI thread, or the process simply has no
# main window to send WM_CLOSE to (CloseMainWindow returns false and Wait-Process then just burns its
# full timeout). This used to return nothing and every caller assumed success, so a failed close fell
# straight through to Copy-Item over a still-running Revit's own loaded DLLs: either an abort partway
# through the deploy (files are locked, and $ErrorActionPreference='Stop'), or -- worse, when nothing
# happened to be locked -- a cheerful "installed for Revit <version>" for a version that is still
# running the OLD code and will keep doing so until it restarts. Confirmed live: a -Silent install
# reported success for a running 2027 whose process was untouched. Callers must now check this and
# route a failure into the deferred-update path below, which exists for exactly this situation.
function Stop-RevitProcessGracefully($Process) {
    # CloseMainWindow() THROWS ("Process has exited, so the requested information is not available")
    # if the process is already gone -- a real race, since the user is perfectly likely to close Revit
    # themselves while this script is prompting them about it. With $ErrorActionPreference='Stop' that
    # exception is terminating and would abort the whole install, for the one outcome we actually
    # wanted. Per-process try/catch: a process that's already gone is a success, not a failure.
    foreach ($p in $Process) {
        try { $p.CloseMainWindow() | Out-Null } catch { }
    }
    $Process | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
    foreach ($p in $Process) {
        try { $p.Refresh() } catch { continue }
        if (-not $p.HasExited) { return $false }
    }
    return $true
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
#
# Issue #192: refuses a source that is not the full installer. The self-copy is what Update Now, the
# deferred-update watcher and Apps & Features all run WITH ARGUMENTS, so a stub here (the one-liner,
# which is what a piped run's $MyInvocation.MyCommand.Definition actually is) breaks all three at once
# and silently. Guarding at the point of the write means no invocation path can regress this again.
function Copy-SelfIfNeeded([string]$Source, [string]$Destination) {
    if (-not (Test-IsFullInstallerScript (Get-Content $Source -Raw))) {
        throw "Refusing to install '$Source' as the installer's own copy at '$Destination': it is not the full install.ps1 (issue #192). Update Now, deferred updates and uninstall would all lose their arguments."
    }
    $resolvedSource = (Resolve-Path $Source).Path
    $resolvedDest = if (Test-Path $Destination) { (Resolve-Path $Destination).Path } else { $null }
    if ($resolvedSource -ne $resolvedDest) {
        Copy-Item $Source $Destination -Force
    }
}

# --- Release manifest: per-component change detection (howto-seed-plan.md §1, step 5) -----------
# A release zip carries manifest.json at its root: the release tag, one sha256 per component
# (addin-2025, addin-2027, server -- a content hash over the component's files, see
# Get-DirectoryContentHash) and the how-to corpus version the broker embeds. The version marker
# records the hashes that were actually installed, so a later run redeploys only what changed: a
# corpus-only release changes the `server` hash alone, and the add-in payloads -- and therefore the
# running Revit -- are left untouched. A zip without a manifest (a hand-built local package, or a
# release older than this scheme) is treated as "everything changed", which is the old behaviour.

function Get-DirectoryContentHash([string]$Dir) {
    # sha256 over "relative/path\n<sha256 of the file>\n" for every file, sorted by path, so the
    # hash is stable across zip tools and timestamps and changes when any file's bytes or name do.
    # Forward slashes and lower-case hex on both sides, so the workflow (Windows) and a test (any
    # OS) agree byte for byte.
    $root = (Resolve-Path $Dir).Path
    $lines = Get-ChildItem $root -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
        "$rel`n$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())`n"
    } | Sort-Object
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join ''))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant() } finally { $sha.Dispose() }
}

function New-PackageManifest([string]$StageDir, [string]$Version, $HowToCorpus) {
    # $HowToCorpus is the broker's `-build-info` howto_corpus object (documents, hash, verified_on),
    # or $null. Components are whatever addin-*/ and server/ directories the stage holds.
    $components = [ordered]@{}
    Get-ChildItem $StageDir -Directory | Where-Object { $_.Name -like 'addin-*' -or $_.Name -eq 'server' } | Sort-Object Name | ForEach-Object {
        $components[$_.Name] = [ordered]@{ sha256 = (Get-DirectoryContentHash $_.FullName) }
    }
    $manifest = [ordered]@{ schema_version = 1; version = $Version; components = $components }
    if ($HowToCorpus) { $manifest['howto_corpus'] = $HowToCorpus }
    $manifest
}

function Read-PackageManifest([string]$ExtractDir) {
    $path = Join-Path $ExtractDir 'manifest.json'
    if (-not (Test-Path $path)) { return $null }
    Get-Content $path -Raw | ConvertFrom-Json
}

function Get-ManifestComponentHash($Manifest, [string]$Component) {
    if (-not $Manifest -or -not $Manifest.PSObject.Properties['components']) { return $null }
    $entry = $Manifest.components.PSObject.Properties[$Component]
    if ($entry -and $entry.Value -and $entry.Value.PSObject.Properties['sha256'] -and $entry.Value.sha256) { return [string]$entry.Value.sha256 }
    return $null
}

function Get-InstalledComponentHash($Marker, [string]$Component) {
    if (-not $Marker -or -not $Marker.PSObject.Properties['components'] -or -not $Marker.components) { return $null }
    $entry = $Marker.components.PSObject.Properties[$Component]
    if ($entry -and $entry.Value) { return [string]$entry.Value }
    return $null
}

function Test-ComponentUnchanged($Manifest, $Marker, [string]$Component, [bool]$PresentOnDisk) {
    # Skip a component only when three things hold: the package says what it contains (a hash),
    # the last install recorded the same hash, AND the files are actually on disk. Any of them
    # missing means deploy -- the third is the repair case the idempotency check exists for.
    $new = Get-ManifestComponentHash $Manifest $Component
    if (-not $new) { return $false }
    $old = Get-InstalledComponentHash $Marker $Component
    return (($old -eq $new) -and $PresentOnDisk)
}

function Get-BrokerProcess([string]$AppDir) {
    # The broker(s) started from THIS install: every MCP client session spawns one (the singleton
    # makes all but one a proxy), and each is owned by that session -- killing it breaks the
    # session, which is why the installer never does.
    # 'mcp-server*': a broker still running from a renamed mcp-server.exe.old image (a staged swap)
    # may report that name.
    Get-Process -Name 'mcp-server*' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -like (Join-Path $AppDir '*') } catch { $false }
    }
}

function Install-BrokerStaged([string]$ServerPayloadDir, [string]$AppDir) {
    # Stage-and-swap for mcp-server.exe (seed plan §1 item 2). Never overwrites a locked file and
    # never stops a broker. Returns one of:
    #   swapped  -- the new exe is in place and nothing was running it; the next start uses it.
    #   staged   -- a broker was running: the running image was renamed to .old (Windows allows
    #               renaming a mapped executable, not overwriting it) and the new exe took its
    #               path, so the running broker keeps serving the old code until the MCP client
    #               next starts it; broker.json -- and the ribbon's "update available" -- clear then.
    #   pending  -- the rename was refused (an AV scan, a non-Windows lock): the new exe waits as
    #               mcp-server.exe.new and the next run of this script (or the watcher) completes
    #               the swap once nothing holds the file.
    New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
    $exe = Join-Path $AppDir 'mcp-server.exe'
    $new = "$exe.new"
    $old = "$exe.old"
    # A leftover .old from an earlier staged swap: gone once its broker exited, else still mapped.
    Remove-Item $old -Force -ErrorAction SilentlyContinue
    # Everything beside the exe (nothing today; kept so a future server-side file is not silently
    # dropped by the exe-only staging below).
    Get-ChildItem $ServerPayloadDir -File | Where-Object { $_.Name -ne 'mcp-server.exe' } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $AppDir $_.Name) -Force
    }
    Copy-Item (Join-Path $ServerPayloadDir 'mcp-server.exe') $new -Force
    if (-not (Test-Path $exe)) {
        Move-Item $new $exe -Force
        return 'swapped'
    }
    $running = @(Get-BrokerProcess $AppDir)
    try {
        Move-Item $exe $old -Force
        Move-Item $new $exe -Force
    } catch {
        return 'pending'
    }
    if ($running.Count -gt 0) { return 'staged' }
    Remove-Item $old -Force -ErrorAction SilentlyContinue
    return 'swapped'
}

function Complete-PendingBrokerSwap([string]$AppDir) {
    # Finishes a 'pending' outcome from an earlier run, and tidies the .old image a 'staged' swap
    # left behind once its broker has exited (the removal is refused, harmlessly, while it is still
    # mapped). Runs on every install, not only when the server changes -- found live: a run that
    # skipped an unchanged broker left the previous run's .old in place. Returns $true when a swap
    # happened.
    $exe = Join-Path $AppDir 'mcp-server.exe'
    $new = "$exe.new"
    Remove-Item "$exe.old" -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $new)) { return $false }
    if (Get-BrokerProcess $AppDir) { return $false }
    try {
        if (Test-Path $exe) { Move-Item $exe "$exe.old" -Force }
        Move-Item $new $exe -Force
        Remove-Item "$exe.old" -Force -ErrorAction SilentlyContinue
        return $true
    } catch {
        return $false
    }
}

# --- Claude client MCP registration -----------------------------------------------------------------
# The broker is a local stdio MCP server; the Claude clients need a config entry pointing at it. Claude
# Code CLI has its own `claude mcp add`; Claude Desktop (and Cowork, which reads the same file) has no
# CLI, so its JSON config is edited directly here. These are functions so install.tests.ps1 can prove
# the merge preserves other servers and the removal takes only ours.

function Get-DesktopConfigPath {
    # Claude Desktop / Cowork on Windows. The standard .exe build reads %APPDATA%\Claude; the Microsoft
    # Store / MSIX build virtualizes that path, so a write to %APPDATA%\Claude is SILENTLY IGNORED there
    # and the real config lives under the package's LocalCache (confirmed live: this project's own VM has
    # the Store build, at Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude). Prefer the MSIX
    # location when a Claude package is present (its Roaming\Claude directory exists), else the standard
    # path.
    $pkg = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Filter 'Claude*' -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'LocalCache\Roaming\Claude') -ErrorAction SilentlyContinue } |
        Select-Object -First 1
    if ($pkg) { return (Join-Path $pkg.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json') }
    return (Join-Path $env:APPDATA 'Claude\claude_desktop_config.json')
}

function Add-DesktopMcpServer([string]$ConfigPath, [string]$Name, [string]$Command, [string[]]$Arguments) {
    # Merge one stdio server into Claude Desktop's config WITHOUT disturbing any other server or
    # top-level key. $false (a no-op) when Claude Desktop is not installed for this user (its config
    # directory is absent). Backs the file up first. Writes UTF-8 with NO BOM -- a leading BOM breaks
    # some JSON parsers and the file is strict JSON.
    $dir = Split-Path $ConfigPath
    if (-not (Test-Path $dir)) { return $false }
    $cfg = if (Test-Path $ConfigPath) { (Get-Content $ConfigPath -Raw) | ConvertFrom-Json } else { [pscustomobject]@{} }
    # Create mcpServers when it is absent OR present-but-null (a config with `"mcpServers": null` would
    # otherwise pass the property check and then throw on the Add-Member below).
    if (-not $cfg.PSObject.Properties['mcpServers'] -or $null -eq $cfg.mcpServers) {
        $cfg | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    $entry = [pscustomobject][ordered]@{ type = 'stdio'; command = $Command; args = @($Arguments) }
    $cfg.mcpServers | Add-Member -NotePropertyName $Name -NotePropertyValue $entry -Force
    # Back up only once, so a re-install never overwrites the pristine pre-install backup.
    if ((Test-Path $ConfigPath) -and -not (Test-Path "$ConfigPath.mcpbridge.bak")) { Copy-Item $ConfigPath "$ConfigPath.mcpbridge.bak" -Force }
    [System.IO.File]::WriteAllText($ConfigPath, ($cfg | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding($false)))
    return $true
}

function Remove-DesktopMcpServer([string]$ConfigPath, [string]$Name) {
    # Remove one server from Claude Desktop's config, leaving every other server and key intact. No-op
    # (and $false) when the file, the mcpServers key, or the named entry is absent.
    if (-not (Test-Path $ConfigPath)) { return $false }
    $cfg = (Get-Content $ConfigPath -Raw) | ConvertFrom-Json
    if (-not $cfg.PSObject.Properties['mcpServers'] -or -not $cfg.mcpServers.PSObject.Properties[$Name]) { return $false }
    $cfg.mcpServers.PSObject.Properties.Remove($Name)
    [System.IO.File]::WriteAllText($ConfigPath, ($cfg | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding($false)))
    return $true
}

function Invoke-ClaudeMcp([string[]]$CliArgs) {
    # Thin, mockable wrapper around `claude mcp ...`. Returns @{ ExitCode; Output } and NEVER throws:
    # under $ErrorActionPreference='Stop' a native non-zero exit OR a line on stderr (even with a
    # redirect) can surface as a terminating error -- notably `claude mcp remove revit` when nothing is
    # registered prints "No MCP server named 'revit' in user scope" and exits non-zero. Neutralize EAP
    # locally and report the exit code so callers drive off it instead of being aborted.
    $savedEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & claude mcp @CliArgs 2>&1
        return @{ ExitCode = $LASTEXITCODE; Output = $out }
    } catch {
        return @{ ExitCode = -1; Output = $_.Exception.Message }
    } finally {
        $ErrorActionPreference = $savedEap
    }
}

function Register-McpServer([string]$ServerExe, [switch]$OnlyIfMissing) {
    # Connect the broker (a local stdio MCP server, mcp-server.exe --mode local -- PRD §05) to this
    # user's Claude clients: Claude Code CLI, and Claude Desktop (which is also what Cowork reads).
    # Prints exact manual instructions for whatever couldn't be done automatically. Called on BOTH the
    # fresh/update path AND the "already up to date" short-circuit -- the add-in being current does not
    # imply the MCP wiring is present (a prior run may have deployed the DLL but never reached
    # registration), so re-running must be able to repair just the wiring. With -OnlyIfMissing it leaves
    # an already-registered client untouched and silent, so a healthy up-to-date re-run says nothing.
    if (-not (Test-Path $ServerExe)) { return }
    $registered = @()
    $todo = @()
    $didWork = $false

    # Claude Code CLI: `claude mcp add` at USER scope, so it is available in EVERY project (the default
    # `local` scope binds it to the current project only). Only `remove` when the entry is actually
    # present -- removing an absent server errors, and (see Invoke-ClaudeMcp) that error would otherwise
    # abort the whole install on a fresh machine.
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        $listRes = Invoke-ClaudeMcp @('list')
        $cliPresent = (($listRes.Output -join "`n") -match '(?im)^\s*revit[\s:]')
        if (-not ($OnlyIfMissing -and $cliPresent)) {
            if ($cliPresent) { Invoke-ClaudeMcp @('remove', 'revit', '--scope', 'user') | Out-Null }
            $addRes = Invoke-ClaudeMcp @('add', '--scope', 'user', 'revit', '--', $ServerExe, '--mode', 'local')
            if ($addRes.ExitCode -eq 0) {
                $registered += 'Claude Code CLI (user scope, every project)'; $didWork = $true
            } else {
                $addTail = ($addRes.Output | Select-Object -Last 1)
                $todo += "Claude Code CLI: automatic registration failed ($addTail). Update the claude CLI, then run: claude mcp add --scope user revit -- `"$ServerExe`" --mode local"
            }
        }
    } elseif (-not $OnlyIfMissing) {
        $todo += "Claude Code CLI (when its CLI is on PATH): claude mcp add --scope user revit -- `"$ServerExe`" --mode local"
    }

    # Claude Desktop + Cowork: merge the entry into claude_desktop_config.json (no CLI edits it). Both
    # the path probe and the merge run inside one try/catch so an unreadable Packages\ dir or a
    # hand-corrupted config falls to printed instructions instead of aborting.
    $desktopCfg = $null
    $desktopOk = $false
    $desktopErr = $null
    $desktopPresent = $false
    try {
        $desktopCfg = Get-DesktopConfigPath
        if ($OnlyIfMissing -and (Test-Path $desktopCfg)) {
            $existing = (Get-Content $desktopCfg -Raw) | ConvertFrom-Json
            $desktopPresent = ($existing.PSObject.Properties['mcpServers'] -and $existing.mcpServers -and $existing.mcpServers.PSObject.Properties['revit'])
        }
        if (-not ($OnlyIfMissing -and $desktopPresent)) {
            $desktopOk = Add-DesktopMcpServer $desktopCfg 'revit' $ServerExe @('--mode', 'local')
        }
    } catch { $desktopOk = $false; $desktopErr = $_.Exception.Message }
    if ($desktopOk) {
        $registered += 'Claude Desktop / Cowork (restart Claude Desktop to load it)'; $didWork = $true
    } elseif (-not ($OnlyIfMissing -and $desktopPresent)) {
        $cfgHint = if ($desktopCfg) { $desktopCfg } else { '%APPDATA%\Claude\claude_desktop_config.json (or your Claude Desktop config)' }
        $jsonExe = $ServerExe.Replace('\', '\\')
        $jsonNote = if ($desktopErr -and $desktopErr -match 'JSON|Convert|parse|token') { "your existing config at $cfgHint isn't valid JSON ($desktopErr) -- fix that first, then " } else { '' }
        $todo += "Claude Desktop / Cowork: ${jsonNote}add this under `"mcpServers`" in $cfgHint, then restart Claude Desktop:`n      `"revit`": { `"type`": `"stdio`", `"command`": `"$jsonExe`", `"args`": [`"--mode`", `"local`"] }"
    }

    # One client (and the trailing note) per line -- crammed onto one line with '; ' separators and
    # inline parentheticals it was hard to read at a glance.
    if ($registered.Count -gt 0) {
        Write-Host ''
        Write-Host 'Connected the revit MCP server to:'
        foreach ($r in $registered) { Write-Host "  - $r" }
    }
    if ($todo.Count -gt 0) {
        Write-Host ''
        Write-Host 'To finish connecting it:'
        foreach ($t in $todo) { Write-Host "  - $t" }
    }
    if ($didWork -or $todo.Count -gt 0) {
        Write-Host ''
        Write-Host "It runs as: `"$ServerExe`" --mode local"
        Write-Host 'Restart any Claude client that was open when this ran, so it reloads its MCP config.'
    }
}

if ($LoadFunctionsOnly) { return }

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
    $brokerSwapped = Complete-PendingBrokerSwap $appDir
    $serverStillPending = $manifest.PSObject.Properties['components'] -and $manifest.components -and
        $manifest.components.PSObject.Properties['server'] -and -not $brokerSwapped -and
        (Test-Path (Join-Path $appDir 'mcp-server.exe.new'))
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
        # Carry `deployed`/`skipped` forward (see the idempotency check and the marker write further
        # down). This path completes an install whose deferred half has now landed, so the versions it
        # just applied join `deployed` -- they now genuinely have files on disk, which is exactly what
        # that field is asked about. Dropping these fields would silently send the next run back to the
        # old check-every-detected-version behaviour.
        $priorMarker = if (Test-Path $versionMarkerPath) { Get-Content $versionMarkerPath | ConvertFrom-Json } else { $null }
        # @( ) OUTSIDE the if: assigning an if-expression's output unrolls a one-element array into
        # a scalar, and a string + array is string concatenation -- found live (step 5): a marker
        # read back as "2027" plus the pending "2027" recorded `deployed: "20272027"`, after which
        # every later run treated 2027 as never installed.
        $priorDeployed = @(if ($priorMarker -and $priorMarker.PSObject.Properties['deployed']) { $priorMarker.deployed })
        $priorSkipped = @(if ($priorMarker -and $priorMarker.PSObject.Properties['skipped']) { $priorMarker.skipped })
        $priorDeferred = @(if ($priorMarker -and $priorMarker.PSObject.Properties['deferred']) { $priorMarker.deferred })
        $nowDeployed = @($priorDeployed + @($manifest.versions) | Sort-Object -Unique)
        # The staged payloads' component hashes were parked in the pending manifest at stage time
        # (they must not be recorded as installed until the files are actually on disk).
        $components = @{}
        if ($priorMarker -and $priorMarker.PSObject.Properties['components'] -and $priorMarker.components) {
            foreach ($prop in $priorMarker.components.PSObject.Properties) { $components[$prop.Name] = $prop.Value }
        }
        if ($manifest.PSObject.Properties['components'] -and $manifest.components) {
            foreach ($prop in $manifest.components.PSObject.Properties) {
                # The server hash is recorded only when the swap really happened; a broker still
                # running from the old image keeps the .new waiting, and the next run's
                # Complete-PendingBrokerSwap (before the idempotency check) finishes it.
                if ($prop.Name -eq 'server' -and $serverStillPending) { continue }
                $components[$prop.Name] = $prop.Value
            }
        }
        @{
            version  = $manifest.version
            deployed = $nowDeployed
            # A version can be in exactly one list; anything just applied leaves the other two.
            skipped  = @($priorSkipped | Where-Object { $nowDeployed -notcontains $_ } | Sort-Object -Unique)
            deferred = @($priorDeferred | Where-Object { $nowDeployed -notcontains $_ } | Sort-Object -Unique)
            components = $components
            # Carried from the prior marker: an all-deferred run only happens when the server was
            # unchanged (a server change is accounted on its own), so the corpus is the prior one.
            howto_corpus = if ($priorMarker -and $priorMarker.PSObject.Properties['howto_corpus']) { $priorMarker.howto_corpus } else { $null }
        } | ConvertTo-Json | Out-File $versionMarkerPath -Encoding utf8
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
        # Install deploys a self-contained payload STRAIGHT into Addins\<version> (Copy-Item
        # "$payloadDir\*"): the .addin manifest, MCPBridge.* and Roslyn, plus Eichler.Connectors.Revit.*,
        # the SQLite stack, System.Data.Common, the localization satellite folders and runtimes\. A
        # self-contained payload OWNS the folder, so remove the whole folder -- but only when every entry
        # in it is demonstrably ours. A bare "our .addin is the only manifest here" test is NOT sufficient:
        # a third-party add-in whose manifest lives in the all-users Addins location can still drop its
        # DLLs into this per-user folder, leaving no foreign *.addin here, and whole-folder removal would
        # then delete their files. So require that EVERY top-level entry matches our payload before
        # removing the folder; otherwise remove only our own members (satellite resource DLLs and
        # runtimes\ included) and leave anything foreign untouched.
        $ownedPatterns = @('MCPBridge.*', 'Eichler.Connectors.Revit.*', 'Microsoft.CodeAnalysis*',
                           'Microsoft.Data.Sqlite.dll', 'e_sqlite3.dll', 'SQLitePCLRaw.*',
                           'System.Data.Common.dll', 'runtimes')
        if (Test-Path (Join-Path $dir 'MCPBridge.addin')) {
            $oursOnly = $true
            foreach ($e in @(Get-ChildItem $dir -ErrorAction SilentlyContinue)) {
                $matched = $false
                foreach ($p in $ownedPatterns) { if ($e.Name -like $p) { $matched = $true; break } }
                # A locale satellite dir (de\, pt-BR\, zh-Hans\, ...) holding only *.resources.dll is ours
                # (Roslyn's localized resources), so it doesn't disqualify the folder.
                if (-not $matched -and $e.PSIsContainer -and $e.Name -match '^[A-Za-z]{2}(-[A-Za-z]+)?$') {
                    $inner = @(Get-ChildItem $e.FullName -File -ErrorAction SilentlyContinue)
                    if ($inner.Count -gt 0 -and -not ($inner | Where-Object { $_.Name -notlike '*.resources.dll' })) { $matched = $true }
                }
                if (-not $matched) { $oursOnly = $false; break }
            }
            if ($oursOnly) {
                Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
            } else {
                foreach ($pat in $ownedPatterns) { Remove-Item (Join-Path $dir $pat) -Force -Recurse -ErrorAction SilentlyContinue }
                # Roslyn's localized resource DLLs sit inside locale subfolders; remove just those (and any
                # locale folder they leave empty), so a third party's resources in the same folder survive.
                foreach ($sub in @(Get-ChildItem $dir -Directory -ErrorAction SilentlyContinue)) {
                    Remove-Item (Join-Path $sub.FullName 'Microsoft.CodeAnalysis*.resources.dll') -Force -ErrorAction SilentlyContinue
                    if (-not @(Get-ChildItem $sub.FullName -Force -ErrorAction SilentlyContinue)) { Remove-Item $sub.FullName -Force -ErrorAction SilentlyContinue }
                }
            }
        }
        # A DLL surviving removal means a running Revit held it locked -- the "close it and re-run" case
        # below. Revit doesn't hold the .addin manifest open, so the loaded DLL is the reliable signal.
        if (Test-Path (Join-Path $dir 'MCPBridge.AddIn.dll')) { $leftoverVersions += $version }
    }
    Unregister-ScheduledTask -TaskName (Get-PendingUpdateTaskName $Scope) -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item $appDir -Recurse -Force -ErrorAction SilentlyContinue
    # The broker keeps its private app-data root at %LOCALAPPDATA%\Connectors\Revit -- what
    # singleton.AppDataDir() resolves on Windows: the materialized search+how-to ranking models
    # (~24MB, a copy of bytes embedded in the exe being removed), the local how-to corpus, the
    # discovery cache, and -- in local mode -- the broker.json/broker.lock rendezvous pair. None of it
    # is a record worth keeping: broker.json is a live rendezvous file whose every field (port, pid,
    # token) is minted fresh on the next launch, and the models are re-materialized from the exe.
    #
    # $leftoverVersions is non-empty only when a version's add-in DLL survived removal (a running
    # Revit had it locked), which means a broker may still be running and relying on this root -- so
    # only clear the whole root when NO add-in remains for this account. Otherwise fall back to the
    # old behaviour of dropping just the models directory. A locked, in-use model file -- e.g. one a
    # broker still running from a renamed .old image holds open -- simply fails to delete and stays;
    # it goes when that process exits, and nothing else references it. Path scope is per-account --
    # the %LOCALAPPDATA% of whoever runs this uninstaller -- so under -Scope AllUsers other users'
    # copies (and, if UAC was answered with a different admin account, the invoking user's own) stay
    # behind, the same per-account scoping the claude-mcp deregistration below already has.
    if ($leftoverVersions.Count -eq 0) {
        Remove-Item "$env:LocalAppData\Connectors\Revit" -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Remove-Item "$env:LocalAppData\Connectors\Revit\models" -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $uninstallKeyPath -Recurse -Force -ErrorAction SilentlyContinue
    # Deregister from the Claude clients install registered it with -- CLI at the same user scope, and
    # Claude Desktop's config (Cowork reads the same file), leaving any other MCP servers there intact.
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        & claude mcp remove revit --scope user 2>$null | Out-Null
    }
    try { Remove-DesktopMcpServer (Get-DesktopConfigPath) 'revit' | Out-Null } catch { }
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
$marker = if (Test-Path $versionMarkerPath) { Get-Content $versionMarkerPath | ConvertFrom-Json } else { $null }
$installed = if ($marker) { $marker.version } else { $null }

# A broker swap an earlier run could not finish (a locked exe) completes here, before the
# "already up to date" short-circuit below can return -- independent review of #174: with the
# completion only inside the deploy block, a broker-only release whose exe was locked stranded its
# .new until the NEXT release, while the summary told the user to re-run. The marker recorded the
# new server hash only once the swap actually happened, so this is the one step still owed.
if (Complete-PendingBrokerSwap $appDir) {
    if (-not $Silent) { Write-Host "Finished a broker update that was waiting for the previous broker to exit." }
}

# Only require a DLL for versions the LAST INSTALL ACTUALLY COVERED. Checking every detected version
# is wrong whenever a release ships no `addin-<year>/` payload for one of them: the deploy loop below
# skips such a version by design, so its DLL never appears, so this check could never become true, so
# the "already up to date" short-circuit below could never fire. Every subsequent run would then
# re-download the release and re-enter the deploy loop -- which for a running Revit prompts the user
# or, under -Silent, force-closes it. PRD §12's self-upgrade path would be interrupting a perfectly
# healthy install on every invocation. A release shipping 2027-only while 2025 is also installed is
# a realistic encounter.
#
# TWO SEPARATE QUESTIONS, so two separate sets. Conflating them into one `covered` list was the
# first attempt at this fix and it did not work at all: a SKIPPED version has, by definition, no DLL,
# so putting it in the set that must have a DLL left the check false forever and reproduced the exact
# bug it was meant to fix.
#   1. "Is this version new since the last install?" -> deployed UNION skipped. Anything detected but
#      in neither was installed after that run (e.g. the user added Revit 2025 later) and must NOT
#      short-circuit: only a download can reveal whether a payload exists for it.
#   2. "Must this version have a DLL on disk?" -> deployed ONLY. A skipped version legitimately has
#      no files, and demanding them is what broke this.
# Markers predating these fields have neither, so fall back to the old all-detected behaviour rather
# than wrongly skipping work.
$deployedBefore = if ($marker -and $marker.PSObject.Properties['deployed']) { @($marker.deployed) } else { $null }
$skippedBefore = if ($marker -and $marker.PSObject.Properties['skipped']) { @($marker.skipped) } else { @() }
# Deferred versions are ACCOUNTED FOR but do NOT need a DLL: the release had a payload for them and
# staged it, but that version's Revit was still running, so the watcher task applies it once that
# Revit exits. Leaving them out of both lists made them look new-since-last-install, so every run
# while an update was pending re-downloaded the release and re-entered the deploy loop -- a bounded
# dose of the same symptom this whole check exists to prevent.
$deferredBefore = if ($marker -and $marker.PSObject.Properties['deferred']) { @($marker.deferred) } else { @() }

if ($null -eq $deployedBefore) {
    $versionsNeedingDll = $detectedVersions
    $unaccountedVersions = @()
} else {
    $versionsNeedingDll = @($detectedVersions | Where-Object { $deployedBefore -contains $_ })
    $unaccountedVersions = @($detectedVersions | Where-Object {
        ($deployedBefore -notcontains $_) -and ($skippedBefore -notcontains $_) -and ($deferredBefore -notcontains $_)
    })
}

$allDllsPresent =
    ($unaccountedVersions.Count -eq 0) -and
    -not ($versionsNeedingDll | Where-Object {
        -not (Test-Path (Join-Path (Get-AddinsDir $_ $Scope) 'MCPBridge.AddIn.dll'))
    })

if (-not $LocalPackagePath -and $installed -eq $releaseTag -and $allDllsPresent) {
    if (-not $Silent) { Write-Host "Revit MCP Bridge is already up to date ($installed)." }
    # The add-in is current, but a prior run may have deployed it without ever reaching MCP
    # registration (interrupted, or an older installer that didn't register). Ensure the wiring is
    # present before returning -- idempotent, and -OnlyIfMissing keeps a healthy re-run silent. Best
    # effort: the add-in is already up to date, so a hiccup wiring a client must not fail the whole run.
    try { Register-McpServer (Join-Path (Get-AppDir $Scope) 'mcp-server.exe') -OnlyIfMissing }
    catch { Write-Host "Note: could not auto-register the MCP server ($($_.Exception.Message)). Run: claude mcp add --scope user revit -- `"$(Join-Path (Get-AppDir $Scope) 'mcp-server.exe')`" --mode local" }
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
        # Download to a UNIQUE per-run path. A fixed %TEMP%\<asset>.zip name meant a leftover from an
        # interrupted run (Ctrl+C leaves the partial file, and AV may still hold it open) blocked every
        # retry with "Access is denied" on the -OutFile write. A GUID name can never collide, and the
        # finally block below removes this run's file; any orphaned partials are named uniquely and get
        # swept by Windows' normal %TEMP% cleanup.
        $zipPath = Join-Path $env:TEMP ("mcpbridge-release-$([guid]::NewGuid()).zip")
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
    $packageManifest = Read-PackageManifest $extractDir
    if ($LocalPackagePath -and $packageManifest -and $packageManifest.PSObject.Properties['version'] -and $packageManifest.version) {
        # A local package that carries a manifest names its own version; the timestamp tag is only
        # for hand-built zips without one.
        $releaseTag = [string]$packageManifest.version
    }
    # The pending manifest parks the hashes of staged (deferred) add-in payloads, so the version
    # marker records them only once ApplyPendingUpdate has put the files on disk.
    $pendingComponents = @{}
    $installedComponents = @{}
    # Prior hashes carry forward only when this package has a manifest to compare them against. A
    # package without one redeploys every component, so a hash it cannot vouch for must not survive
    # into the marker (review of #174: a hand-built zip after a real release left the release's
    # hashes in place, and the next real install then skipped the dev build as "already current").
    if ($packageManifest -and $marker -and $marker.PSObject.Properties['components'] -and $marker.components) {
        foreach ($prop in $marker.components.PSObject.Properties) { $installedComponents[$prop.Name] = $prop.Value }
    }
    $skippedVersions = @()
    $unchangedVersions = @()
    foreach ($version in $detectedVersions) {
        $payloadDir = Join-Path $extractDir "addin-$version"
        if (-not (Test-Path $payloadDir)) {
            $skippedVersions += $version
            continue
        }
        $dllPresent = Test-Path (Join-Path (Get-AddinsDir $version $Scope) 'MCPBridge.AddIn.dll')
        if (Test-ComponentUnchanged $packageManifest $marker "addin-$version" $dllPresent) {
            # Same bytes as what is installed: nothing to deploy, and -- the point of the manifest --
            # no reason to touch a running Revit for this version.
            $unchangedVersions += $version
            continue
        }

        # The connector's own script API ships its XML-doc sidecar beside its DLL, and the sidecar is
        # LOAD-BEARING rather than cosmetic (issue #91): DiscoveryReflector treats a MISSING sidecar as
        # "everything is documented", so a payload with the DLL and no .xml installs cleanly, reports
        # nothing, and gives every agent a fully discoverable API with empty summaries. That is a worse
        # product than not shipping the API at all, and nothing downstream would surface it.
        #
        # Today the .xml arrives via MSBuild's default related-file copying for a transitive
        # ProjectReference. That is exactly the kind of default that changes silently under a toolchain
        # upgrade, so it is asserted here rather than assumed. Fails the install loudly instead.
        $connectorDll = Join-Path $payloadDir 'Eichler.Connectors.Revit.dll'
        $connectorXml = Join-Path $payloadDir 'Eichler.Connectors.Revit.xml'
        if ((Test-Path $connectorDll) -and -not (Test-Path $connectorXml)) {
            throw "The Revit $version payload has Eichler.Connectors.Revit.dll but no matching .xml doc sidecar. Installing it would make the connector's own API discoverable with empty summaries. This is a packaging bug -- do not work around it by deleting the DLL."
        }

        $proc = Get-RevitProcess $version
        if ($proc) {
            # Three ways this version can end up still running, all of which must reach the SAME
            # deferred-update path: the user declines to close it, a -Silent force-close fails, or an
            # accepted interactive close fails. Only the first was handled before, so a failed close
            # fell through to the deploy below and either aborted on locked DLLs or reported success
            # for a version still running the old code. See Stop-RevitProcessGracefully's own comment.
            $defer = $false
            if ($Silent) {
                $defer = -not (Stop-RevitProcessGracefully $proc)
            } else {
                $answer = Read-Host "Revit $version is running and must close to update it. Close it now? [Y/n]"
                if ($answer -eq 'n') {
                    $defer = $true
                } else {
                    $defer = -not (Stop-RevitProcessGracefully $proc)
                    if ($defer) {
                        Write-Host "Revit $version didn't close -- it may have an unsaved-changes prompt or another dialog open."
                    }
                }
            }

            if ($defer) {
                $pendingDir = Join-Path (Get-PendingUpdateDir $Scope) "addin-$version"
                New-Item -ItemType Directory -Force -Path $pendingDir | Out-Null
                Copy-Item "$payloadDir\*" $pendingDir -Force -Recurse
                if (-not $Silent) {
                    Write-Host "Revit $version is still running -- it'll finish updating automatically as soon as you close it."
                }
                $deferredVersions += $version
                $deferredProcessIds += @($proc | ForEach-Object { $_.Id })
                $h = Get-ManifestComponentHash $packageManifest "addin-$version"
                if ($h) { $pendingComponents["addin-$version"] = $h }
                continue
            }
        }

        $dir = Get-AddinsDir $version $Scope
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Copy-Item "$payloadDir\*" $dir -Force -Recurse
        $deployedVersions += $version
        $h = Get-ManifestComponentHash $packageManifest "addin-$version"
        if ($h) { $installedComponents["addin-$version"] = $h }
    }
    if ($skippedVersions.Count -gt 0) {
        Write-Host "This release doesn't include a build for Revit $($skippedVersions -join ', ') -- skipping it."
    }
    # "Doesn't support any of them" means NO payload existed for any detected version -- not that
    # every payload was already installed (review of the seed plan: the old check threw on the
    # nothing-changed case too, which a corpus-only release makes the common case).
    if ($deployedVersions.Count -eq 0 -and $deferredVersions.Count -eq 0 -and $unchangedVersions.Count -eq 0) {
        throw "Found Revit $($detectedVersions -join ', ') on this machine, but this release doesn't support any of them yet. Check for a newer release or contact support."
    }

    New-Item -ItemType Directory -Force -Path $appDir | Out-Null
    $serverPayloadDir = Join-Path $extractDir 'server'
    $serverExe = Join-Path $appDir 'mcp-server.exe'
    $brokerOutcome = $null
    if (Complete-PendingBrokerSwap $appDir) { $brokerOutcome = 'swapped' }
    if (Test-Path $serverPayloadDir) {
        if (Test-ComponentUnchanged $packageManifest $marker 'server' (Test-Path $serverExe)) {
            if (-not $brokerOutcome) { $brokerOutcome = 'unchanged' }
        } else {
            $brokerOutcome = Install-BrokerStaged $serverPayloadDir $appDir
            $h = Get-ManifestComponentHash $packageManifest 'server'
            if ($h) {
                if ($brokerOutcome -eq 'pending') { $pendingComponents['server'] = $h } else { $installedComponents['server'] = $h }
            }
        }
    }

    # Create the broker's private app-data root now, so it exists before the first launch. The broker
    # materializes its ranking-models cache and local how-to corpus here, and (in local mode) writes
    # broker.json/broker.lock here -- what singleton.AppDataDir() resolves on Windows. The broker
    # would create it lazily too, but setting it up here pairs with the uninstall cleanup above so the
    # installer owns this directory's whole lifecycle.
    New-Item -ItemType Directory -Force -Path "$env:LocalAppData\Connectors\Revit" | Out-Null

    Copy-SelfIfNeeded $ScriptPath $selfCopyPath
    # The marker is written whenever this run accounted for the release: something deployed, or
    # everything was already current (which must still move `version` forward, or the next run
    # would download the same release again -- the re-download-forever path the seed plan's review
    # named). A run where every add-in was deferred and nothing else changed is the exception: the
    # release is not installed yet, it is pending, and the watcher writes the marker when it lands.
    $accounted = ($deployedVersions.Count -gt 0) -or ($unchangedVersions.Count -gt 0) -or ($brokerOutcome -eq 'swapped') -or ($brokerOutcome -eq 'staged')
    if ($accounted) {
        # Recorded SEPARATELY, not merged: the idempotency check above needs `deployed` to know which
        # versions must have a DLL on disk, and `deployed` + `skipped` to know which versions this
        # release accounted for at all. Merging them into one list is what made the first attempt at
        # this fix a no-op -- see the comment there. Unchanged versions have their DLL on disk, so
        # they belong in `deployed`.
        $howtoCorpus = if ($packageManifest -and $packageManifest.PSObject.Properties['howto_corpus']) { $packageManifest.howto_corpus } else { $null }
        @{
            version  = $releaseTag
            deployed = @(@($deployedVersions) + @($unchangedVersions) | Sort-Object -Unique)
            skipped  = @($skippedVersions | Sort-Object -Unique)
            # Staged but not yet applied; the watcher task finishes these. Recorded so they don't
            # read as new-since-last-install on every subsequent run.
            deferred = @($deferredVersions | Sort-Object -Unique)
            # Per-component content hashes actually on disk (manifest.json's), so the next run can
            # skip what did not change. Absent for a package without a manifest.
            components = $installedComponents
            howto_corpus = $howtoCorpus
        } | ConvertTo-Json | Out-File $versionMarkerPath -Encoding utf8
    }
    if ($deferredVersions.Count -gt 0) {
        $manifestPath = Get-PendingUpdateManifestPath $Scope
        @{ version = $releaseTag; versions = $deferredVersions; components = $pendingComponents } | ConvertTo-Json | Out-File $manifestPath -Encoding utf8
        Register-PendingUpdateWatcher $Scope $selfCopyPath $deferredProcessIds
    }
} finally {
    if ($extractDir -and (Test-Path $extractDir)) { Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($zipDownloaded -and (Test-Path $zipPath)) { Remove-Item $zipPath -Force -ErrorAction SilentlyContinue }
}

if (-not $accounted) {
    # Every detected+shippable version was deferred (still running, user declined to close it now)
    # and nothing else changed -- the watcher task takes it from here; nothing more to do this run.
    return
}

# --- Register the MCP Server with the user's Claude clients -----------------------------------------
# Connect the freshly deployed broker to Claude Code CLI + Claude Desktop/Cowork (see Register-McpServer).
# (The Mac+Parallels remote-mode dev topology registers on the Mac side via install-mac.sh, not here.)
Register-McpServer $serverExe

# --- Confirm the broker carries the search-ranking embedding models --------------------------------
# A release built by release.yml always fetches the models before building, so a normal install has
# them; only a hand-built -LocalPackagePath zip made without the fetch-models step would not -- and
# then search_functions/search_howtos would silently rank LEXICAL-ONLY. `-search-models` prints one
# line and exits, so this is a cheap probe; it exits non-zero when the models are missing, which under
# $ErrorActionPreference='Stop' does NOT throw for a native exe (only $LASTEXITCODE is set), so guard
# on the printed text rather than the exit code. Warn, don't fail: a lexical-only broker still works.
if (Test-Path $serverExe) {
    # This is a courtesy probe at the very end of an otherwise-successful install, so it must never be
    # able to abort it: run the broker in a job with a timeout (a corrupt or wrong-arch exe could throw
    # on launch; a wedged one could hang forever) and warn only on a definitive "not bundled" answer.
    # If the probe can't run or times out, stay silent -- the install already succeeded.
    $modelsLine = $null
    try {
        $probe = Start-Job -ScriptBlock { param($exe) & $exe -search-models 2>&1 | Select-Object -First 1 } -ArgumentList $serverExe
        if (Wait-Job $probe -Timeout 20) { $modelsLine = (Receive-Job $probe | Select-Object -First 1) } else { Stop-Job $probe -ErrorAction SilentlyContinue }
        Remove-Job $probe -Force -ErrorAction SilentlyContinue
    } catch { $modelsLine = $null }
    if ($modelsLine -and "$modelsLine" -notmatch 'bundled') {
        Write-Host "WARNING: the installed broker has NO search-ranking models bundled -- search_functions/search_howtos will rank keyword-only. Expected only for a -LocalPackagePath build made without fetch-models; a real release always includes them. ($modelsLine)"
    }
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

$parts = @()
if ($deployedVersions.Count -gt 0) { $parts += "installed for Revit $($deployedVersions -join ', ')" }
if ($unchangedVersions.Count -gt 0) { $parts += "add-in already current for Revit $($unchangedVersions -join ', ') (left untouched)" }
$summary = "Revit MCP Bridge $releaseTag"
if ($parts.Count -gt 0) { $summary += ": $($parts -join '; ')" }
$summary += "."
if ($deferredVersions.Count -gt 0) { $summary += " Revit $($deferredVersions -join ', ') will update automatically once you close it." }
switch ($brokerOutcome) {
    'swapped'   { $summary += " Broker updated." }
    'staged'    { $summary += " Broker updated on disk, but a running broker is still serving the previous version: it takes effect when your MCP client next starts it -- reconnect the revit MCP server (e.g. /mcp in Claude Code) or restart the client. The Revit ribbon shows the update as available until then." }
    'pending'   {
        $summary += " The new broker is waiting as mcp-server.exe.new; re-run this installer once no broker is running to finish the swap."
        if (-not (Test-Path $serverExe)) { $summary += " Until then there is NO mcp-server.exe in place (the old one was moved aside before the swap was refused), so a new MCP client session cannot start the broker." }
    }
    'unchanged' { $summary += " Broker already current." }
}
Write-Host $summary

} catch {
    # Turn any terminating error into a clear, actionable message instead of a raw PowerShell error
    # record surfacing through `irm | iex`. The crafted `throw`s above already carry a friendly
    # sentence; anything else (a dropped connection, a rate-limited GitHub API, a corrupt download, a
    # locked or ACL-blocked file) arrives here as its raw exception, so map the common shapes to a hint.
    $msg = $_.Exception.Message
    $hint = switch -Regex ($_.Exception.GetType().FullName + ' ' + $msg) {
        'remote name|could not be resolved|Unable to connect|HttpRequest|WebException|SocketException' {
            "`n  Check your internet connection and re-run. If it persists, GitHub may be rate-limiting this network -- wait a few minutes." }
        '\(403\)|rate limit' {
            "`n  GitHub is rate-limiting anonymous downloads from this network. Wait a few minutes and re-run." }
        'Central Directory|Expand-Archive|corrupt|Zip' {
            "`n  The downloaded package looks corrupt -- re-run to download it again." }
        'Access to the path|UnauthorizedAccess|used by another process|is denied' {
            "`n  Access was denied to a file. If it happened while downloading, your %TEMP% may be full or locked (an antivirus scan of a leftover download) -- clear it and re-run. Otherwise a Revit add-in file is in use: close Revit and any running MCP client, then re-run (or try again as Administrator with -Scope AllUsers)." }
        default { '' }
    }
    Write-Host ''
    Write-Host "Revit MCP Bridge install did not complete: $msg$hint"
    exit 1
} finally {
    if ($BootstrapCreated -and (Test-Path $ScriptPath)) { Remove-Item $ScriptPath -Force -ErrorAction SilentlyContinue }
}
