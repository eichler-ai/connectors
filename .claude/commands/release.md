---
description: Cut and publish a new release (bump version, tag, push, trigger the release pipeline)
---

This command takes one required argument: `major`, `minor`, or `patch` -- which part of the current
version to bump (e.g. `/release minor`). If it's missing or isn't one of those three words, stop and
ask for it.

Follow these steps in order. This is a real, publicly-visible, hard-to-reverse action (it publishes
a real GitHub Release and triggers `.github/workflows/release.yml` on the self-hosted runner) --
don't skip the confirmation step, and don't treat "the user asked for /release" as consent to push
the tag itself.

1. **Confirm the repo is in a releasable state.** Check that the current branch is `main`, the
   working tree is clean (`git status`), and `main` is up to date with `origin/main` (`git fetch`,
   then compare `git rev-parse HEAD` against `git rev-parse origin/main`). If any of these isn't
   true, stop and tell the user exactly what to fix (switch branches, commit/stash, pull) -- do not
   proceed.

2. **Run `revit/dev-tooling/ci-local.sh`** (the same script `/ci` runs) and require it to end with
   `CI-LOCAL: PASS`. If it fails, report which step(s) failed and stop -- do not tag a release on a
   failing build.

3. **Also verify the C# side**, which `ci-local.sh` deliberately does not cover (see its own header
   comment and `ci.yml`'s: the C# add-in needs real `RevitAPI.dll`/`RevitAPIUI.dll` references from
   an actual Revit install, which this repo's Linux CI doesn't have). A release ships the add-in, so
   shipping one that hasn't been test-verified is a real gap -- build
   `revit/mcp-bridge/MCPBridge.sln` and run `MCPBridge.Core.Tests` on **both** TFMs via the dev VM
   (see the `revit-connector-development` skill, `dev-environment.md`, for the exact `prlctl exec`
   command shapes and toolchain quirks on that machine). Confirm 0 failures on both legs by reading
   the actual test **count** in the output, not just the process exit code -- this project has been
   bitten before by `dotnet test` exiting 0 while silently skipping an entire test assembly (missing
   DLL for a TFM prints no summary line at all). If either leg fails or can't be confirmed, stop and
   report it.
   If the release carries a how-to corpus change (`git diff <last-tag> -- revit/mcp-server/internal/howto/corpus/`
   is non-empty), the `revit-connector-development` skill also requires the live `TestHowToSweep` and
   `TestHowToEndToEnd` runs on both Revit versions before the tag; confirm they were run, or stop.

4. **Determine the current latest version.** `git fetch --tags`, then
   `git tag -l 'v*' --sort=-v:refname | head -1`. If that's empty, treat the current version as
   `v0.0.0`.

5. **Compute the next version** by bumping the component named in the argument, resetting any lower
   components to 0 (standard semver): e.g. current `v1.2.3`, `/release minor` -> `v1.3.0`; current
   `v1.2.3`, `/release major` -> `v2.0.0`; current `v1.2.3`, `/release patch` -> `v1.2.4`.

6. **Show the user a summary** before doing anything irreversible: current version -> new version,
   plus a change preview (`git log <last-tag>..HEAD --oneline`, or `git log --oneline` from the
   beginning if there was no previous tag) -- this preview is informational only, not something you
   author into release notes; the workflow generates those itself. State plainly that confirming
   will push a real tag, which triggers `.github/workflows/release.yml` and publishes a real, live
   GitHub Release with `mcpbridge-release.zip` and `checksums.txt` attached, plus notes GitHub
   auto-generates from merged-PR titles since the previous release tag.

7. **Pause and ask the user to confirm** before pushing anything. Actually wait for their answer in
   this turn -- do not narrate "I'm about to push" and push anyway. If they decline or want changes,
   stop here without tagging. If you cannot get an interactive answer at all (a background or piped
   session, no way to prompt) -- stop. Never treat silence, a timeout, or a pre-supplied instruction
   elsewhere in the conversation as confirmation for this specific release.

8. **On confirmation**, run `git tag vX.Y.Z && git push origin vX.Y.Z`. The tag push itself is what
   triggers the release workflow -- nothing else needs to run.

9. **Report back the Actions run URL** so the user can watch it live, e.g. via
   `gh run list --workflow=release.yml --limit 1` (or `gh run watch <id>` if you want to follow it
   yourself). Don't declare the release "done" until the workflow run itself has finished
   successfully -- a pushed tag only starts the pipeline, it doesn't guarantee the release publishes.
