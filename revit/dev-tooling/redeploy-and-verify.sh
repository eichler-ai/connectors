#!/usr/bin/env bash
# One-shot dev-loop helper: kill Revit, deploy freshly-built DLLs, relaunch (optionally with a
# fixture document), restart the standalone Mac broker to force a fresh add-in reconnect, and wait
# until that reconnect actually lands -- all of it. Replaces a sequence that used to take five-plus
# separate manual steps (each its own round trip) per verification cycle.
#
# Document-launch mode (issue #32): opening a document always loses the race against the add-in's
# one-shot register snapshot (issue #30) -- the add-in connects and registers 0 documents before
# the document finishes opening, and nothing on the VM side can refresh that (the VM launcher
# agent's forced reconnect only restarts the LOCAL-mode broker; the add-in here is connected to
# THIS Mac's broker). So this wrapper streams redeploy-and-verify.ps1's output live and, each time
# the ps1 reports a fresh-but-document-less registration ("STALE_REGISTRATION" marker), restarts
# the Mac broker -- dropping the add-in's connection and forcing a fresh registration that includes
# whatever is open by then. Event-driven on both sides (the ps1's wait is FileSystemWatcher-based;
# the restarts react to actual registrations, never timers), so the reconnect lands seconds after
# the first stale registration in the common case. All of this is removable scaffolding around
# issue #30 -- if the add-in ever pushes live document-snapshot updates, delete it.
#
# Prerequisite: build first (this script does not build) --
#   prlctl exec "<vm>" cmd /c "dotnet build \\psf\connectors\revit\mcp-bridge\MCPBridge.sln --no-incremental"
#
# Usage:
#   revit/dev-tooling/redeploy-and-verify.sh [options]
#
# Options (all optional; defaults match this project's own dev environment):
#   --vm NAME                Parallels VM name (default: "Windows 11")
#   --mac-bind IP            This Mac's address the VM can reach (default: auto-detected, first
#                             10.211.55.x address from ifconfig -- override if that ever changes)
#   --app-data-dir PATH      Shared broker app-data dir (default: <repo>/Connectors/Revit)
#   --broker-exe PATH        Mac broker binary (default: <repo>/revit/mcp-server/mcp-server-mac)
#   --tfm TFM                Build output TFM to deploy (default: net10.0-windows)
#   --revit-version VER      Addins\<VER> folder to deploy into (default: 2027)
#   --doc-source PATH        VM-side pristine fixture .rvt to launch with (needs --doc-dest too)
#   --doc-dest PATH          VM-side working-copy .rvt path (paired with --doc-source), or on its
#                            own to open an existing document directly with no pristine refresh
#   --marker STRING          Byte-grep this string in the deployed MCPBridge.Core.dll before
#                            trusting the deploy (guards against a stale/incremental build)
#   --timeout-sec N          Reconnect wait timeout (default: 150 -- a cold Revit launch alone can
#                            take ~50s before it even attempts to connect)
#   --skip-copy               Only close/relaunch/verify -- DLLs already deployed
#   --skip-relaunch            Only redeploy DLLs -- don't touch the running Revit instance
#   --skip-broker-restart      Don't touch the standalone Mac broker at all: skips both the initial
#                              restart AND the reactive stale-registration restarts described above
#                              (so document-launch mode will likely FAIL with it -- only combine
#                              them if something else forces the post-open reconnect). Use when the
#                              add-in is already connected to a broker that will pick up the new
#                              DLLs fine, e.g. redeploying without relaunching Revit at all.
#
# On success, prints the `go test -tags harness` command line ready to copy-paste, with
# -broker-bind/-broker-app-data-dir already filled in.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

VM_NAME="Windows 11"
MAC_BIND=""
APP_DATA_DIR="$REPO_ROOT/Connectors/Revit"
BROKER_EXE="$REPO_ROOT/revit/mcp-server/mcp-server-mac"
TFM="net10.0-windows"
REVIT_VERSION="2027"
DOC_SOURCE=""
DOC_DEST=""
MARKER=""
TIMEOUT_SEC="150"
SKIP_COPY=false
SKIP_RELAUNCH=false
SKIP_BROKER_RESTART=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --vm) VM_NAME="$2"; shift 2 ;;
    --mac-bind) MAC_BIND="$2"; shift 2 ;;
    --app-data-dir) APP_DATA_DIR="$2"; shift 2 ;;
    --broker-exe) BROKER_EXE="$2"; shift 2 ;;
    --tfm) TFM="$2"; shift 2 ;;
    --revit-version) REVIT_VERSION="$2"; shift 2 ;;
    --doc-source) DOC_SOURCE="$2"; shift 2 ;;
    --doc-dest) DOC_DEST="$2"; shift 2 ;;
    --marker) MARKER="$2"; shift 2 ;;
    --timeout-sec) TIMEOUT_SEC="$2"; shift 2 ;;
    --skip-copy) SKIP_COPY=true; shift ;;
    --skip-relaunch) SKIP_RELAUNCH=true; shift ;;
    --skip-broker-restart) SKIP_BROKER_RESTART=true; shift ;;
    -h|--help) sed -n '2,50p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 1 ;;
  esac
done

if [[ -z "$MAC_BIND" ]]; then
  MAC_BIND="$(ifconfig | awk '/inet 10\.211\.55\./ {print $2; exit}')"
  if [[ -z "$MAC_BIND" ]]; then
    echo "could not auto-detect this Mac's 10.211.55.x address (Parallels shared network) -- pass --mac-bind explicitly" >&2
    exit 1
  fi
fi

BROKER_LOG=/tmp/redeploy-and-verify-broker.log
BROKER_ARGS_PATTERN="$(basename "$BROKER_EXE") -mode remote -bind $MAC_BIND -app-data-dir $APP_DATA_DIR"

# Restart the standalone Mac broker and confirm the replacement actually became PRIMARY -- the
# same broker-lock-race guard the VM launcher agent applies on its side (issue #26): the singleton
# lock goes to whichever process asks first, so a stray secondary (e.g. left by a backgrounded
# `go test -tags harness` run, or this session's own MCP-client-spawned broker) can beat our fresh
# process to it the moment the old primary dies. Only a lock WINNER writes broker.json, so reading
# it back and checking the pid it names is the authoritative test. Confirmation-driven, not
# timer-driven: no fixed sleeps -- each attempt polls broker.json briefly and either confirms,
# kills the impostor it found, or retries.
restart_broker() {
  local attempt deadline pid cmd
  for attempt in 1 2 3; do
    # Kill any broker matching OUR arguments -- the one being replaced, or a prior attempt's
    # unconfirmed process (still alive retrying the lock; leaving it would leak one secondary per
    # attempt, and it could steal the lock from a LATER restart's fresh process).
    pkill -f "$BROKER_ARGS_PATTERN" 2>/dev/null || true
    ( cd "$(dirname "$BROKER_EXE")" && nohup bash -c "sleep 100000 | ./$BROKER_ARGS_PATTERN" >>"$BROKER_LOG" 2>&1 & disown )
    deadline=$((SECONDS + 10))
    while (( SECONDS < deadline )); do
      # Pretty-printed one-key-per-line JSON written only by our own broker -- sed is enough.
      pid="$(sed -n 's/.*"pid": *\([0-9][0-9]*\).*/\1/p' "$APP_DATA_DIR/broker.json" 2>/dev/null | head -1)"
      if [[ -n "$pid" ]] && cmd="$(ps -p "$pid" -o command= 2>/dev/null)"; then
        if [[ "$cmd" == *"$BROKER_ARGS_PATTERN"* ]]; then
          return 0   # a live primary running with exactly our arguments -- confirmed
        fi
        # A live process with the WRONG command line holds the primary lock: an impostor won the
        # race. Kill it (only if it actually looks like a broker -- guards the pid-reuse case) and
        # retry; our just-started secondary may then win, but re-killing and restarting via the
        # outer loop keeps this correct either way.
        if [[ "$cmd" == *mcp-server* ]]; then kill "$pid" 2>/dev/null || true; fi
        break
      fi
      # broker.json missing, unparsable, or naming a dead pid (the old primary) -- the fresh
      # primary rewrites it as soon as it wins the lock.
      sleep 0.2
    done
  done
  echo "WARNING: could not confirm a broker matching this script's arguments became primary within 3 attempts -- see $BROKER_LOG and $APP_DATA_DIR/broker.json" >&2
  return 0   # don't abort: the registration wait downstream will surface the failure with context
}

if ! $SKIP_BROKER_RESTART; then
  echo "==> restarting the standalone Mac broker (fresh primary, confirmed via broker.json)"
  restart_broker
fi

PS_ARGS=(-Tfm "$TFM" -AddinsVersion "$REVIT_VERSION" -TimeoutSec "$TIMEOUT_SEC")
[[ -n "$DOC_SOURCE" ]] && PS_ARGS+=(-DocSource "$DOC_SOURCE")
[[ -n "$DOC_DEST" ]] && PS_ARGS+=(-DocDest "$DOC_DEST")
[[ -n "$MARKER" ]] && PS_ARGS+=(-Marker "$MARKER")
$SKIP_COPY && PS_ARGS+=(-SkipCopy)
$SKIP_RELAUNCH && PS_ARGS+=(-SkipRelaunch)

# React to the ps1's stale-registration markers only when we manage the broker AND launched with a
# document -- otherwise this is a plain passthrough of the streamed output.
REACT_TO_STALE=false
if [[ -n "$DOC_DEST" ]] && ! $SKIP_RELAUNCH && ! $SKIP_BROKER_RESTART; then
  REACT_TO_STALE=true
fi

# Ceiling on reactive restarts, not a retry schedule: each restart is triggered by an actual
# stale registration (evidence the add-in reconnected and the document STILL wasn't open), so
# hitting this many means something else is wrong (a dialog wedging the idle loop, a document too
# large for the ps1's -TimeoutSec) and more restarts won't help.
MAX_FORCED_RECONNECTS=5

echo "==> running redeploy-and-verify.ps1 on the VM (one prlctl exec call, output streamed live)"
if prlctl exec "$VM_NAME" powershell -ExecutionPolicy Bypass -File '\\psf\connectors\revit\dev-tooling\redeploy-and-verify.ps1' "${PS_ARGS[@]}" 2>&1 | {
  reconnects=0
  while IFS= read -r line; do
    printf '%s\n' "$line"
    if $REACT_TO_STALE && [[ "$line" == *"STALE_REGISTRATION:"* ]] && (( reconnects < MAX_FORCED_RECONNECTS )); then
      reconnects=$((reconnects + 1))
      echo "==> stale registration seen -- restarting the Mac broker to force re-registration ($reconnects/$MAX_FORCED_RECONNECTS)"
      restart_broker
    fi
  done
}; then
  echo
  echo "==> ready. Harness command:"
  echo
  echo "cd revit/test-harness && go test -tags harness ./... -v -run <TestName> \\"
  echo "  -broker-exe \"$BROKER_EXE\" \\"
  echo "  -broker-mode remote \\"
  echo "  -broker-bind \"$MAC_BIND\" \\"
  echo "  -broker-app-data-dir \"$APP_DATA_DIR\""
else
  status=$?
  echo
  echo "==> FAILED -- see the PowerShell output above." >&2
  echo "    If it reconnected with 0 documents and a document was expected, a blocking dialog" >&2
  echo "    (e.g. Revit's trial splash) may be wedging the idle loop -- check the screen:" >&2
  echo "    prlctl capture \"$VM_NAME\" --file /tmp/vm-screen.png" >&2
  exit "$status"
fi
