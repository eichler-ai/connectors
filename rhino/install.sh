#!/bin/bash
# One-step install (and uninstall) of the Rhino MCP connector on macOS -- the counterpart of
# rhino/install.ps1. Downloads a rhino-v* GitHub release's .yak (latest by default), installs it with
# Rhino 8's own yak CLI, and registers the MCP server with BOTH Claude Code and Claude Desktop.
#
# Like the Windows script, it just orchestrates: yak owns the plug-in install, and the server's own
# `register` / `unregister` subcommand owns the dual-client registration (including the macOS Claude
# Desktop config path, ~/Library/Application Support/Claude/).
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/eichler-ai/connectors/main/rhino/install.sh | bash
#   # a specific version, or uninstall -- download then run the file (args can't pass through curl|bash):
#   curl -fsSL https://raw.githubusercontent.com/eichler-ai/connectors/main/rhino/install.sh -o install.sh
#   bash install.sh --version 0.1.0
#   bash install.sh --uninstall
set -eu

REPO='eichler-ai/connectors'
PKG='rhino-mcp-bridge'
SERVER='mcp-server-mac'
YAK='/Applications/Rhino 8.app/Contents/Resources/bin/yak'

UNINSTALL=0
VERSION=''
while [ $# -gt 0 ]; do
    case "$1" in
        --uninstall) UNINSTALL=1 ;;
        --version) shift; VERSION="${1:-}"; [ -n "$VERSION" ] || { echo '--version needs a value, e.g. --version 0.1.0' >&2; exit 1; } ;;
        --version=*) VERSION="${1#--version=}" ;;
        *) echo "Unknown argument: $1 (expected --uninstall or --version X.Y.Z)" >&2; exit 1 ;;
    esac
    shift
done
VERSION="${VERSION#rhino-v}"   # accept either 0.1.0 or rhino-v0.1.0

[ -x "$YAK" ] || { echo "Could not find Rhino 8's yak at '$YAK'. Is Rhino 8 installed?" >&2; exit 1; }

# The newest rhino-mcp-bridge/<version>/ under the per-user package folder, or empty.
package_dir() {
    local root="$HOME/Library/Application Support/McNeel/Rhinoceros/packages/8.0/$PKG"
    [ -d "$root" ] || return 0
    ls -dt "$root"/*/ 2>/dev/null | head -1
}

# Run a leaf command with a timeout (macOS has no `timeout`): a server old enough to lack the register/
# unregister subcommands would otherwise fall through to its stdio loop and hang the installer. Output
# still reaches the terminal. NB: only the direct child is signalled -- route only single, non-forking
# binaries (like `mcp-server-mac register`) through this, not subshells or process trees.
run_timeout() {
    local secs="$1"; shift
    "$@" & local pid=$!
    ( sleep "$secs"; kill "$pid" 2>/dev/null ) & local watcher=$!
    local rc=0; wait "$pid" 2>/dev/null || rc=$?
    kill "$watcher" 2>/dev/null || true
    return "$rc"
}

if [ "$UNINSTALL" = 1 ]; then
    dir="$(package_dir)"
    if [ -n "$dir" ] && [ -f "$dir$SERVER" ]; then
        echo 'Unregistering from your Claude clients...'
        run_timeout 60 "$dir$SERVER" unregister || true
    else
        echo 'Server binary not found; skipping Claude deregistration (uninstalling the plug-in anyway).'
    fi
    echo 'Uninstalling the plug-in...'
    if ! "$YAK" uninstall "$PKG"; then
        echo 'yak uninstall failed (close Rhino if it is open, then re-run with --uninstall).' >&2
        exit 1
    fi
    echo 'Done. Restart Rhino to unload the plug-in.'
    exit 0
fi

if pgrep -x Rhinoceros >/dev/null 2>&1; then
    echo 'Note: Rhino is running. It must be restarted to load the plug-in, and re-installing the same'
    echo '      version can fail while its files are locked. Close Rhino if the install below errors.'
fi

if [ -n "$VERSION" ]; then echo "Finding Rhino release rhino-v$VERSION..."; else echo 'Finding the latest Rhino release...'; fi
# The unauthenticated GitHub API returns releases newest-first and (unlike drafts) DOES include
# prereleases, so we must filter them out to match install.ps1 -- otherwise macOS could install an RC
# the Windows one-liner skips. `?per_page=100` so a burst of Revit v* releases can't push the newest
# rhino-v* off the first page.
if ! json="$(curl -fsSL "https://api.github.com/repos/$REPO/releases?per_page=100" -H 'User-Agent: rhino-mcp-install')"; then
    echo 'Could not fetch releases from GitHub. The unauthenticated GitHub API allows 60 requests/hour;' >&2
    echo 'if you hit that, wait a few minutes and retry.' >&2
    exit 1
fi

# No jq/python3 is guaranteed on macOS, so parse the pretty-printed JSON with a small state machine.
# GitHub emits each release's fields in a fixed order -- tag_name, then draft/prerelease, then the
# assets (each with browser_download_url) -- so per release we can note the tag and whether to skip it
# before its .yak asset line appears. Emits "tag<TAB>url", newest first, for each rhino-v release that
# is neither draft nor prerelease and has a rhino-mcp-bridge .yak asset.
matches="$(printf '%s' "$json" | awk -v pkg="$PKG" '
    /"tag_name":/ {
        rhino = ($0 ~ /"rhino-v/); skip = 0
        if (match($0, /rhino-v[^"]*/)) tag = substr($0, RSTART, RLENGTH); else tag = ""
    }
    /"draft": *true/      { skip = 1 }
    /"prerelease": *true/ { skip = 1 }
    rhino && !skip && /"browser_download_url":/ {
        if (match($0, "https://[^\"]*" pkg "-[^\"]*\\.yak")) {
            print tag "\t" substr($0, RSTART, RLENGTH)
            rhino = 0   # one asset line per release
        }
    }
')"

if [ -n "$VERSION" ]; then
    line="$(printf '%s\n' "$matches" | grep "^rhino-v$VERSION	" | head -1 || true)"
    [ -n "$line" ] || { echo "No non-prerelease release tagged rhino-v$VERSION with a .yak asset was found." >&2; exit 1; }
else
    line="$(printf '%s\n' "$matches" | head -1)"
    [ -n "$line" ] || { echo 'No rhino-v* release was found on GitHub.' >&2; exit 1; }
fi
tag="${line%%	*}"
url="${line#*	}"

tmpdir="$(mktemp -d)"
trap 'rm -rf "$tmpdir"' EXIT
yakfile="$tmpdir/$(basename "$url")"
echo "Downloading $(basename "$url")..."
curl -fsSL -o "$yakfile" "$url"

echo 'Installing the plug-in with yak...'
"$YAK" uninstall "$PKG" >/dev/null 2>&1 || true   # replace any prior version; ignore "not installed"
if ! "$YAK" install "$yakfile"; then
    echo 'yak install failed (close Rhino if it is open, then re-run).' >&2
    exit 1
fi

dir="$(package_dir)"
server="$dir$SERVER"
[ -f "$server" ] || { echo "The plug-in installed but its server binary is missing (expected $SERVER in the package folder)." >&2; exit 1; }
chmod +x "$server" 2>/dev/null || true

echo 'Registering the MCP server with your Claude clients (Claude Code + Claude Desktop)...'
run_timeout 60 "$server" register || echo "  (registration did not finish; run MCPBridgeRegister inside Rhino, or update to a newer release)"

echo ''
echo "Installed $tag. Next:"
echo '  1. Restart Rhino so it loads the MCP Bridge plug-in.'
echo '  2. Restart your Claude client so it picks up the server:'
echo '       - Claude Code: run  /mcp'
echo '       - Claude Desktop: quit fully and reopen.'
echo 'Run MCPBridgeStatus in Rhino any time to check the connection and registration.'
