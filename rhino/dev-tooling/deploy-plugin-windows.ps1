<#
.SYNOPSIS
  Windows counterpart of deploy-plugin.sh: build the Rhino MCP Bridge, package it with yak, install it
  into Rhino 8 for Windows, restart Rhino, and deterministically open a document so the live harness's
  doc-dependent cases can run (#289).

.DESCRIPTION
  There is no VM topology for this connector (PRD §05); on Windows the dev loop is run by hand or by this
  script. Everything it does was learned during the phase-2 Windows pass (see the skill's
  rhino-connector-development/dev-environment.md "Verifying on Windows"):

  - KILL Rhino before building/reinstalling: a running Rhino holds the plug-in DLLs open, so the build
    stalls retrying the output copy and `yak uninstall` fails "Access denied".
  - Package ALL the product DLLs beside the .rhp (not just the .rhp) or the plug-in fails to load with
    "Could not load file or assembly 'Eichler.Connectors.Rhino'"; RhinoCommon.dll must never ship.
  - Rhino 8 runs x64-under-emulation on Windows-on-ARM, so cold start is slow (~2 min): poll generously.
  - #287: the plug-in force-loads the demand-loaded RhinoCodePlugin so Python 3 registers with no
    ScriptEditor; this waits for the "python warm-up done" line, which follows that force-load (a failed
    force-load instead surfaces as "python warm-up failed", which this reports).
  - #289: a programmatic launch intermittently opens NO document, and the connector faithfully reports
    zero — so doc-dependent cases fail with document-not-found. Running one `rhinocode` script after
    launch materialises an untitled document deterministically (rhinocode works once #287 loads
    RhinoCode, no ScriptEditor needed).

.PARAMETER NoRestart
  Build + install only; do not restart Rhino (it picks the new build up at its next start).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File rhino\dev-tooling\deploy-plugin-windows.ps1
#>
[CmdletBinding()]
param([switch]$NoRestart)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent (Split-Path -Parent $PSCommandPath)  # -> rhino/
$sln        = Join-Path $root 'mcp-bridge\Rhino.MCPBridge.sln'
$outDir     = Join-Path $root 'mcp-bridge\src\Rhino.MCPBridge.PlugIn\bin\Release'
$rhinoExe   = 'C:\Program Files\Rhino 8\System\Rhino.exe'
$yak        = 'C:\Program Files\Rhino 8\System\yak.exe'
$rhinocode  = 'C:\Program Files\Rhino 8\System\rhinocode.exe'
$connLog    = Join-Path $env:LOCALAPPDATA 'Connectors\Rhino\connection.log'

function Stop-Rhino {
    Get-Process Rhino -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
    Start-Sleep -Seconds 3
}

Write-Host '==> kill Rhino (it locks the plug-in DLLs)'
Stop-Rhino

Write-Host '==> build Release'
dotnet build $sln -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

Write-Host '==> package + yak install'
$pkg = Join-Path $env:TEMP ('rhino-mcp-bridge-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $pkg | Out-Null
Copy-Item -Path "$outDir\*.rhp", "$outDir\*.dll", "$outDir\*.deps.json" -Destination $pkg
Copy-Item -Path "$outDir\*.xml" -Destination $pkg -ErrorAction SilentlyContinue
if (Test-Path "$pkg\RhinoCommon.dll") { throw 'RhinoCommon.dll must not ship in the package (Rhino loads its own copy)' }
Set-Content -Path "$pkg\manifest.yml" -Encoding utf8 -Value @'
name: rhino-mcp-bridge
version: 0.0.1
authors:
  - Eichler
description: Rhino MCP Bridge (dev build) -- lets Claude and other agents drive this Rhino through the Rhino MCP Server
url: https://github.com/eichler-ai/connectors
'@
Push-Location $pkg
try {
    & $yak build | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "yak build failed (exit $LASTEXITCODE)" }
    $yakFile = Get-ChildItem *.yak | Select-Object -First 1
    if ($null -eq $yakFile) { throw 'yak build produced no .yak package' }
    & $yak uninstall rhino-mcp-bridge | Out-Null   # ignore "not installed"; native non-zero does not throw
    & $yak install $yakFile.FullName
    if ($LASTEXITCODE -ne 0) { throw "yak install failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}
# Best-effort cleanup of the staging dir. yak can briefly hold a handle on it right after install, so a
# Win32 "Access is denied" here must not fail the deploy (-ErrorAction doesn't catch that provider
# exception under ErrorActionPreference=Stop, so swallow it explicitly); a leftover temp dir is harmless.
try { Remove-Item -Recurse -Force $pkg -ErrorAction Stop } catch { Write-Host "    (left staging dir $pkg -- $($_.Exception.Message))" }

if ($NoRestart) {
    Write-Host '==> --NoRestart set: installed; Rhino will load it at its next start.'
    return
}

Write-Host '==> restart Rhino'
# connection.log is a rolling (size-capped) log, so a line-count offset is unsafe (a rotation resets it).
# Track this launch by wall-clock instead: match the newest "python warm-up (done|failed)" line and accept
# it only if its ISO timestamp is at/after launch. Under heavy load CPython deploy has taken ~150 s.
$launchUtc = (Get-Date).ToUniversalTime().AddSeconds(-5)
Start-Process -FilePath $rhinoExe -ArgumentList '/nosplash'

Write-Host '    waiting for python warm-up done (which follows the #287 RhinoCode force-load; a failed force-load surfaces as python warm-up failed). x64 emulation is slow; up to ~6 min.'
$deadline = (Get-Date).AddSeconds(420)
$pythonReady = $false
$pythonSeen  = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 6
    $line = Select-String -Path $connLog -Pattern 'python warm-up (done|failed)' -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($null -ne $line) {
        $ts = $null
        try { $ts = [System.DateTimeOffset]::Parse((($line.Line -split '\s+')[0])) } catch { }
        if ($null -ne $ts -and $ts.ToUniversalTime() -ge $launchUtc) {
            $pythonSeen = $true
            $pythonReady = $line.Line -match 'python warm-up done'
            break
        }
    }
}
Get-Content $connLog -Tail 3 -ErrorAction SilentlyContinue

Write-Host '==> ensure a document is open (#289: a programmatic launch may open none)'
# Executing any rhinocode script materialises an untitled document when none is open. rhinocode reaches
# Rhino because #287's force-load brought RhinoCode up -- no ScriptEditor needed. The probe writes back
# whether an active document exists, so this step CONFIRMS the fix rather than assuming it.
$probeOut = Join-Path $env:TEMP 'mcp-ensure-doc.out'
Remove-Item $probeOut -ErrorAction SilentlyContinue
$probe = Join-Path $env:TEMP 'mcp-ensure-doc.py'
Set-Content -Path $probe -Encoding utf8 -Value "import Rhino`nopen(r'$probeOut','w').write('doc=' + str(Rhino.RhinoDoc.ActiveDoc is not None))"
& $rhinocode script $probe | Out-Null
Start-Sleep -Seconds 2
$docState = (Get-Content $probeOut -ErrorAction SilentlyContinue) -join ''
if ($docState -eq 'doc=True') { Write-Host "    a document is open ($docState)." }
else { Write-Warning "could not confirm an open document (got '$docState'); doc-dependent harness cases may skip." }

if ($pythonSeen -and -not $pythonReady) {
    Write-Warning 'Python warm-up FAILED (see connection.log and #287); C# execution still works.'
} elseif (-not $pythonSeen) {
    Write-Warning 'Did not observe a python warm-up result within the deadline; check connection.log.'
}
Write-Host '==> done. Build a server (go build ./cmd/mcp-server) and run the harness with -broker-exe pointing at it.'
