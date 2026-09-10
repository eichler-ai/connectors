#!/bin/bash
# Build the Rhino MCP Bridge, package it with yak, install it into the local Rhino 8, and restart
# Rhino discarding any unsaved document (implementation-plan.md phase 1; spikes/phase-1a-method.md §6).
# Usage: rhino/dev-tooling/deploy-plugin.sh [--no-restart]
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet@8/libexec}"
export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
RHINO_BIN="/Applications/Rhino 8.app/Contents/Resources/bin"
YAK="$RHINO_BIN/yak"

echo "==> build"
dotnet build "$ROOT/mcp-bridge/Rhino.MCPBridge.sln" -c Release -nologo -v q
OUT="$ROOT/mcp-bridge/src/Rhino.MCPBridge.PlugIn/bin/Release"

PKG="$(mktemp -d)"
cp "$OUT"/Rhino.MCPBridge.PlugIn.rhp "$OUT"/Rhino.MCPBridge.Core.dll "$OUT"/Rhino.MCPBridge.RhinoAdapter.dll "$OUT"/Rhino.MCPBridge.PlugIn.deps.json "$PKG"/
cat > "$PKG/manifest.yml" <<YAML
name: rhino-mcp-bridge
version: 0.0.1
authors:
  - Eichler
description: Rhino MCP Bridge (dev build) -- lets Claude and other agents drive this Rhino through the Rhino MCP Server
url: https://github.com/eichler-ai/connectors
YAML
echo "==> package + install"
( cd "$PKG" && "$YAK" build >/dev/null && "$YAK" uninstall rhino-mcp-bridge >/dev/null 2>&1 || true; "$YAK" install ./*.yak )
rm -rf "$PKG"

if [[ "${1:-}" == "--no-restart" ]]; then exit 0; fi
echo "==> restart Rhino"
"$ROOT/docs/spikes/phase-1a/rhino-restart.sh"
