# Deployment / install tooling

Installs the Revit MCP Bridge into Revit's Addins folder (per Revit version) and registers the Revit MCP Server with Claude's MCP client config. See PRD §12 (Signing & distribution) — [`docs/PRD.md`](docs/PRD.md).

- **`install.ps1`** — the whole installer: first install, in-place update, and uninstall are the same script (`-Uninstall` to remove). Designed to be run either as a downloaded file or piped directly (`irm .../install.ps1 | iex`) — see PRD §12 "Installation UX" for why this is a script rather than a packaged GUI installer. Idempotent: re-running with nothing to do is a single GitHub API call, not a full reinstall (see PRD §12 "Self-upgrade" for the three-outcome version check this implements).
- **`-LocalPackagePath <zip>`** — a testing/offline escape hatch. There is no release pipeline yet producing real signed GitHub Release artifacts (PRD §12's own "Known gap"), so this lets the deploy mechanics (per-version detection, idempotency, registry writes, MCP registration) be validated live against a real Revit install now, against a hand-built local zip matching the expected release payload layout (`addin-<year>/`, `server/`, at the zip root). Never used in a real install.
- Mac + Parallels users run this script inside their Windows VM for the add-in half; the broker itself runs natively on the Mac host. A separate, shorter macOS/bash counterpart script (not yet written) handles that half — see PRD §12 "Mac + Parallels".

**Not yet built**: the release pipeline itself (CI that builds, signs, and publishes `mcpbridge-release.zip` + `checksums.txt` to GitHub Releases in the layout this script expects) and the Mac-side broker installer script. This script cannot do a real end-to-end install until that pipeline exists.
