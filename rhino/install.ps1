<#
.SYNOPSIS
  One-step install (and uninstall) of the Rhino MCP connector on Windows.

.DESCRIPTION
  Downloads the latest rhino-v* GitHub release's .yak, installs it with Rhino 8's own yak CLI, and
  registers the MCP server with BOTH Claude Code and Claude Desktop.

  Deliberately much smaller than revit/install.ps1: that script predates the tooling and hand-rolls
  add-in-per-version copying, a broker stage-and-swap, version markers and an Apps & Features entry.
  Here yak owns the plug-in install (versioned, atomic, loaded at next Rhino start) and the server's own
  `register` / `unregister` subcommand owns the dual-client registration (and the Claude Desktop MSIX
  config path) -- so this script only orchestrates them. The plug-in is uninstalled by this script's
  -Uninstall, by `_PackageManager` inside Rhino, or by `yak uninstall`; there is no separate installed
  program to register.

.PARAMETER Version
  Install a specific version (e.g. 0.1.0) instead of the latest release.

.PARAMETER Uninstall
  Unregister from both Claude clients and uninstall the plug-in.

.EXAMPLE
  irm https://raw.githubusercontent.com/eichler-ai/connectors/main/rhino/install.ps1 | iex

.EXAMPLE
  # a specific version, or uninstall -- download then run the file (args can't pass through irm|iex):
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -Version 0.1.0
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest's progress bar throttles PS 5.1 badly
# GitHub's API needs TLS 1.2, which Windows PowerShell 5.1 does not negotiate by default.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$RepoSlug      = 'eichler-ai/connectors'
$PkgName       = 'rhino-mcp-bridge'
$ServerExeName = 'mcp-server-win-x64.exe'

function Find-Yak {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Rhino 8\System\yak.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Rhino 8\System\yak.exe')
    )
    $y = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $y) { throw 'Could not find Rhino 8''s yak.exe (looked under Program Files). Is Rhino 8 installed?' }
    return $y
}

function Invoke-ServerSubcommand([string]$exe, [string]$sub) {
    # Run `<server> <sub>` with a timeout: a server old enough to lack the register/unregister subcommands
    # would otherwise fall through to its stdio server loop and hang the installer. Output (one line per
    # Claude client) goes straight to the console.
    $p = Start-Process -FilePath $exe -ArgumentList $sub -NoNewWindow -PassThru
    if (-not $p.WaitForExit(60000)) {
        try { $p.Kill() } catch { }
        Write-Warning "'$sub' did not finish in 60s. The installed server may predate this installer -- update to a newer release, or run MCPBridgeRegister inside Rhino."
    }
}

function Get-PackageServerExe([string]$version) {
    # yak installs to the per-user packages folder as rhino-mcp-bridge/<version>/.
    $root = Join-Path $env:APPDATA "McNeel\Rhinoceros\packages\8.0\$PkgName"
    if (-not (Test-Path $root)) { return $null }
    $dir = if ($version -and (Test-Path (Join-Path $root $version))) {
        Join-Path $root $version
    } else {
        Get-ChildItem $root -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $dir) { return $null }
    Join-Path $dir $ServerExeName
}

$yak = Find-Yak

# --- Uninstall -------------------------------------------------------------------------------------
if ($Uninstall) {
    $serverExe = Get-PackageServerExe
    if ($serverExe -and (Test-Path $serverExe)) {
        Write-Host 'Unregistering from your Claude clients...'
        Invoke-ServerSubcommand $serverExe 'unregister'   # removes rhino from Claude Code AND Claude Desktop
    } else {
        Write-Host 'Server binary not found; skipping Claude deregistration (uninstalling the plug-in anyway).'
    }
    Write-Host 'Uninstalling the plug-in...'
    & $yak uninstall $PkgName
    Write-Host 'Done. Restart Rhino to unload the plug-in.'
    return
}

# --- Install ---------------------------------------------------------------------------------------
if (Get-Process Rhino -ErrorAction SilentlyContinue) {
    Write-Warning 'Rhino is running. It must be restarted to load the plug-in, and a re-install of the same version can fail while its files are locked. Close Rhino if the install below errors.'
}

Write-Host 'Finding the latest Rhino release...'
# NB: Invoke-RestMethod emits a JSON array as a SINGLE pipeline object, so do NOT wrap the assignment in
# @() — that yields a one-element array holding the whole array, and the filter below then sees nothing.
# Assign directly; piping into Where-Object enumerates the releases correctly.
$allReleases = Invoke-RestMethod -Uri "https://api.github.com/repos/$RepoSlug/releases" -Headers @{ 'User-Agent' = 'rhino-mcp-install' }
$rhinoReleases = @($allReleases | Where-Object { $_.tag_name -like 'rhino-v*' -and -not $_.draft })
if ($Version) {
    $release = $rhinoReleases | Where-Object { $_.tag_name -eq "rhino-v$Version" } | Select-Object -First 1
    if (-not $release) { throw "No release tagged rhino-v$Version was found." }
} else {
    $release = $rhinoReleases | Select-Object -First 1   # the API returns newest first
    if (-not $release) { throw 'No rhino-v* release was found on GitHub.' }
}
$asset = $release.assets | Where-Object { $_.name -like "$PkgName-*.yak" } | Select-Object -First 1
if (-not $asset) { throw "Release $($release.tag_name) has no $PkgName .yak asset." }
$installVersion = $release.tag_name -replace '^rhino-v', ''

$yakFile = Join-Path $env:TEMP $asset.name
Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)..."
(New-Object System.Net.WebClient).DownloadFile($asset.browser_download_url, $yakFile)

Write-Host 'Installing the plug-in with yak...'
try { & $yak uninstall $PkgName 2>&1 | Out-Null } catch { }   # replace any prior version; ignore "not installed"
& $yak install $yakFile
if ($LASTEXITCODE -ne 0) { throw 'yak install failed (close Rhino if it is open, then re-run).' }
Remove-Item $yakFile -ErrorAction SilentlyContinue

$serverExe = Get-PackageServerExe $installVersion
if (-not $serverExe -or -not (Test-Path $serverExe)) { throw "The plug-in installed but its server binary is missing (expected $ServerExeName in the package folder)." }

Write-Host 'Registering the MCP server with your Claude clients (Claude Code + Claude Desktop)...'
# Run with a timeout: a server old enough to lack the `register` subcommand would otherwise fall through
# to its stdio server loop and hang. Prints one line per client; a client that is not installed is skipped.
Invoke-ServerSubcommand $serverExe 'register'

Write-Host ''
Write-Host "Installed $($release.tag_name)."
Write-Host 'Next:'
Write-Host '  1. Restart Rhino so it loads the MCP Bridge plug-in.'
Write-Host '  2. Restart your Claude client so it picks up the server:'
Write-Host '       - Claude Code: run  /mcp'
Write-Host '       - Claude Desktop: quit fully (tray icon -> Quit) and reopen.'
Write-Host 'Run MCPBridgeStatus in Rhino any time to check the connection and registration.'
