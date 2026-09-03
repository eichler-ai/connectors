# Deployment / install tooling

Installs the Revit MCP Bridge into Revit's Addins folder (per Revit version) and registers the Revit MCP Server with Claude's MCP client config. See PRD §12 (Signing & distribution) — [`docs/PRD.md`](docs/PRD.md).

## Install (Windows)

With Revit 2025 and/or 2027 installed, run this in PowerShell:

```powershell
irm https://raw.githubusercontent.com/eichler-ai/connectors/main/revit/install.ps1 | iex
```

It downloads the latest signed release, installs the add-in for whichever supported Revit versions are present, and registers the MCP server with Claude if the `claude` CLI is on `PATH`. Re-run any time to update; remove it from Windows **Apps & features** or with `install.ps1 -Uninstall`. Releases are **self-signed** for now, so Windows shows an "Unknown Publisher" prompt on first run (a CA certificate is deferred — PRD §12).

- **`install.ps1`** — the whole installer: first install, in-place update, and uninstall are the same script (`-Uninstall` to remove). Designed to be run either as a downloaded file or piped directly (`irm https://raw.githubusercontent.com/eichler-ai/connectors/main/revit/install.ps1 | iex`) — see PRD §12 "Installation UX" for why this is a script rather than a packaged GUI installer. Idempotent: re-running with nothing to do is a single GitHub API call, not a full reinstall (see PRD §12 "Self-upgrade" for the three-outcome version check this implements).
- **`-LocalPackagePath <zip>`** — a testing/offline escape hatch: the deploy mechanics (per-version detection, idempotency, registry writes, MCP registration) run against a hand-built local zip matching the release payload layout (`addin-<year>/`, `server/`, optionally `manifest.json`, at the zip root) instead of a download. Never used in a real install.
- **Per-component updates (`manifest.json`).** A release zip carries a manifest with one content hash per component (`addin-2025`, `addin-2027`, `server`) and the how-to corpus version the broker embeds; `installed-version.json` records what was installed. A run redeploys only the components whose hash changed: a corpus-only release changes `server` alone, so the add-in is left untouched and **a running Revit is not closed**. The broker is updated by stage-and-swap — the new exe is written beside the old one and moved into place, never over a locked file, and no broker process is ever stopped (each one belongs to an MCP client session). While a broker is still running the previous version the installer says so; the update takes effect when the client next starts the broker (reconnect the `revit` MCP server, or restart the client), which also rewrites `broker.json` and clears the ribbon's "Update available". A zip without a manifest (a hand-built local package) is treated as "everything changed".
- **One-time cost after upgrading to this installer:** a marker written by an older installer has no component hashes, so the first update redeploys every component once (closing Revit if it is running) before per-component skipping applies.
- **Tests:** `revit/install.tests.ps1` (Pester 5) covers the manifest, change detection and the broker swap and runs in CI under pwsh; `-LoadFunctionsOnly` dot-sources the script's functions without running an install, which is also how the release workflow builds the manifest.
- Mac + Parallels users run this script inside their Windows VM for the add-in half; the broker itself runs natively on the Mac host. **`install-mac.sh`** (alongside this script, at `revit/`) handles that half — see PRD §12 "Mac + Parallels" and the script's own header comment for why it's a shorter, source-build-based script rather than this one's release-download path (it builds the broker from source rather than downloading a release).

The release pipeline (`.github/workflows/release.yml`, `/release`) builds, signs and publishes `mcpbridge-release.zip` (with `manifest.json`) + `checksums.txt` to GitHub Releases in the layout this script expects, and prepends a "How-tos" section to the release notes naming every how-to added, revised, merged or removed since the previous tag.
