@echo off
REM Path below is whatever the current Parallels shared-folder alias resolves to on this
REM machine right now -- it has changed at least once across a restart (\\psf -> \\Mac,
REM see the revit-connector-development skill). Re-verify with `dir \\Mac\connectors` /
REM `dir \\psf\connectors` before trusting this hardcoded value; don't assume it's still right.
set MCPBRIDGE_BROKER_MODE=remote
set MCPBRIDGE_SHARED_ROOT=\\Mac\connectors
start "" "C:\Program Files\Autodesk\Revit 2027\Revit.exe"
