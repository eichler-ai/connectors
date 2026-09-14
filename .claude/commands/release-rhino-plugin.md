---
description: Cut and publish a Rhino MCP connector release — build the cross-platform yak package, live-verify it, tag, and publish a GitHub Release with the .yak attached
---

This command takes one required argument: `major`, `minor`, or `patch` — which part of the current
version to bump (e.g. `/release-rhino-plugin minor`). If it's missing or isn't one of those three
words, stop and ask for it. (An explicit `X.Y.Z` is also accepted if the user gives one.)

This is the Rhino connector's release, which is **not** the Revit `/release` flow: there is no
`release.yml` pipeline. A Rhino release builds the distributable **yak package** on demand
(`.github/workflows/rhino-package.yml`), live-verifies it against a real Rhino, then publishes a
GitHub Release with the `.yak` attached — the **install-from-file** posture (no `yak push` to the
public server yet; that is a deliberate future step, PRD §15). Tags are prefixed `rhino-v` so they
never collide with Revit's `v*` tags. Publishing a Release is publicly visible and hard to reverse —
do not skip the confirmation step, and never treat "the user ran the command" as consent to publish.

Follow these steps in order.

1. **Confirm a releasable state.** Current branch is `main`, the working tree is clean
   (`git status`), and `main` is up to date with `origin/main` (`git fetch`, then compare
   `git rev-parse HEAD` and `git rev-parse origin/main`). If any isn't true, stop and say exactly
   what to fix.

2. **Confirm CI is green on this commit.** The connector's tier-1 suites and cross-compiles run in
   `.github/workflows/ci.yml`. Check the latest `main` run is success:
   `gh run list --workflow=ci.yml --branch main --limit 1`, and that its `headSha` is the release
   commit. If it isn't green (or hasn't run for this commit), stop — do not release on a red or
   unverified build. **If the failure is a known or unrelated flake** (a timing-sensitive subprocess
   test, say — this repo has one in `revit/mcp-server`'s primary-continuity suite): that is not a
   licence to release over red. Re-run the failed job (`gh run rerun <id> --failed`) to confirm it's a
   flake, and **fix the flake in its own PR** before releasing — never tag on a red or still-flaky run.
   The v0.1.0 release hit exactly this and had to de-flake the test first.

3. **Determine the current version.** `git fetch --tags`, then
   `git tag -l 'rhino-v*' --sort=-v:refname | head -1`. If empty, treat the current version as
   `rhino-v0.0.0`.

4. **Compute the next version** by bumping the named component and zeroing the lower ones (semver):
   `rhino-v1.2.3` + `minor` → `rhino-v1.3.0`; `+ major` → `rhino-v2.0.0`; `+ patch` → `rhino-v1.2.4`.
   The SemVer string passed to the package build is the version **without** the `rhino-v` prefix
   (e.g. `1.3.0`).

5. **Build the package on demand.** Dispatch the packaging workflow with the version:
   `gh workflow run rhino-package.yml --ref main -f version=<X.Y.Z>`. Find the run
   (`gh run list --workflow=rhino-package.yml --limit 1`), watch it to completion
   (`gh run watch <id> --exit-status`), and require **both** jobs (`stage (macOS)`, `yak build
   (Windows)`) to succeed. Then download the artifact:
   `gh run download <id> -n rhino-yak-package -D <tmp>`. Sanity-check the `.yak` is the full package
   (**~178 MB** — the size the on-demand workflow actually produces; a ~48 MB package means the
   go:embed search models were not fetched, so investigate before releasing). The filename is
   `rhino-mcp-bridge-<X.Y.Z>-any-any.yak` — note **`any-any`**, not `rh8_*`: CI runs `yak build` on a
   runner with **no Rhino installed**, so `yak.exe` can't read the required Rhino version from the
   `.rhp` and defaults the distribution tag to "any Rhino, any OS." That is expected and harmless for
   install-from-file (it still installs and loads on Rhino 8), but it overstates compatibility — the
   release notes MUST say Rhino 8 is required. (Restoring the precise `rh8_*` tag is a known follow-up.)

6. **Live-verify the package against a real Rhino** — the release gate (the tier-1 suites don't
   exercise install/load/register). Use the Windows dev VM (see the `rhino-connector-development`
   skill's `dev-environment.md`, "Reaching the Windows VM from the Mac — SSH", and the phase-7 spike
   `rhino/docs/spikes/phase-7-windows-distribution.md` for the exact commands and the headless-VM
   gotchas). At minimum, on `ssh rhino-vm`:
   - `yak install` the downloaded `.yak`; confirm the `.rhp` **and** `mcp-server-win-x64.exe` land in
     the package folder.
   - Launch Rhino (open a document via a copied template `.3dm` file-arg — a headless launch opens
     none, #289), confirm the plug-in loads (`instances/<pid>.json`, `bridge_version` matches this
     build) with a clean `startup-errors.log`.
   - Run `MCPBridgeRegister`. Let Rhino finish warming up first — wait for the `python warm-up done`
     line in `connection.log` (the #287 `RhinoCode` force-load runs on the first `RhinoApp.Idle` tick,
     which the VM does fire, and `rhinocode` can't reach Rhino until then), then drive the command with
     `rhinocode command MCPBridgeRegister`. If `rhinocode` is uncooperative, the fallback that the
     phase-7 PRs actually used is to drive it over the MCP socket with a C# `execute_script` calling
     `RhinoApp.RunScript("_MCPBridgeRegister", true)`. Either way, confirm `claude mcp get rhino` shows
     **Connected** at the packaged binary path, then clean up (`claude mcp remove rhino --scope user`,
     `yak uninstall rhino-mcp-bridge`, remove scratch). *(Note: `MCPBridgeRegister`'s command-line
     invocation is only proven via the phase-7 PR verification — the first real release exercises it
     end to end, so watch this step.)*
   - If a Mac is available, install the `.yak` there too and confirm the plug-in loads and the
     universal `mcp-server-mac` runs (`lipo -info` shows `x86_64 arm64`). If you can't verify Mac,
     say so explicitly in the release notes rather than implying it was checked.
   If any step fails or can't be confirmed, stop and report — do not tag.

7. **Show the user a summary** before anything irreversible: current → new version, the `.yak`
   filename and size, the live-verification result (what was checked, on which platforms), and a
   change preview (`git log <last-rhino-tag>..HEAD --oneline`, or from the beginning if there is no
   previous tag). State plainly that confirming will push a real `rhino-vX.Y.Z` tag and publish a
   public GitHub Release with the `.yak` attached.

8. **Pause and wait for the user's explicit confirmation in this turn.** Do not narrate "about to
   publish" and publish anyway. If they decline, stop without tagging. If you cannot get an
   interactive answer (background/piped session), stop — silence is never consent for this.

9. **On confirmation, tag and publish:**
   - `git tag rhino-vX.Y.Z <release-commit> && git push origin rhino-vX.Y.Z`.
   - `gh release create rhino-vX.Y.Z <path-to-.yak> --title "Rhino MCP connector X.Y.Z" --notes-file <notes>`.
     Write curated notes (use `--notes-file`, not `--generate-notes` — with no prior `rhino-v` tag the
     latter dumps every commit in history): the **install** steps (download the `.yak`, `yak install
     <file>` or drag onto Rhino, restart Rhino, run `MCPBridgeRegister`), **"Rhino 8 required"** (it
     compensates for the `any-any` tag), a short what's-inside, the **honest verification result**
     (which platforms were live-checked and which weren't), and a **Known issues** section (the
     `any-any` tag; not yet on the public Package Manager; macOS notarization deferred). The 178 MB
     asset upload is slow — expect the `gh release create` call to run for a few minutes.

10. **Verify the published release** — do not call it done on the `gh release create` exit code alone.
    Confirm with `gh release view rhino-vX.Y.Z --json tagName,isDraft,assets,targetCommitish`: it is
    **not a draft**, the `.yak` asset is attached with state **`uploaded`** and the right size
    (~178 MB), and `git rev-parse rhino-vX.Y.Z` is the release commit. Confirm the asset is actually
    **downloadable** (a `curl` of the asset's `apiUrl` with `Accept: application/octet-stream` returns
    HTTP 200). Only then report the Release URL as done. `yak push` to the public package server is
    intentionally **not** part of this flow yet; if/when that lands it becomes a further step (`yak
    push` the same `.yak`, gated on the same confirmation).

11. **Reflect, and refine this command if warranted.** After a successful release, compare how the run
    actually went against these steps: what surprised you, what was missing, what was wrong, what you
    had to work out ad hoc (a filename/tag that didn't match, a flaky gate, a slow step, a manual
    workaround). If the prompt would be more accurate or more useful with a change, say so and — with
    the user's go-ahead — edit this file. Each release should leave this command a little sharper than
    it found it; the v0.1.0 run, for instance, corrected the expected `.yak` tag, added the flaky-CI
    guidance, and turned "report the URL" into this explicit verification and reflection.
