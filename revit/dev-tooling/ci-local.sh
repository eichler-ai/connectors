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

# CI's "assert a plain build carries its source revision" step, WARN-only here -- the one
# deliberate divergence from ci.yml in this script, and the reason is a legitimate local setup: the
# Go toolchain finds a repository by looking for a `.git` DIRECTORY, so a build from a git worktree
# whose `.git` is a file stamps the enclosing checkout's revision (when nested inside one) or
# nothing at all (when it isn't). This project's own dev process uses external worktrees for Go
# work, so failing here would fail every one of them for an environment property, not a defect.
# What CI asserts is what matters: the artifacts people install know their own revision (#116).
echo "==> build provenance (warn-only locally -- CI asserts it)"
provenance_bin="$(mktemp -t mcp-server-provenance)"
if go build -o "$provenance_bin" ./cmd/mcp-server; then
    provenance_line="$("$provenance_bin" -version)"
    echo "    $provenance_line"
    case "$provenance_line" in
        *unknown*) echo "    NOTE: this build carries no source revision. Expected from a git worktree; a defect from the main checkout (see internal/buildinfo)." ;;
    esac
fi
rm -f "$provenance_bin"

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
