#!/bin/bash
# One-step install (and uninstall) of the Rhino MCP connector on macOS -- the counterpart of
# rhino/install.ps1. Downloads the latest rhino-v* GitHub release's .yak, installs it with Rhino 8's own
# yak CLI, and registers the MCP server with BOTH Claude Code and Claude Desktop.
#
# Like the Windows script, it just orchestrates: yak owns the plug-in install, and the server's own
# `register` / `unregister` subcommand owns the dual-client registration (including the macOS Claude
# Desktop config path, ~/Library/Application Support/Claude/).
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/eichler-ai/connectors/main/rhino/install.sh | bash
#   # or, to uninstall (download then run, so the flag reaches the script):
#   curl -fsSL https://raw.githubusercontent.com/eichler-ai/connectors/main/rhino/install.sh -o install.sh && bash install.sh --uninstall
set -eu

REPO='eichler-ai/connectors'
PKG='rhino-mcp-bridge'
SERVER='mcp-server-mac'
YAK='/Applications/Rhino 8.app/Contents/Resources/bin/yak'

[ -x "$YAK" ] || { echo "Could not find Rhino 8's yak at '$YAK'. Is Rhino 8 installed?" >&2; exit 1; }

# The newest rhino-mcp-bridge/<version>/ under the per-user package folder, or empty.
package_dir() {
    local root="$HOME/Library/Application Support/McNeel/Rhinoceros/packages/8.0/$PKG"
    [ -d "$root" ] || return 0
    ls -dt "$root"/*/ 2>/dev/null | head -1
}

# Run a command with a timeout (macOS has no `timeout`): a server old enough to lack the register/
# unregister subcommands would otherwise fall through to its stdio loop and hang the installer. Output
# still reaches the terminal.
run_timeout() {
    local secs="$1"; shift
    "$@" & local pid=$!
    ( sleep "$secs"; kill "$pid" 2>/dev/null ) & local watcher=$!
    local rc=0; wait "$pid" 2>/dev/null || rc=$?
    kill "$watcher" 2>/dev/null || true
    return "$rc"
}

if [ "${1:-}" = '--uninstall' ]; then
    dir="$(package_dir)"
    if [ -n "$dir" ] && [ -x "$dir$SERVER" ]; then
        echo 'Unregistering from your Claude clients...'
        run_timeout 60 "$dir$SERVER" unregister || true
    else
        echo 'Server binary not found; skipping Claude deregistration (uninstalling the plug-in anyway).'
    fi
    echo 'Uninstalling the plug-in...'
    "$YAK" uninstall "$PKG"
    echo 'Done. Restart Rhino to unload the plug-in.'
    exit 0
fi

echo 'Finding the latest Rhino release...'
# Fetch first (set -e catches a network failure with curl's own error), then extract the newest rhino
# .yak URL. The API returns releases newest-first, so the first rhino-mcp-bridge-*.yak asset is latest.
json="$(curl -fsSL "https://api.github.com/repos/$REPO/releases?per_page=100")"
url="$(printf '%s' "$json" | grep '"browser_download_url"' | grep -oE "https://[^\"]*${PKG}-[^\"]*\.yak" | head -1 || true)"
[ -n "$url" ] || { echo 'No rhino-v* .yak release was found on GitHub.' >&2; exit 1; }

tmpdir="$(mktemp -d)"
yakfile="$tmpdir/$(basename "$url")"
echo "Downloading $(basename "$url")..."
curl -fsSL -o "$yakfile" "$url"

echo 'Installing the plug-in with yak...'
"$YAK" uninstall "$PKG" >/dev/null 2>&1 || true   # replace any prior version; ignore "not installed"
"$YAK" install "$yakfile"
rm -rf "$tmpdir"

dir="$(package_dir)"
server="$dir$SERVER"
[ -f "$server" ] || { echo "The plug-in installed but its server binary is missing (expected $SERVER in the package folder)." >&2; exit 1; }
chmod +x "$server" 2>/dev/null || true

echo 'Registering the MCP server with your Claude clients (Claude Code + Claude Desktop)...'
run_timeout 60 "$server" register || echo "  (registration did not finish; run MCPBridgeRegister inside Rhino, or update to a newer release)"

echo ''
echo 'Installed. Next:'
echo '  1. Restart Rhino so it loads the MCP Bridge plug-in.'
echo '  2. Restart your Claude client so it picks up the server:'
echo '       - Claude Code: run  /mcp'
echo '       - Claude Desktop: quit fully and reopen.'
echo 'Run MCPBridgeStatus in Rhino any time to check the connection and registration.'
