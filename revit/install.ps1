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

    # The ribbon's self-update click passes this: no interactive prompts (an uninstall closes a running
    # Revit rather than asking), no "already up to date" chatter on the common no-op case. An add-in
    # UPDATE never closes anything in either mode (see "Versioned add-in layout" below).
    [switch]$Silent,

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
# for the ribbon's Update Now) goes through $ScriptPath instead, which is always a real
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
# copy with arguments (the ribbon's Update Now with -Update -Silent, Apps & Features' -Uninstall) lost
# them: Update Now ran an interactive install in a
# hidden window and hung on Read-Host. Found live on the first Update Now against a real release.
# Get-InstallerSourceForBootstrap now uses the definition only if it IS the full script, and otherwise
# fetches the canonical script from the raw URL (what the stub did anyway, minus the argument loss),
# validating either way; Copy-SelfIfNeeded refuses to install anything but the full script.
$InstallerRawUrl = "https://raw.githubusercontent.com/$RepoSlug/main/revit/install.ps1"

# The one-liner stub is ~90 bytes with no param block; the real script declares -LoadFunctionsOnly and
# defines Copy-SelfIfNeeded. Those two markers reject the stub, but both sit in the first few KB, so
# (independent review of #193) they say nothing about the REST of the text: a download cut off at 70%
# carried both and would have been installed as the self-copy, to fail later as a bare ParserError in
# a hidden window -- the same shape #192 was filed for. So the text must also END with the sentinel
# comment on this file's last line (a cut anywhere above loses it, including a cut between two
# complete statements that the parser would accept) AND parse as complete PowerShell (a corrupted
# middle with an intact tail). Belt and braces, each covering what the other cannot.
function Test-IsFullInstallerScript([string]$Text) {
    if (-not $Text) { return $false }
    if (-not (($Text -match '(?m)^\s*\[switch\]\$LoadFunctionsOnly') -and ($Text -match 'function Copy-SelfIfNeeded'))) {
        return $false
    }
    if ($Text.TrimEnd() -notmatch '# MCPBRIDGE-INSTALL-PS1-END-OF-FILE$') {
        return $false
    }
    $tokens = $null; $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$parseErrors) | Out-Null
    return ($null -eq $parseErrors) -or ($parseErrors.Count -eq 0)
}

# Piped invocations other than the documented one-liner (e.g. `Get-Content saved.ps1 | iex`) also reach
# the download branch, since their real source is equally unrecoverable from $MyInvocation; the
# self-copy is then main's current script rather than the text that ran. Acceptable: the self-copy's
# job is to be a complete installer for Update Now/uninstall to run, and main is what the one-liner
# installs anyway. Run a saved copy with -File to keep it byte-for-byte.
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

# Per-version, by the process's own exe path (which encodes the year), not by process name -- a
# name-based check cannot tell 2026's Revit.exe from 2027's. Used to say which Revit years are still
# running the previous add-in after an update (they are never closed for it), to decide whether to
# launch Revit after a fresh install, and by uninstall. Review finding: .Path resolves via MainModule
# and can throw for a bitness-mismatched process -- guarded per-item so one inaccessible process
# doesn't abort the whole check for every other running Revit.
function Get-RevitProcess([string]$RevitVersion) {
    Get-Process -Name 'Revit' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -like "*\Revit $RevitVersion\Revit.exe" } catch { $false }
    }
}

# UNINSTALL ONLY. An add-in update never closes Revit (the versioned layout below deploys beside the
# running add-in and flips a pointer); uninstall is the one operation that has to remove a DLL a
# running Revit may have mapped, so it asks that Revit to close first. Returns $true only if every
# passed process is ACTUALLY gone afterwards: CloseMainWindow() is a request, not a kill, and it
# routinely fails -- the user hits Cancel on a "save changes?" prompt, a modal dialog owns the UI
# thread, or there is no main window to send WM_CLOSE to. Uninstall does not rely on this value; it
# tests what survived removal and reports it for a second run.
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

# Only copies $ScriptPath onto $selfCopyPath when they aren't already the same file -- review
# finding: when this script IS the deployed copy re-invoking itself (the ribbon's -Update -Silent
# self-update path), $PSCommandPath and $selfCopyPath resolve to the identical
# file, and Copy-Item onto itself is a terminating error under $ErrorActionPreference = 'Stop',
# silently aborting everything after it (MCP re-registration, the registry version bump, relaunching
# Revit) while still having already deployed the new files and written the version marker.
#
# Issue #192: refuses a source that is not the full installer. The self-copy is what Update Now and
# Apps & Features both run WITH ARGUMENTS, so a stub here (the one-liner, which is what a piped run's
# $MyInvocation.MyCommand.Definition actually is) breaks both at once and silently. Guarding at the point of the write means no invocation path can regress this again.
# Deletes install scratch (the extracted payload, the downloaded zip) without ever throwing: a few
# short retries for a transient lock (antivirus scanning a just-written exe), then give up quietly.
# See the deploy block's finally for the failure this replaced.
function Remove-ScratchBestEffort([string]$Path, [switch]$Recurse) {
    if (-not $Path -or -not (Test-Path $Path)) { return }
    for ($i = 1; $i -le 4; $i++) {
        try {
            Remove-Item $Path -Recurse:$Recurse -Force -ErrorAction Stop
            return
        } catch {
            if ($i -lt 4) { Start-Sleep -Milliseconds (300 * $i) }
        }
    }
}

function Copy-SelfIfNeeded([string]$Source, [string]$Destination) {
    if (-not (Test-IsFullInstallerScript (Get-Content $Source -Raw))) {
        throw "Refusing to install '$Source' as the installer's own copy at '$Destination': it is not the full install.ps1 (issue #192). Update Now and uninstall would both lose their arguments."
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
    # or $null. Components are whatever addin-*/, shim-*/ and server/ directories the stage holds.
    $components = [ordered]@{}
    Get-ChildItem $StageDir -Directory | Where-Object { $_.Name -like 'addin-*' -or $_.Name -like 'shim-*' -or $_.Name -eq 'server' } | Sort-Object Name | ForEach-Object {
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
    #               mcp-server.exe.new and the next run of this script completes the swap once
    #               nothing holds the file.
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
    # Already swapped (issue #192's live test): a previous run moved the old image to .old and the new
    # one into place, but the old brokers kept running FROM .old, so that .old stays locked and every
    # later run's `exe -> .old` move below fails -- reported as 'pending' forever, with the version
    # marker never recording the new server hash and the summary asking for a re-run that can never
    # succeed. If the exe on disk already IS the payload, there is nothing to move: drop the staging
    # copy and report what is true.
    if ((Get-FileHash $exe -Algorithm SHA256).Hash -eq (Get-FileHash $new -Algorithm SHA256).Hash) {
        Remove-Item $new -Force -ErrorAction SilentlyContinue
        if ($running.Count -gt 0) { return 'staged' }
        Remove-StaleBrokerImages $AppDir
        return 'swapped'
    }
    # The move-aside name is not fixed (second live update, v0.1.1 -> v0.1.2): with an older broker
    # still running from .old, that name is locked and the move onto it fails, so the whole swap was
    # 'pending' until EVERY older broker exited -- a v0.1.2 parked as .new while clients kept starting
    # the on-disk v0.1.1, and the ribbon kept saying "update available" after the update. When .old is
    # still there after the delete above, it is in use: park the current image under a unique name
    # instead. Remove-StaleBrokerImages sweeps every .old* once its process has gone.
    $aside = $old
    if (Test-Path $old) { $aside = "$exe.old-$([guid]::NewGuid().ToString('N').Substring(0, 8))" }
    try {
        Move-Item $exe $aside -Force
        Move-Item $new $exe -Force
    } catch {
        return 'pending'
    }
    if ($running.Count -gt 0) { return 'staged' }
    Remove-StaleBrokerImages $AppDir
    return 'swapped'
}

# After a pending swap has completed: moves the server hash the deferring run left in
# mcp-server.exe.new.sha256 into the version marker's components, so the marker describes what is on
# disk (review of #196). No sidecar, no marker -> nothing to do. Never throws: bookkeeping must not
# fail an install whose files are already right.
function Complete-PendingServerMarker([string]$AppDir, [string]$MarkerPath) {
    $sidecar = Join-Path $AppDir 'mcp-server.exe.new.sha256'
    try {
        if (-not (Test-Path $sidecar)) { return }
        $hash = (Get-Content $sidecar -Raw).Trim()
        if ($hash -and (Test-Path $MarkerPath)) {
            $marker = Get-Content $MarkerPath -Raw | ConvertFrom-Json
            if (-not $marker.PSObject.Properties['components'] -or -not $marker.components) {
                $marker | Add-Member -NotePropertyName components -NotePropertyValue ([pscustomobject]@{}) -Force
            }
            $marker.components | Add-Member -NotePropertyName server -NotePropertyValue $hash -Force
            $marker | ConvertTo-Json -Depth 5 | Out-File $MarkerPath -Encoding utf8
        }
        Remove-Item $sidecar -Force -ErrorAction SilentlyContinue
    } catch { }
}

# Deletes every parked previous broker image (mcp-server.exe.old, .old-xxxxxxxx) that no process
# holds any more; the ones still running stay, silently, until the next run. Never throws.
function Remove-StaleBrokerImages([string]$AppDir) {
    Get-ChildItem $AppDir -Filter 'mcp-server.exe.old*' -File -ErrorAction SilentlyContinue | ForEach-Object {
        try { Remove-Item $_.FullName -Force -ErrorAction Stop } catch { }
    }
}

function Complete-PendingBrokerSwap([string]$AppDir) {
    # Finishes a 'pending' outcome from an earlier run, and tidies the .old image a 'staged' swap
    # left behind once its broker has exited (the removal is refused, harmlessly, while it is still
    # mapped). Runs on every install, not only when the server changes -- found live: a run that
    # skipped an unchanged broker left the previous run's .old in place. Returns $true when a swap
    # happened.
    # Returns 'swapped', 'staged' (the new image is in place but a running broker still serves the
    # previous one -- same meaning as Install-BrokerStaged's), or $false when nothing was pending or
    # the move was refused. A running broker no longer blocks this (second live update): the current
    # image is parked under a unique name, exactly as Install-BrokerStaged does, so a .new left by an
    # earlier 'pending' run lands on the next run instead of waiting for every broker to exit.
    $exe = Join-Path $AppDir 'mcp-server.exe'
    $new = "$exe.new"
    $old = "$exe.old"
    Remove-StaleBrokerImages $AppDir
    if (-not (Test-Path $new)) { return $false }
    $running = @(Get-BrokerProcess $AppDir)
    try {
        if (Test-Path $exe) {
            $aside = if (Test-Path $old) { "$exe.old-$([guid]::NewGuid().ToString('N').Substring(0, 8))" } else { $old }
            Move-Item $exe $aside -Force
        }
        Move-Item $new $exe -Force
    } catch {
        return $false
    }
    if ($running.Count -gt 0) { return 'staged' }
    Remove-StaleBrokerImages $AppDir
    return 'swapped'
}

# --- Versioned add-in layout: stable shim + addin\<version>\<year>\ (docs/self-update-architecture.md §4,
# issue #211) ------------------------------------------------------------------------------------------
# Revit loads a small, rarely-changing MCPBridge.Shim.dll from Addins\<year>; the shim reads
# <app dir>\addin\current.json and Assembly.LoadFrom's the real add-in out of addin\<version>\<year>\.
# An add-in update is therefore: stage the new version folder beside whatever a running Revit has
# mapped, then flip the pointer -- atomically, last, with nothing asked to close. A running Revit keeps
# its version until its next start. This is the ONLY add-in layout: every release ships shim-<year>/
# beside addin-<year>/, and a payload without its shim is a packaging error the deploy loop refuses,
# not a case it falls back from (the pre-shim flat deploy, the flat->shim migration and the
# close/defer machinery they needed were deleted while the project had no installs to migrate --
# docs/http-transport-rfc.md §4c).

function Get-AddinVersionsRoot([string]$AppDir) { Join-Path $AppDir 'addin' }
function Get-AddinPointerPath([string]$AppDir) { Join-Path (Get-AddinVersionsRoot $AppDir) 'current.json' }
function Get-AddinVersionDir([string]$AppDir, [string]$Version, [string]$RevitVersion) {
    Join-Path (Join-Path (Get-AddinVersionsRoot $AppDir) $Version) $RevitVersion
}

function Read-AddinPointer([string]$AppDir) {
    # The parsed current.json ({ version; previous? }), or $null when absent, unreadable, or without a
    # version. Get-Content -Raw | ConvertFrom-Json tolerates a UTF-8 BOM (Windows PowerShell's Out-File
    # -Encoding utf8 writes one), as does the shim's own reader.
    $path = Get-AddinPointerPath $AppDir
    if (-not (Test-Path $path)) { return $null }
    try {
        $p = Get-Content $path -Raw | ConvertFrom-Json
        if ($p -and $p.PSObject.Properties['version'] -and $p.version) { return $p }
    } catch { }
    return $null
}

function Write-AddinPointer([string]$AppDir, [string]$Version) {
    # The atomic "apply" step (§4.4): write a sibling temp file, then rename it over current.json, so a
    # reader never sees a half-written pointer. `previous` remembers the version this replaces, which
    # is what Remove-StaleAddinVersions retains alongside the current one (§4.5). UTF-8 without a BOM.
    $root = Get-AddinVersionsRoot $AppDir
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $path = Get-AddinPointerPath $AppDir
    $prior = Read-AddinPointer $AppDir
    $previous = $null
    if ($prior) {
        if ($prior.version -ne $Version) { $previous = [string]$prior.version }
        elseif ($prior.PSObject.Properties['previous'] -and $prior.previous) { $previous = [string]$prior.previous }
    }
    $pointer = [ordered]@{ version = $Version }
    if ($previous) { $pointer['previous'] = $previous }
    $tmp = "$path.tmp-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    [IO.File]::WriteAllText($tmp, ($pointer | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
    # File.Replace is ReplaceFile (atomic on NTFS; a rename on Unix). [NullString]::Value, not $null:
    # PowerShell turns $null into "" for a .NET string parameter, and "" is "not a legal path" here.
    # A Revit starting at this very instant has current.json open for its one read, and ReplaceFile
    # refuses while it does (IOException) -- a few short retries, since that read is milliseconds long.
    if (-not (Test-Path $path)) { [IO.File]::Move($tmp, $path); return }
    for ($i = 1; ; $i++) {
        try { [IO.File]::Replace($tmp, $path, [NullString]::Value); return }
        catch [System.IO.IOException] {
            if ($i -ge 5) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue; throw }
            Start-Sleep -Milliseconds (100 * $i)
        }
    }
}

function Test-ShimAddinInstalled([string]$AddinsDir) {
    (Test-Path (Join-Path $AddinsDir 'MCPBridge.Shim.dll')) -and (Test-Path (Join-Path $AddinsDir 'MCPBridge.addin'))
}

function Test-VersionedAddinInstalled([string]$AppDir, [string]$AddinsDir, [string]$RevitVersion) {
    # Complete for this Revit year when the shim + its manifest are in Addins\<year>, the pointer reads,
    # and the folder it points at holds this year's real add-in. This is "the add-in is on disk for
    # this year" -- what the idempotency check asks.
    if (-not (Test-ShimAddinInstalled $AddinsDir)) { return $false }
    $p = Read-AddinPointer $AppDir
    if (-not $p) { return $false }
    Test-Path (Join-Path (Get-AddinVersionDir $AppDir ([string]$p.version) $RevitVersion) 'MCPBridge.AddIn.dll')
}

function Install-AddinVersionPayload([string]$PayloadDir, [string]$AppDir, [string]$Version, [string]$RevitVersion) {
    # Lays down addin\<Version>\<year>\ from the release's addin-<year>/ payload, verbatim. Touches
    # neither Addins\<year> nor the pointer, so a running Revit is never contended -- a NEW version
    # always lands in a folder nothing has mapped. Only re-staging the SAME version while a Revit runs it
    # (a dev -LocalPackagePath zip whose manifest names a fixed version; never a release, whose tag is
    # new each time) hits the mapped files, and that is reported for what it is.
    $dest = Get-AddinVersionDir $AppDir $Version $RevitVersion
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    try {
        Copy-Item "$PayloadDir\*" $dest -Force -Recurse -ErrorAction Stop
    } catch {
        throw "Could not write the Revit $RevitVersion add-in into $dest ($($_.Exception.Message)). A running Revit $RevitVersion is loaded from that same version folder; close it and re-run, or give the package a new version."
    }
}

function Install-AddinShim([string]$ShimDir, [string]$AddinsDir) {
    # Puts MCPBridge.Shim.dll + the shim's MCPBridge.addin into Addins\<year>. Returns 'placed',
    # 'unchanged' (same bytes already there -- the common case, since the shim changes only when its
    # contract does, §4.2), or 'held' (a DIFFERENT shim is mapped by a running Revit, so the copy was
    # refused; the pointer flip still applied, the shim refresh is retried on a later run).
    New-Item -ItemType Directory -Force -Path $AddinsDir | Out-Null
    $pairs = @(
        @{ Src = (Join-Path $ShimDir 'MCPBridge.Shim.dll'); Dst = (Join-Path $AddinsDir 'MCPBridge.Shim.dll') },
        @{ Src = (Join-Path $ShimDir 'MCPBridge.addin');    Dst = (Join-Path $AddinsDir 'MCPBridge.addin') }
    )
    $todo = @($pairs | Where-Object {
        -not (Test-Path $_.Dst) -or ((Get-FileHash $_.Dst -Algorithm SHA256).Hash -ne (Get-FileHash $_.Src -Algorithm SHA256).Hash)
    })
    if ($todo.Count -eq 0) { return 'unchanged' }
    foreach ($pair in $todo) {
        try { Copy-Item $pair.Src $pair.Dst -Force -ErrorAction Stop } catch { return 'held' }
    }
    return 'placed'
}

function Remove-OwnedAddinFiles([string]$AddinsDir) {
    # Removes what this installer puts in Addins\<year> -- the shim (MCPBridge.Shim.dll) and its
    # MCPBridge.addin manifest -- and nothing else; the versioned payloads live under $appDir\addin\ and
    # go with the app dir. The folder itself goes only when that leaves it empty: a third-party add-in
    # whose manifest lives in the all-users Addins location can still drop its DLLs into this per-user
    # folder, and whole-folder removal would delete them. Silent on a file a running Revit holds:
    # callers test what survived (Test-ShimAddinInstalled) to find that out.
    if (-not (Test-Path (Join-Path $AddinsDir 'MCPBridge.addin'))) { return }
    foreach ($name in 'MCPBridge.Shim.dll', 'MCPBridge.addin') {
        Remove-Item (Join-Path $AddinsDir $name) -Force -ErrorAction SilentlyContinue
    }
    if (-not @(Get-ChildItem $AddinsDir -Force -ErrorAction SilentlyContinue)) {
        Remove-Item $AddinsDir -Force -ErrorAction SilentlyContinue
    }
}

function Remove-StaleAddinVersions([string]$AppDir) {
    # §4.5, the add-in twin of Remove-StaleBrokerImages: on any run, drop every addin\<v>\ that is
    # neither the pointer's current version nor the one before it (a Revit not restarted since the last
    # update is still running `previous`, and its lazy loads -- Roslyn, satellite resources, the SQLite
    # native -- must keep finding their files). A folder a running Revit has mapped cannot be RENAMED
    # on Windows, so rename-first is the in-use test: a refused rename skips the folder whole (never a
    # half-deleted version), a successful one proves nothing holds it and the delete follows. Never
    # throws; anything refused is retried next run. The shim ignores *.stale-* names.
    $root = Get-AddinVersionsRoot $AppDir
    if (-not (Test-Path $root)) { return }
    $p = Read-AddinPointer $AppDir
    if (-not $p) { return }   # no pointer: nothing is known to be current, so nothing is known to be stale.
    $keep = @([string]$p.version)
    if ($p.PSObject.Properties['previous'] -and $p.previous) { $keep += [string]$p.previous }
    foreach ($d in @(Get-ChildItem $root -Directory -ErrorAction SilentlyContinue)) {
        if ($keep -contains $d.Name) { continue }
        $aside = $d.FullName
        if ($d.Name -notlike '*.stale-*') {
            $aside = "$($d.FullName).stale-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
            try { Move-Item $d.FullName $aside -Force -ErrorAction Stop } catch { continue }
        }
        try { Remove-Item $aside -Recurse -Force -ErrorAction Stop } catch { }
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

# --- Uninstall ------------------------------------------------------------------------------------
if ($Uninstall) {
    # Review finding: the original version deleted files unconditionally and reported "uninstalled"
    # even when a running Revit had one of them locked, silently leaving it behind. Uninstall is the
    # ONE operation that asks a running Revit to close: it has to remove the shim DLL that Revit has
    # mapped (an update never does -- it deploys beside the running add-in and flips a pointer).
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
        # The shim + its manifest; the versioned payloads live under $appDir\addin\ and go with the app
        # dir below. See Remove-OwnedAddinFiles for why the folder itself goes only when that empties it.
        Remove-OwnedAddinFiles $dir
        # The shim DLL surviving removal means a running Revit held it locked -- the "close it and
        # re-run" case below. Revit doesn't hold the .addin manifest open, so the loaded DLL is the
        # reliable signal.
        if (Test-Path (Join-Path $dir 'MCPBridge.Shim.dll')) { $leftoverVersions += $version }
    }
    # Takes the whole addin\<version>\<year>\ tree and current.json with it. A running Revit's mapped
    # version folder survives (silently) and is reported via $leftoverVersions' shim signal above.
    Remove-Item $appDir -Recurse -Force -ErrorAction SilentlyContinue
    # The broker keeps its private app-data root at %LOCALAPPDATA%\Connectors\Revit -- what
    # singleton.AppDataDir() resolves on Windows: the materialized search+how-to ranking models
    # (~24MB, a copy of bytes embedded in the exe being removed), the local how-to corpus, the
    # discovery cache, and -- in local mode -- the broker.json rendezvous file with its lock files
    # (broker.lock, the broker.lock.<n> generations and broker.election.lock of issue #212). None of it
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
$completedEarly = Complete-PendingBrokerSwap $appDir
if ($completedEarly) { Complete-PendingServerMarker $appDir $versionMarkerPath }
# Likewise for add-in version folders a previous run could not delete because a Revit still had them
# mapped (§4.5): retried on every run, before any short-circuit.
Remove-StaleAddinVersions $appDir
if ($completedEarly -and -not $Silent) {
    if ($completedEarly -eq 'staged') {
        Write-Host "Installed the broker update that was waiting. A running broker still serves the previous version until your MCP client restarts it (reconnect the revit MCP server, or restart the client)."
    } else {
        Write-Host "Finished a broker update that was waiting for the previous broker to exit."
    }
}

# Only require a DLL for versions the LAST INSTALL ACTUALLY COVERED. Checking every detected version
# is wrong whenever a release ships no `addin-<year>/` payload for one of them: the deploy loop below
# skips such a version by design, so its DLL never appears, so this check could never become true, so
# the "already up to date" short-circuit below could never fire. Every subsequent run would then
# re-download the release and re-enter the deploy loop. PRD §12's self-upgrade path would be
# re-installing a perfectly healthy install on every invocation. A release shipping 2027-only while
# 2025 is also installed is a realistic encounter.
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

if ($null -eq $deployedBefore) {
    $versionsNeedingDll = $detectedVersions
    $unaccountedVersions = @()
} else {
    $versionsNeedingDll = @($detectedVersions | Where-Object { $deployedBefore -contains $_ })
    $unaccountedVersions = @($detectedVersions | Where-Object {
        ($deployedBefore -notcontains $_) -and ($skippedBefore -notcontains $_)
    })
}

$allDllsPresent =
    ($unaccountedVersions.Count -eq 0) -and
    -not ($versionsNeedingDll | Where-Object {
        -not (Test-VersionedAddinInstalled $appDir (Get-AddinsDir $_ $Scope) $_)
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

    # --- Deploy, per detected+supported version. No Revit is closed, asked, or waited on (PRD §12
    # "Multi-version installs", self-update-architecture.md §4.4): each year's payload lands in a
    # version folder nothing has mapped, the pointer flips once, and a running Revit keeps its current
    # add-in until its own next start -- the summary names the years that still need that restart.
    $packageManifest = Read-PackageManifest $extractDir
    if ($LocalPackagePath -and $packageManifest -and $packageManifest.PSObject.Properties['version'] -and $packageManifest.version) {
        # A local package that carries a manifest names its own version; the timestamp tag is only
        # for hand-built zips without one.
        $releaseTag = [string]$packageManifest.version
    }
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
    $restartVersions = @()
    $shimHeldVersions = @()
    # Pass 1 below stages each year's payload under $appDir\addin\<tag>\<year>\ and flips the pointer;
    # pass 2 refreshes the shim in Addins\<year>.
    $shimYears = [ordered]@{}
    $flipPointer = $false
    foreach ($version in $detectedVersions) {
        $payloadDir = Join-Path $extractDir "addin-$version"
        if (-not (Test-Path $payloadDir)) {
            $skippedVersions += $version
            continue
        }
        # Every release carries shim-<year>/ beside addin-<year>/ (release.yml). A payload without its
        # shim cannot be deployed at all -- there is no flat fallback -- so it is a packaging error.
        $shimPayloadDir = Join-Path $extractDir "shim-$version"
        if (-not (Test-Path (Join-Path $shimPayloadDir 'MCPBridge.Shim.dll'))) {
            throw "The package has an addin-$version payload but no shim-$version\MCPBridge.Shim.dll beside it. Every release ships the shim for each add-in it carries; this is a packaging bug -- do not work around it by deploying the add-in into Revit's Addins folder."
        }
        $addinsDir = Get-AddinsDir $version $Scope
        # The real add-in sitting in Addins\<year> itself is a pre-shim deploy (or a dev deploy-and-verify
        # run): Revit would load it directly, beside or instead of the shim. Nothing here migrates that;
        # say so and stop before touching anything.
        if (Test-Path (Join-Path $addinsDir 'MCPBridge.AddIn.dll')) {
            throw "Revit $version's add-ins folder ($addinsDir) holds MCPBridge.AddIn.dll directly -- a pre-shim add-in deploy this installer no longer manages. Close Revit $version, remove that folder's MCPBridge.* files, and re-run."
        }
        if (Test-ComponentUnchanged $packageManifest $marker "addin-$version" (Test-VersionedAddinInstalled $appDir $addinsDir $version)) {
            # Same bytes as what is installed: nothing to deploy.
            $unchangedVersions += $version
            $shimYears[$version] = @{ Payload = $payloadDir; Shim = $shimPayloadDir; Changed = $false }
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

        # §4.4: the payload lands in a folder nothing has mapped, so no Revit is touched, asked, or
        # closed here. The pointer flips after this loop; the Addins\<year> shim refresh is pass 2.
        Install-AddinVersionPayload $payloadDir $appDir $releaseTag $version
        $h = Get-ManifestComponentHash $packageManifest "addin-$version"
        if ($h) { $installedComponents["addin-$version"] = $h }
        $shimYears[$version] = @{ Payload = $payloadDir; Shim = $shimPayloadDir; Changed = $true }
        $flipPointer = $true
    }

    # --- Pointer flip: the atomic "apply" for the versioned layout (§4.4). ---------------------------
    if ($flipPointer) {
        # Every version folder must be complete for every year this install serves: a year whose add-in
        # did not change still gets a copy under the new tag (same bytes, no process touched), or the
        # shim would have to fall back to an older folder that Remove-StaleAddinVersions is entitled
        # to delete two releases from now.
        foreach ($version in @($shimYears.Keys)) {
            if ($shimYears[$version].Changed) { continue }
            if (-not (Test-Path (Join-Path (Get-AddinVersionDir $appDir $releaseTag $version) 'MCPBridge.AddIn.dll'))) {
                Install-AddinVersionPayload $shimYears[$version].Payload $appDir $releaseTag $version
            }
        }
        Write-AddinPointer $appDir $releaseTag
        Remove-StaleAddinVersions $appDir
    }

    # --- Pass 2: the Addins\<year> side -- place or refresh the shim. --------------------------------
    foreach ($version in @($shimYears.Keys)) {
        $entry = $shimYears[$version]
        $dir = Get-AddinsDir $version $Scope
        # Refresh the shim only if its bytes changed -- never blocking on a running Revit: a held shim
        # keeps working (it still reads the pointer) and is retried next run.
        $shimOutcome = Install-AddinShim $entry.Shim $dir
        if ($shimOutcome -eq 'held') {
            $shimHeldVersions += $version
        } else {
            $h = Get-ManifestComponentHash $packageManifest "shim-$version"
            if ($h) { $installedComponents["shim-$version"] = $h }
        }
        if ($entry.Changed) {
            $deployedVersions += $version
            if (Get-RevitProcess $version) { $restartVersions += $version }
        }
    }

    if ($skippedVersions.Count -gt 0) {
        Write-Host "This release doesn't include a build for Revit $($skippedVersions -join ', ') -- skipping it."
    }
    # "Doesn't support any of them" means NO payload existed for any detected version -- not that
    # every payload was already installed (review of the seed plan: the old check threw on the
    # nothing-changed case too, which a corpus-only release makes the common case).
    if ($deployedVersions.Count -eq 0 -and $unchangedVersions.Count -eq 0) {
        throw "Found Revit $($detectedVersions -join ', ') on this machine, but this release doesn't support any of them yet. Check for a newer release or contact support."
    }

    New-Item -ItemType Directory -Force -Path $appDir | Out-Null
    $serverPayloadDir = Join-Path $extractDir 'server'
    $serverExe = Join-Path $appDir 'mcp-server.exe'
    $brokerOutcome = $null
    $completed = Complete-PendingBrokerSwap $appDir
    if ($completed) { $brokerOutcome = $completed }
    if (Test-Path $serverPayloadDir) {
        if (Test-ComponentUnchanged $packageManifest $marker 'server' (Test-Path $serverExe)) {
            if (-not $brokerOutcome) { $brokerOutcome = 'unchanged' }
        } else {
            $brokerOutcome = Install-BrokerStaged $serverPayloadDir $appDir
            $h = Get-ManifestComponentHash $packageManifest 'server'
            if ($h) {
                if ($brokerOutcome -eq 'pending') {
                    # Remembered beside the parked image so the run that finally completes the swap
                    # (possibly the "already up to date" short-circuit of a later run, which never
                    # reaches this block) can record the hash in the marker -- review of #196: without
                    # this the marker kept the previous server hash until the NEXT release.
                    Set-Content -Path "$serverExe.new.sha256" -Value $h -NoNewline
                } else {
                    $installedComponents['server'] = $h
                    Remove-Item "$serverExe.new.sha256" -Force -ErrorAction SilentlyContinue
                }
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
    # The marker is written on every run that gets here: something deployed, or everything was already
    # current (which must still move `version` forward, or the next run would download the same
    # release again -- the re-download-forever path the seed plan's review named). Nothing is ever
    # pending on the add-in side: the pointer flip above IS the install.
    #
    # `deployed` and `skipped` are recorded SEPARATELY, not merged: the idempotency check above needs
    # `deployed` to know which versions must have a DLL on disk, and `deployed` + `skipped` to know
    # which versions this release accounted for at all. Merging them into one list is what made the
    # first attempt at this fix a no-op -- see the comment there. Unchanged versions have their DLL on
    # disk, so they belong in `deployed`.
    $howtoCorpus = if ($packageManifest -and $packageManifest.PSObject.Properties['howto_corpus']) { $packageManifest.howto_corpus } else { $null }
    @{
        version  = $releaseTag
        deployed = @(@($deployedVersions) + @($unchangedVersions) | Sort-Object -Unique)
        skipped  = @($skippedVersions | Sort-Object -Unique)
        # Per-component content hashes actually on disk (manifest.json's), so the next run can
        # skip what did not change. Absent for a package without a manifest.
        components = $installedComponents
        howto_corpus = $howtoCorpus
    } | ConvertTo-Json | Out-File $versionMarkerPath -Encoding utf8
} finally {
    # Best-effort for real (issue #192's live test): Remove-Item -Recurse on a directory whose file is
    # still open -- here, the freshly extracted 86 MB mcp-server.exe under an antivirus scan -- throws a
    # TERMINATING "Access is denied" that -ErrorAction SilentlyContinue does not suppress. Thrown from
    # this finally, it replaced the deploy's normal completion and jumped to the top-level catch, so a
    # run that had already installed everything reported "did not complete" with exit 1 and never
    # reached MCP re-registration, the uninstall key, or the summary. Scratch that cannot be deleted
    # right now is a leak of a few files in %TEMP%, never a reason to fail the install.
    Remove-ScratchBestEffort $extractDir -Recurse
    if ($zipDownloaded) { Remove-ScratchBestEffort $zipPath }
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

# Start Revit for the user after an install that deployed to a version none of whose Revit is running
# (a fresh install). Never when a Revit is already up -- the versioned layout deploys UNDER a running
# Revit and a second instance is not what anyone asked for.
$launchVersion = @($deployedVersions | Where-Object { -not (Get-RevitProcess $_) } | Select-Object -First 1)
if (-not $Silent -and $launchVersion.Count -gt 0) {
    Start-Process "C:\Program Files\Autodesk\Revit $($launchVersion[0])\Revit.exe"
}

$parts = @()
if ($deployedVersions.Count -gt 0) { $parts += "installed for Revit $($deployedVersions -join ', ')" }
if ($unchangedVersions.Count -gt 0) { $parts += "add-in already current for Revit $($unchangedVersions -join ', ') (left untouched)" }
$summary = "Revit MCP Bridge $releaseTag"
if ($parts.Count -gt 0) { $summary += ": $($parts -join '; ')" }
$summary += "."
if ($restartVersions.Count -gt 0) { $summary += " Revit $($restartVersions -join ', ') is still running the previous add-in -- restart it to load the new one." }
if ($shimHeldVersions.Count -gt 0) { $summary += " The MCP Bridge shim for Revit $($shimHeldVersions -join ', ') changed in this release but is in use; it is refreshed the next time this installer runs with that Revit closed (the add-in itself is already updated)." }
switch ($brokerOutcome) {
    'swapped'   { $summary += " MCP Server updated." }
    'staged'    { $summary += " MCP Server updated on disk, but a running server is still serving the previous version: it takes effect when your MCP client next starts it -- reconnect the revit MCP server (e.g. /mcp in Claude Code) or restart the client. The Revit ribbon shows the update as available until then." }
    'pending'   {
        $summary += " The new MCP Server is waiting as mcp-server.exe.new; re-run this installer once no server is running to finish the swap."
        if (-not (Test-Path $serverExe)) { $summary += " Until then there is NO mcp-server.exe in place (the old one was moved aside before the swap was refused), so a new MCP client session cannot start the server." }
    }
    'unchanged' { $summary += " MCP Server already current." }
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
    # WHERE it failed, not only what (found chasing a -Silent Update Now that reported "Access is
    # denied" and nothing else): the failing statement and line, and a copy of the whole message in
    # the add-in's log directory -- the one place a person can look after a silent run whose hidden
    # console has already closed. Best-effort: a logging failure must not mask the real one.
    $where = try { $_.InvocationInfo.PositionMessage } catch { '' }
    Write-Host ''
    Write-Host "Revit MCP Bridge install did not complete: $msg$hint"
    if ($where) { Write-Host "  Failed at:$where" }
    try {
        $errorLog = Join-Path $env:LocalAppData 'Connectors\Revit\install-errors.log'
        New-Item -ItemType Directory -Force -Path (Split-Path $errorLog) | Out-Null
        Add-Content -Path $errorLog -Value ("{0} install did not complete (args: {1}): {2}`n  at:{3}`n  stack: {4}" -f (Get-Date -Format o), ($PSBoundParameters.Keys -join ','), $msg, $where, $_.ScriptStackTrace)
    } catch { }
    exit 1
} finally {
    if ($BootstrapCreated -and (Test-Path $ScriptPath)) { Remove-Item $ScriptPath -Force -ErrorAction SilentlyContinue }
}

# Keep this the LAST line: Test-IsFullInstallerScript requires it, so a download cut off anywhere
# above -- even between two complete statements, where the parser sees nothing wrong -- is rejected
# rather than installed as the self-copy (issue #192, and its review).
# MCPBRIDGE-INSTALL-PS1-END-OF-FILE
