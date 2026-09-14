#!/bin/bash
# Build the distributable Rhino MCP Bridge yak package: ONE cross-platform ("-any") .yak carrying the
# plug-in and BOTH platforms' MCP server binaries, so a single package installs and runs on Windows and
# macOS. This is the release artifact (PRD §15, phase 7) — distinct from deploy-plugin.sh, which builds
# and installs into the local Rhino for the dev loop.
#
# What goes in the package (and why it is cross-platform from one build):
#   - the plug-in's managed assemblies (platform-agnostic IL), its deps.json and the Connector XML docs
#     discovery reads; never RhinoCommon.dll (Rhino loads its own).
#   - the flattened native e_sqlite3.dll (win-x64; also runs on Windows-on-ARM under emulation) via *.dll,
#     and a UNIVERSAL libe_sqlite3.dylib (arm64+x64, lipo'd) so the plug-in's SQLite loads on either Mac.
#   - mcp-server-win-x64.exe (windows/amd64) and a UNIVERSAL mcp-server-mac (darwin arm64+x64), named per
#     Core/Registration/ServerBinaryNames.cs so the plug-in's ServerBinaryLocator finds its platform's one.
#
# Runs on macOS (needs dotnet@8, go, lipo, and a yak CLI). CI runs it on a macOS runner (phase 7 PR 3).
# Usage: rhino/dev-tooling/package-yak.sh [--version X.Y.Z] [--out DIR]
set -euo pipefail

VERSION="0.0.1-dev"
OUT_DIR=""
STAGE_DIR=""   # --stage-only: build + stage the package folder here and STOP before `yak build`.
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --out) OUT_DIR="$2"; shift 2 ;;
    --stage-only) STAGE_DIR="$2"; shift 2 ;;   # for CI: stage on macOS (needs lipo), yak build on Windows
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"          # -> rhino/
OUT_DIR="${OUT_DIR:-$ROOT/dist}"
# Toolchain. The defaults suit a Homebrew dev Mac with a local Rhino; CI (phase 7 PR 3) has neither, so it
# MUST provide its own .NET 8 (set DOTNET_ROOT, e.g. via actions/setup-dotnet) and a standalone yak CLI
# (point RHINO_YAK_PATH at it) — there is no Rhino to borrow yak from on a runner.
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet@8/libexec}"
export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
YAK="${RHINO_YAK_PATH:-/Applications/Rhino 8.app/Contents/Resources/bin/yak}"

# Server binary names — MUST match Core/Registration/ServerBinaryNames.cs (the plug-in resolves these).
WIN_SERVER="mcp-server-win-x64.exe"
MAC_SERVER="mcp-server-mac"

# yak is only needed for the build step, which --stage-only skips (CI stages on macOS, yak-builds on Windows).
TOOLS=(lipo go); [[ -z "$STAGE_DIR" ]] && TOOLS+=("$YAK")
for tool in "${TOOLS[@]}"; do command -v "$tool" >/dev/null 2>&1 || [[ -x "$tool" ]] || { echo "missing: $tool" >&2; exit 1; }; done

echo "==> build plug-in (Release)"
dotnet build "$ROOT/mcp-bridge/Rhino.MCPBridge.sln" -c Release -nologo -v q
BIN="$ROOT/mcp-bridge/src/Rhino.MCPBridge.PlugIn/bin/Release"

# The search_functions / how-to ranking models are go:embed'd from a gitignored assets dir (fetched,
# not committed). Without this the server still builds but ships lexical-only search — and the package
# is ~a quarter the size, which is how a modelless build slips through unnoticed. Idempotent (sha-pinned).
echo "==> fetch search models (for go:embed)"
"$ROOT/../internal/servercore/semsearch/models/fetch-models.sh"

echo "==> build server binaries (cross-compiled, CGO-free)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
( cd "$ROOT/mcp-server"
  GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -o "$WORK/$WIN_SERVER" ./cmd/mcp-server
  GOOS=darwin  GOARCH=arm64 CGO_ENABLED=0 go build -o "$WORK/mac-arm64" ./cmd/mcp-server
  GOOS=darwin  GOARCH=amd64 CGO_ENABLED=0 go build -o "$WORK/mac-amd64" ./cmd/mcp-server )
lipo -create "$WORK/mac-arm64" "$WORK/mac-amd64" -output "$WORK/$MAC_SERVER"

echo "==> stage the package"
# --stage-only puts the staged folder at STAGE_DIR (it must survive for the Windows yak-build job);
# otherwise it lives under WORK and is cleaned after yak build.
if [[ -n "$STAGE_DIR" ]]; then PKG="$STAGE_DIR"; rm -rf "$PKG"; fi
PKG="${PKG:-$WORK/pkg}"; mkdir -p "$PKG"
cp "$BIN"/*.rhp "$BIN"/*.dll "$BIN"/*.deps.json "$PKG"/            # *.dll includes the flat win e_sqlite3.dll
cp "$BIN"/*.xml "$PKG"/ 2>/dev/null || true
# Universal Mac SQLite native, replacing the single-arch flattened one, so the plug-in loads on both Macs.
lipo -create "$BIN/runtimes/osx-arm64/native/libe_sqlite3.dylib" "$BIN/runtimes/osx-x64/native/libe_sqlite3.dylib" -output "$PKG/libe_sqlite3.dylib"
cp "$WORK/$WIN_SERVER" "$WORK/$MAC_SERVER" "$PKG"/
chmod +x "$PKG/$MAC_SERVER"

# Guards: the accidents that break the load or leave a platform unserved.
[[ -f "$PKG/RhinoCommon.dll" ]] && { echo "RhinoCommon.dll must not ship (Rhino loads its own)" >&2; exit 1; }
for f in Rhino.MCPBridge.PlugIn.rhp "$WIN_SERVER" "$MAC_SERVER" libe_sqlite3.dylib e_sqlite3.dll; do
  [[ -f "$PKG/$f" ]] || { echo "package is missing $f" >&2; exit 1; }
done

cat > "$PKG/manifest.yml" <<YAML
name: rhino-mcp-bridge
version: $VERSION
authors:
  - Eichler
description: Rhino MCP Bridge -- lets Claude and other agents drive Rhino 8 and Grasshopper through the Rhino MCP Server (Python 3 + C#, API discovery, verified how-tos).
url: https://github.com/eichler-ai/connectors
keywords:
  - mcp
  - claude
  - automation
  - grasshopper
YAML

if [[ -n "$STAGE_DIR" ]]; then
  echo "==> staged (no yak build): $PKG"
  echo "    run \`$YAK build\` in that folder on Windows to produce the .yak"
  exit 0
fi

echo "==> yak build"
( cd "$PKG" && "$YAK" build )
YAK_FILE="$(ls "$PKG"/*.yak 2>/dev/null | head -1 || true)"
[[ -n "$YAK_FILE" ]] || { echo "yak build produced no .yak" >&2; exit 1; }

mkdir -p "$OUT_DIR"
DEST="$OUT_DIR/$(basename "$YAK_FILE")"
mv "$YAK_FILE" "$DEST"
echo "==> packaged: $DEST ($(du -h "$DEST" | cut -f1))"
echo "    win server $(du -h "$WORK/$WIN_SERVER" | cut -f1) · mac server $(du -h "$WORK/$MAC_SERVER" | cut -f1) (universal)"
