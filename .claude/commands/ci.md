---
description: Run the same checks CI runs, locally, in the same order every time
---

Run `revit/dev-tooling/ci-local.sh` from the repo root via the Bash tool and report its result.

This script is the single source of truth for "what CI checks" — it mirrors
`.github/workflows/ci.yml` step-for-step (gofmt, go vet, go test -race, and a Windows
cross-compile for `revit/mcp-server`; `go vet -tags harness` for `revit/test-harness`). Always
run the script itself rather than reconstructing these steps by hand, so a check here means the
same thing every time regardless of which session runs it — if the two ever need to diverge,
that's a bug in one of them, not an excuse to hand-roll a different set of steps for this command.

If it fails, report exactly which step(s) failed (the script prints `FAILED: <step>` for each) and
fix them before proceeding — do not report success unless the script's own final line says
`CI-LOCAL: PASS`.

This does not cover the C# add-in (`MCPBridge.Core`/`RevitAdapter`/`AddIn`) or the live tier-2
harness suite — both need a machine with Revit installed and are run via the VM-side dev loop
documented in the `revit-connector-development` skill, not by this command. A green result here is
necessary but not sufficient before a PR that touches the Revit seam; see CONTRIBUTING.md's PR
expectations for when live verification is also required.
