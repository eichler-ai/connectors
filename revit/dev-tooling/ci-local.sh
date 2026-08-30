#!/usr/bin/env bash
# Runs exactly what .github/workflows/ci.yml runs, in the same order, locally -- so a check here
# means the same thing every time, for a human or an agent, instead of each session hand-typing
# (and potentially drifting from) the actual CI recipe. Keep this in lockstep with ci.yml: if a
# step changes there, change it here too, in the same commit.
#
# Does NOT run anything Revit/VM-dependent -- matches CI's own scope exactly (see ci.yml's own
# top-of-file comment and CONTRIBUTING.md's testing-tiers section): tier-2 harness tests need a
# live Revit + broker stack no CI runner has, so CI only type-checks them, and so does this script.
# This is not a substitute for live verification before a PR that touches the Revit seam --
# CONTRIBUTING.md's own PR expectations still apply on top of this passing.
#
# One unmodelled difference from real CI, worth knowing rather than fixing: ci.yml pins the Go
# toolchain via actions/setup-go's go-version-file (each module's own go.mod); this script uses
# whatever `go` is already on PATH. A local Go version drifted far enough from what CI actually
# runs is the one way this script's "PASS" and CI's own result could still disagree.

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
FAILED=0

run_step() {
    local desc="$1"
    shift
    echo "==> $desc"
    if ! "$@"; then
        echo "    FAILED: $desc"
        FAILED=1
    fi
}

echo "### MCP Server (Go) -- revit/mcp-server"
cd "$REPO_ROOT/revit/mcp-server" || exit 1

echo "==> gofmt"
unformatted="$(gofmt -l .)"
if [[ -n "$unformatted" ]]; then
    echo "    FAILED: gofmt needed on:"
    echo "$unformatted"
    FAILED=1
fi

run_step "go vet ./..." go vet ./...
run_step "go test -race ./..." go test -race ./...
run_step "cross-compile Windows build" env GOOS=windows GOARCH=amd64 go build ./...

echo
echo "### Test harness type-check (Go) -- revit/test-harness"
cd "$REPO_ROOT/revit/test-harness" || exit 1
run_step "go vet -tags harness ./... (type-check only, per CONTRIBUTING.md -- tier-2 tests need a live Revit stack)" go vet -tags harness ./...

echo
if [[ "$FAILED" -eq 0 ]]; then
    echo "CI-LOCAL: PASS"
    exit 0
else
    echo "CI-LOCAL: FAIL -- see FAILED lines above"
    exit 1
fi
