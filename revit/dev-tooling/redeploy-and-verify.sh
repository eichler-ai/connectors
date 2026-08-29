#!/usr/bin/env bash
# One-shot dev-loop helper: optionally build, close Revit gracefully, deploy freshly-built DLLs,
# relaunch (optionally with a pristine-copy fixture document), ensure a healthy standalone Mac
# broker (restarting only if the current one is missing/dead/mismatched), and wait until the
# add-in's registration actually lands with the expected document count -- all of it. Replaces a
# sequence that used to take five-plus separate manual steps (each its own round trip) per cycle.
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
# Run this from the MAC HOST (it drives the VM via prlctl); the VM must be running, with the
# launcher agent (launcher-agent.ps1) resident -- this script only drops signal files for it.
#
# Usage:
#   revit/dev-tooling/redeploy-and-verify.sh [options]
#
# Examples:
#   # Full cycle: build, redeploy, relaunch with a pristine fixture copy, verify the document
#   # actually registered (--marker proves the deployed DLL contains that string, i.e. is fresh):
#   revit/dev-tooling/redeploy-and-verify.sh --build --marker 'SomeStringUniqueToYourChange' \
#     --doc-source 'C:\dev\fixtures\harness-live.rvt' --doc-dest 'C:\dev\fixtures\work.rvt'
#
#   # Fast relaunch-only cycle, no document (DLLs already deployed):
#   revit/dev-tooling/redeploy-and-verify.sh --skip-copy
#
#   # Redeploy DLLs only, leave the running Revit alone:
#   revit/dev-tooling/redeploy-and-verify.sh --skip-relaunch
#
# Output contract: streams all progress live; the VM side prints "REDEPLOY_RESULT: PASS|FAIL" and
# this script exits 0 only on PASS (on PASS it also prints a ready-to-paste `go test -tags harness`
# command with the right remote-mode flags). Safe to pipe (`... | tee`, a pipeline consumer that
# waits for EOF): the brokers this script starts are fully detached from its own stdout/stderr, so
# the pipe closes the moment the script exits -- see restart_broker's shape comment for the hang
# the previous form caused. On FAIL, diagnose in this order: the [redeploy] lines
# above the failure (they include Revit's last connection.log lines), C:\dev\launcher-agent.log on
# the VM (did the close/launch signals parse and fire?), and `prlctl capture "<vm>" --file x.png`
# (a modal dialog wedging Revit's idle loop is invisible in every log but obvious on screen).
#
# Options (all optional; defaults match this project's own dev environment):
#   --build                  Build MCPBridge.sln on the VM first (--no-incremental). Without this,
#                             the build outputs already under bin\Debug\<tfm>\ are what get deployed
#                             -- pass --marker to prove they contain your change. (If building
#                             manually instead, beware: prlctl exec mangles \\psf\... paths in
#                             double-quoted bash strings -- single-quote them.)
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
#                              ensure/restart AND the reactive stale-registration restarts above
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
BUILD=false

# All progress lines carry elapsed seconds so a slow phase is visible at a glance (and so future
# tuning has real numbers to work from). Typical healthy run: build ~60-90s if --build; close ~5s;
# launch-to-first-registration ~55s (cold Revit start dominates, nothing here can compress it);
# each reactive reconnect cycle ~8s.
say() { echo "==> [${SECONDS}s] $*"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --build) BUILD=true; shift ;;
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
    # Print the whole comment header verbatim -- length-proof, unlike a hardcoded line range
    # (which silently truncated once already when the header grew).
    -h|--help) awk 'NR>1 { if (!/^#/) exit; sub(/^# ?/,""); print }' "${BASH_SOURCE[0]}"; exit 0 ;;
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
    # Shape matters here, found the hard way (live hang, reproduced in isolation):
    #   ( cd ... && nohup ... & disown )   -- the PREVIOUS form -- left the parenthesized subshell
    # alive for the broker's entire lifetime, still holding this script's inherited stdout/stderr.
    # Run as `redeploy-and-verify.sh | anything`, the consumer then never saw EOF: the script
    # exited but the pipe stayed open, hanging the pipeline indefinitely (the disown inside the
    # parens was also disowning in the WRONG shell -- the inner one -- which is what produced the
    # stray "Terminated: 15" job-control noise on each restart cycle). This form fixes all of it:
    # `exec` replaces the subshell with the nohup'd wrapper (no lingering middleman process),
    # explicit </dev/null plus the log redirects detach it from every inherited fd, and the
    # `& disown` OUTSIDE the parens backgrounds+disowns in this shell, the one whose job table
    # actually matters. The `sleep 100000 |` stdin feeder is still required -- the broker is a
    # stdio MCP server and exits when its stdin closes; the feeder's own ~27h orphaned sleep after
    # a broker restart is a known, accepted cosmetic leak (a sleeping process, no fds held).
    ( cd "$(dirname "$BROKER_EXE")" && exec nohup bash -c "sleep 100000 | ./$BROKER_ARGS_PATTERN" </dev/null >>"$BROKER_LOG" 2>&1 ) & disown
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

# True iff broker.json currently names a live process running with exactly our arguments.
broker_is_healthy() {
  local pid cmd
  pid="$(sed -n 's/.*"pid": *\([0-9][0-9]*\).*/\1/p' "$APP_DATA_DIR/broker.json" 2>/dev/null | head -1)"
  [[ -n "$pid" ]] && cmd="$(ps -p "$pid" -o command= 2>/dev/null)" && [[ "$cmd" == *"$BROKER_ARGS_PATTERN"* ]]
}

# The VM's UNC alias for this repo's share can FLIP between \\psf\connectors and \\Mac\connectors
# across VM restarts (documented SKILL.md gotcha; PR #33 review finding when this was hardcoded).
# Resolve it fresh each run -- costs well under a second and removes the single most likely
# environment-drift failure. tr strips the CR that Windows output carries.
say "resolving the VM's UNC alias for the connectors share"
# Deliberately NO double quotes inside the -Command string: prlctl exec STRIPS them (confirmed
# live -- a quoted "\\psf\connectors" arrived as a bare token and PowerShell tried to run it as a
# command; a new variant of the documented inline--Command corruption class). Every token here is
# spaceless, so unquoted parses fine on the PowerShell side. `|| true` because under set -e a
# prlctl failure would otherwise kill the script before the actionable error below can print.
UNC_ROOT="$(prlctl exec "$VM_NAME" powershell -Command 'if (Test-Path \\psf\connectors\revit) { Write-Output \\psf\connectors } elseif (Test-Path \\Mac\connectors\revit) { Write-Output \\Mac\connectors }' | tr -d '\r' || true)"
if [[ -z "$UNC_ROOT" ]]; then
  echo "the VM resolves neither \\\\psf\\connectors nor \\\\Mac\\connectors -- check the share mapping:" >&2
  echo "  prlctl list \"$VM_NAME\" --info | grep -A2 'Host Shared Folders'" >&2
  exit 1
fi
say "share resolves as $UNC_ROOT"

if $BUILD; then
  say "building MCPBridge.sln on the VM (--no-incremental)"
  # powershell, not cmd /c: prlctl exec + cmd mangles \\...\ UNC paths (documented gotcha,
  # reconfirmed live while writing this flag -- the path arrived as \psf\... and MSBuild failed
  # with 'project file does not exist'). Filter to errors + the summary lines: MSBuild warning
  # blocks (e.g. MSB3277 reference conflicts) ran to 2.3MB on a real run of this repo's solution;
  # the Warning(s) count still shows, rerun by hand if you need the detail.
  if ! prlctl exec "$VM_NAME" powershell -Command "dotnet build '$UNC_ROOT\revit\mcp-bridge\MCPBridge.sln' --no-incremental" | grep -E ': error |Build succeeded|Build FAILED|Warning\(s\)|Error\(s\)|Time Elapsed' ; then
    echo "==> BUILD FAILED -- rerun the dotnet build by hand for full output" >&2
    exit 1
  fi
fi

if ! $SKIP_BROKER_RESTART; then
  # Ensure-not-churn: a healthy primary already running with our exact arguments is reused as is
  # -- restarting it would drop the add-in's live connection for no benefit (a relaunch produces
  # its fresh registration from Revit's side, and document-mode reconnects are handled reactively
  # below). Anything else -- no broker, dead pid in broker.json, or a primary running with
  # different arguments -- gets the full guarded restart.
  if broker_is_healthy; then
    say "standalone Mac broker already healthy (matching args, live pid) -- reusing it"
  else
    say "restarting the standalone Mac broker (fresh primary, confirmed via broker.json)"
    restart_broker
  fi
fi

PS_ARGS=(-SrcRoot "$UNC_ROOT\\revit\\mcp-bridge\\src" -Tfm "$TFM" -AddinsVersion "$REVIT_VERSION" -TimeoutSec "$TIMEOUT_SEC")
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

say "running redeploy-and-verify.ps1 on the VM (one prlctl exec call, output streamed live)"
if prlctl exec "$VM_NAME" powershell -ExecutionPolicy Bypass -File "$UNC_ROOT\\revit\\dev-tooling\\redeploy-and-verify.ps1" "${PS_ARGS[@]}" 2>&1 | {
  reconnects=0
  while IFS= read -r line; do
    printf '%s\n' "$line"
    if $REACT_TO_STALE && [[ "$line" == *"STALE_REGISTRATION:"* ]] && (( reconnects < MAX_FORCED_RECONNECTS )); then
      reconnects=$((reconnects + 1))
      say "stale registration seen -- restarting the Mac broker to force re-registration ($reconnects/$MAX_FORCED_RECONNECTS)"
      restart_broker
    fi
  done
}; then
  echo
  say "ready. Harness command:"
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
