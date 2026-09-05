#!/usr/bin/env bash
# Installs, updates, or removes the Mac-side half of this project's own Mac + Parallels dev
# topology (PRD §12 "Mac + Parallels", §05 remote mode): builds/places the mcp-server broker
# binary on the Mac host and registers it with Claude Code, wired for -mode remote against the
# Windows VM's shared folder.
#
# This is NOT a second product installer -- there is no macOS build of Revit, so there is nothing
# on this side for an end user to install. This script exists purely for developers of THIS
# project running Claude Code on the Mac side of the dev environment described in PRD §05/§12;
# revit/install.ps1 (run inside the VM) remains the one that installs the actual add-in, exactly
# as it does for the Windows-native target deployment.
#
# Deliberately short, per PRD §12's own framing ("not a redesign of anything above"): no
# self-upgrade machinery (that's an add-in-update concern, Windows-side only), no
# release-artifact download+checksum verification (PRD §12's own "Known gap" -- no release
# pipeline exists yet; install.ps1 has the same -LocalPackagePath escape hatch for the same
# reason). This script builds the broker directly from this checked-out source instead, which is
# both simpler and more correct for its actual audience -- a developer who has the repo cloned,
# not an end user installing a packaged release.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BROKER_SRC_DIR="$REPO_ROOT/revit/mcp-server"
BROKER_BIN="$BROKER_SRC_DIR/mcp-server-mac"
SHARED_ROOT="$REPO_ROOT/Connectors/Revit"

UNINSTALL=0
BIND_OVERRIDE=""
PORT=0 # ephemeral by default (PRD §05: broker.json is the discoverable source of truth for the
       # actual port either way) -- a fixed port was a manual, arbitrary dev-session choice
       # earlier, not a real requirement; nothing downstream needs it fixed.

usage() {
    cat <<EOF
Usage: $(basename "$0") [--uninstall] [--bind <ip>] [--port <n>]

  --uninstall     Remove the Claude Code MCP registration (does not delete the built binary).
  --bind <ip>     Override the auto-detected Parallels shared-network IP the VM's Revit add-in
                  will dial. Auto-detection looks for the "bridge100" interface Parallels' shared
                  networking creates by default -- pass this explicitly if your setup differs.
  --port <n>      Fixed port instead of an ephemeral one. Rarely needed (see comment in script).
EOF
}

require_value() {
    # set -u turns a missing "$2" into a raw "unbound variable" crash rather than a clean
    # message -- guard every flag that takes one.
    if [ $# -lt 2 ]; then
        echo "$1 requires a value" >&2
        usage >&2
        exit 1
    fi
}

while [ $# -gt 0 ]; do
    case "$1" in
        --uninstall) UNINSTALL=1; shift ;;
        --bind) require_value "$@"; BIND_OVERRIDE="$2"; shift 2 ;;
        --port) require_value "$@"; PORT="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 1 ;;
    esac
done

# Validated once, up front, rather than letting a typo surface later as an opaque MCP connection
# failure inside Claude Code -- main.go validates both too, but only at broker spawn time, by
# which point this script has already reported success.
if [ -n "$BIND_OVERRIDE" ]; then
    if ! [[ "$BIND_OVERRIDE" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        echo "--bind $BIND_OVERRIDE is not a valid IPv4 literal" >&2
        exit 1
    fi
fi
if ! [[ "$PORT" =~ ^[0-9]+$ ]] || [ "$PORT" -gt 65535 ]; then
    echo "--port $PORT is not a valid port number (0-65535)" >&2
    exit 1
fi

if ! command -v claude >/dev/null 2>&1; then
    echo "Claude Code CLI not found on PATH -- install it first: https://claude.com/claude-code" >&2
    exit 1
fi

if [ "$UNINSTALL" = "1" ]; then
    claude mcp remove revit --scope local >/dev/null 2>&1 || true
    echo "Removed the 'revit' MCP registration (scope: local). Built binary and Connectors/Revit left in place -- delete them by hand if you want those gone too."
    exit 0
fi

if ! command -v go >/dev/null 2>&1; then
    echo "Go toolchain not found on PATH -- required to build the broker from source (no release pipeline exists yet, PRD §12)." >&2
    exit 1
fi

# --- Auto-detect the Mac's IP on the Parallels shared network --------------------------------
# Parallels' default shared-networking setup creates a "bridge100" interface on the Mac side
# (confirmed live in this project's own dev environment) carrying a private (commonly
# 10.211.55.x) address the VM can reach directly, no NAT/port-forwarding involved. This is a
# best-effort default, not a guarantee -- Parallels network configuration can differ (a
# different adapter name, a custom network setup), which is exactly why --bind exists as an
# escape hatch. Always print what was used so a wrong guess is visible, never silent.
if [ -n "$BIND_OVERRIDE" ]; then
    BIND_IP="$BIND_OVERRIDE"
    echo "Using --bind override: $BIND_IP"
else
    # `|| true`: under set -e/-o pipefail, ifconfig exiting non-zero (interface doesn't exist)
    # would otherwise kill the script right here, before the friendly message below -- the one
    # case that message exists for -- ever gets a chance to print.
    BIND_IP="$(ifconfig bridge100 2>/dev/null | awk '/inet /{print $2; exit}' || true)"
    if [ -z "$BIND_IP" ]; then
        echo "Could not auto-detect a Parallels shared-network IP (looked for interface 'bridge100')." >&2
        echo "Find yours with: ifconfig | grep -B4 'inet 10.211' -- then re-run with --bind <ip>." >&2
        exit 1
    fi
    echo "Auto-detected Parallels shared-network IP: $BIND_IP (interface bridge100 -- override with --bind if this is wrong for your setup)"
fi

# --- Build the broker ---------------------------------------------------------------------------
# Stamped with the revision of THIS checkout (issue #116). The Go toolchain stamps one
# automatically, which is why a plain `go build` still carries provenance -- but it finds the
# repository by walking up for a `.git` DIRECTORY, so a build made inside a git worktree gets the
# ENCLOSING checkout's revision, or none at all. Declaring it here means the binary reports the tree
# it was actually built from, which is what makes `get_skills`' own "compare this against
# `git rev-parse HEAD`" advice safe to give. Same shape in dev-tooling/deploy-and-verify.sh.
#
# "Modified" here counts uncommitted changes to TRACKED files only -- `git status --porcelain` would
# also flip on any stray untracked scratch file, and a warning that is permanently on stops being
# read.
BUILDINFO_PKG="github.com/eichler-ai/connectors/revit/mcp-server/internal/buildinfo"
BROKER_LDFLAGS=()
if BROKER_REV="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null)"; then
    BROKER_REV_TIME="$(git -C "$REPO_ROOT" show -s --format=%cI HEAD 2>/dev/null || true)"
    if git -C "$REPO_ROOT" diff --quiet HEAD 2>/dev/null; then BROKER_DIRTY=false; else BROKER_DIRTY=true; fi
    BROKER_LDFLAGS=(-ldflags "-X $BUILDINFO_PKG.stampedRevision=$BROKER_REV -X $BUILDINFO_PKG.stampedRevisionTime=$BROKER_REV_TIME -X $BUILDINFO_PKG.stampedModified=$BROKER_DIRTY")
else
    echo "Note: $REPO_ROOT is not a git checkout -- the broker will report its revision as unknown." >&2
fi

echo "Building mcp-server for macOS..."
( cd "$BROKER_SRC_DIR" && go build "${BROKER_LDFLAGS[@]}" -o "$BROKER_BIN" ./cmd/mcp-server )
echo "Built: $BROKER_BIN -- $("$BROKER_BIN" -version)"

mkdir -p "$SHARED_ROOT"

# --- Register with Claude Code ------------------------------------------------------------------
# --scope local (the default, but explicit here since this must never accidentally end up in a
# committed .mcp.json): per-user, per-project, stored in ~/.claude.json -- machine-specific
# values (this IP, these absolute paths) have no business in a file other developers pull. See
# this project's own PR history for exactly this mistake caught and reverted.
claude mcp remove revit --scope local >/dev/null 2>&1 || true
claude mcp add revit --scope local -- "$BROKER_BIN" -mode remote -bind "$BIND_IP" -port "$PORT" -shared-root "$SHARED_ROOT"

# `claude mcp add` on an already-registered name is a silent, exit-0 no-op rather than an
# overwrite -- so if the `remove` above failed for a real reason (swallowed by `|| true`, since
# "wasn't previously registered" also exits non-zero and must stay non-fatal), this would
# otherwise report success while a stale registration (old IP, old binary path) is what's
# actually left in ~/.claude.json. Confirm what's actually registered rather than trust the exit
# code of a command that can't distinguish those two cases.
if ! claude mcp get revit 2>/dev/null | grep -qF -- "$BIND_IP"; then
    echo "Registration did not take effect as expected -- 'claude mcp get revit' doesn't show $BIND_IP." >&2
    echo "A previous registration may be stuck; try: claude mcp remove revit --scope local, then re-run this script." >&2
    exit 1
fi

echo "Registered 'revit' with Claude Code (scope: local)."
echo "Remote-mode config: -bind $BIND_IP -port $PORT -shared-root $SHARED_ROOT"
echo "  (the broker's own models cache + how-to corpus stay on this Mac's app-data, not the shared folder)"
echo "Next: inside the VM, run revit/install.ps1 (if not already), launch Revit with"
echo "  MCPBRIDGE_BROKER_MODE=remote and MCPBRIDGE_SHARED_ROOT=<UNC path to this repo's shared folder>"
echo "set for the process, then run 'claude' here and it will spawn this broker automatically."
