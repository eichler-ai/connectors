#!/usr/bin/env bash
# One-shot dev-loop helper: optionally build, close Revit gracefully, deploy freshly-built DLLs,
# relaunch (optionally with a pristine-copy fixture document), rebuild the Mac broker from this
# checkout and ensure a healthy one is running (restarting only if the binary changed, or the
# current one is missing/dead/mismatched -- issue #116), and wait until the
# add-in's registration actually lands with the expected document count -- all of it. Replaces a
# sequence that used to take five-plus separate manual steps (each its own round trip) per cycle.
#
# Document-launch mode needs no special machinery any more: the add-in pushes a live
# document-snapshot refresh the moment a document finishes opening (issue #30's fix -- "register
# refreshed: N document(s)" in connection.log), and the ps1's registration wait accepts that line
# the same as a connect-time register. The former STALE_REGISTRATION marker/reaction loop this
# wrapper carried (issue #32's workaround: force-restarting the Mac broker per document-less
# registration) is deleted, exactly as its own comment said to do once live push existed. This
# wrapper's remaining jobs: build, UNC-alias resolution, ensure-broker, one streamed prlctl call.
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
#   --share-name NAME        Parallels share holding THIS checkout (default: the basename of
#                             the repo root, which is what the main checkout's share is named).
#                             Set this when running from a git worktree shared in under a
#                             different name, e.g. --share-name describe-fn. The alias
#                             (\\psf\ vs \\Mac\) is still resolved automatically either way.
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
#   --skip-broker-restart      Don't touch the standalone Mac broker at all (skips the rebuild AND
#                              the ensure/restart). Use when the add-in is already connected to a
#                              broker that will pick up the new DLLs fine, e.g. redeploying without
#                              relaunching Revit, or when another process owns the broker. Note what
#                              you are giving up: skill.md and every tool schema live inside the
#                              broker binary, so a session run this way can be served content older
#                              than this checkout (issue #116) -- get_skills' own build field and
#                              `<broker> -version` say which revision is actually running.
#
# On success, prints the `go test -tags harness` command line ready to copy-paste, with
# -broker-bind/-broker-app-data-dir already filled in.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

VM_NAME="Windows 11"
SHARE_NAME="$(basename "$REPO_ROOT")"
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
# a document-open snapshot refresh lands within a moment of the open completing.
say() { echo "==> [${SECONDS}s] $*"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --build) BUILD=true; shift ;;
    --vm) VM_NAME="$2"; shift 2 ;;
    --share-name) SHARE_NAME="$2"; shift 2 ;;
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
# across VM restarts (documented dev-environment.md gotcha; PR #33 review finding when this was hardcoded).
# Resolve it fresh each run -- costs well under a second and removes the single most likely
# environment-drift failure. tr strips the CR that Windows output carries.
say "resolving the VM's UNC alias for the '$SHARE_NAME' share"
# -EncodedCommand, NOT an inline -Command string. prlctl exec corrupts backslash sequences in
# inline commands (documented gotcha), and with an arbitrary share name that is not theoretical:
# a share called "redeploy-fix" makes the path \\psf\redeploy-fix, whose \r was eaten live during
# this flag's own development, yielding "\psfedeploy-fix". Base64/UTF-16LE sidesteps quoting and
# escaping entirely. Output is sentinel-prefixed so a CLIXML/progress preamble can't be mistaken
# for the value. `|| true` because under set -e a prlctl failure would kill the script before the
# actionable error below can print.
ps_encode() { printf '%s' "$1" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n'; }

resolve_cmd="foreach (\$a in @('\\\\psf\\$SHARE_NAME','\\\\Mac\\$SHARE_NAME')) { if (Test-Path (Join-Path \$a 'revit')) { Write-Output ('UNCROOT=' + \$a); break } }"
UNC_ROOT="$(prlctl exec "$VM_NAME" powershell -EncodedCommand "$(ps_encode "$resolve_cmd")" 2>/dev/null | tr -d '\r' | sed -n 's/^UNCROOT=//p' | head -1 || true)"
if [[ -z "$UNC_ROOT" ]]; then
  echo "the VM resolves neither \\\\psf\\$SHARE_NAME nor \\\\Mac\\$SHARE_NAME -- check the share mapping:" >&2
  echo "  prlctl list \"$VM_NAME\" --info | grep -A2 'Host Shared Folders'" >&2
  echo "if this checkout is a git worktree, share it in and name it with --share-name:" >&2
  echo "  prlctl set \"$VM_NAME\" --shf-host-add <name> --path $REPO_ROOT --mode rw" >&2
  exit 1
fi
say "share resolves as $UNC_ROOT"

# IDENTITY GUARD. Everything downstream -- the build, -SrcRoot, the DLLs that get deployed -- is
# read from $UNC_ROOT, NOT from the $REPO_ROOT this script was invoked out of. If those are two
# different checkouts, the run builds, deploys, relaunches and reports PASS having verified an
# entirely different tree: the silent wrong-source failure that --marker exists to catch, and
# which only helps if you already suspected it. Prove they are the same tree rather than trusting
# the names to line up -- drop a uniquely-named probe file here, look for it there.
PROBE=".redeploy-probe-$$-${RANDOM}"
: > "$REPO_ROOT/$PROBE"
# EXIT trap as well as the explicit rm below: a Ctrl-C between the two would otherwise leave
# an untracked probe file sitting in the repo root.
trap 'rm -f "$REPO_ROOT/$PROBE"' EXIT
probe_cmd="if (Test-Path '$UNC_ROOT\\$PROBE') { Write-Output PROBE=MATCH }"
PROBE_SEEN="$(prlctl exec "$VM_NAME" powershell -EncodedCommand "$(ps_encode "$probe_cmd")" 2>/dev/null | tr -d '\r' || true)"
rm -f "$REPO_ROOT/$PROBE"
if [[ "$PROBE_SEEN" != *PROBE=MATCH* ]]; then
  echo "REFUSING TO RUN: $UNC_ROOT is not this checkout." >&2
  echo "  invoked from: $REPO_ROOT" >&2
  echo "  resolved to:  $UNC_ROOT  (share '$SHARE_NAME')" >&2
  echo "Continuing would have built and deployed a different tree and reported PASS. Share this" >&2
  echo "checkout in, then name it:" >&2
  echo "  prlctl set \"$VM_NAME\" --shf-host-add <name> --path $REPO_ROOT --mode rw" >&2
  echo "  $0 --share-name <name> ..." >&2
  exit 1
fi
say "share identity confirmed: $UNC_ROOT is this checkout"

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
  # BROKER FRESHNESS (issue #116). This script deploys the ADD-IN; nothing here ever rebuilt the
  # broker, and install-mac.sh -- the only thing that does -- is a one-time setup step. So the
  # broker serving this dev loop was whatever binary was built whenever that was last run, while
  # skill.md, every tool schema and every tool description are compiled INTO it. That produced a
  # live session where get_skills taught an API surface deleted by #91/#92 and the on-disk file was
  # already correct, which read as a documentation bug for three separate diagnoses.
  #
  # Rebuild unconditionally rather than comparing revisions: `go build` is content-addressed and a
  # no-op run costs well under a second, whereas a revision comparison has to be right about which
  # checkout the toolchain stamped (it is NOT this one when building from a git worktree nested
  # inside another checkout -- see internal/buildinfo's own note). Restart only when the BYTES
  # changed, which keeps the ensure-not-churn property below: an unchanged binary never drops the
  # add-in's live connection.
  if ! command -v go >/dev/null 2>&1; then
    echo "Go toolchain not found on PATH -- required to rebuild the broker (issue #116: a stale broker serves a stale skill.md and stale tool schemas)." >&2
    exit 1
  fi
  broker_hash_before="$(shasum -a 256 "$BROKER_EXE" 2>/dev/null | awk '{print $1}')"
  say "rebuilding the Mac broker from this checkout -> $BROKER_EXE"
  if ! ( cd "$REPO_ROOT/revit/mcp-server" && go build -o "$BROKER_EXE" ./cmd/mcp-server ); then
    echo "broker build FAILED -- refusing to continue against whatever binary is already there" >&2
    exit 1
  fi
  broker_hash_after="$(shasum -a 256 "$BROKER_EXE" 2>/dev/null | awk '{print $1}')"

  # Ensure-not-churn: a healthy primary already running with our exact arguments is reused as is
  # -- restarting it would drop the add-in's live connection for no benefit (a relaunch produces
  # its fresh registration from Revit's side). Anything else -- a changed binary, no broker, dead
  # pid in broker.json, or a primary running with different arguments -- gets the full guarded
  # restart.
  if [[ "$broker_hash_before" != "$broker_hash_after" ]]; then
    say "broker binary changed -- restarting so the running primary is the one just built"
    restart_broker
  elif broker_is_healthy; then
    say "standalone Mac broker already healthy (matching args, live pid) and unchanged by the rebuild -- reusing it"
  else
    say "restarting the standalone Mac broker (fresh primary, confirmed via broker.json)"
    restart_broker
  fi
  say "broker now serving: $("$BROKER_EXE" -version 2>/dev/null || echo 'unknown -- -version not supported by this binary')"
fi

PS_ARGS=(-SrcRoot "$UNC_ROOT\\revit\\mcp-bridge\\src" -Tfm "$TFM" -AddinsVersion "$REVIT_VERSION" -TimeoutSec "$TIMEOUT_SEC")
[[ -n "$DOC_SOURCE" ]] && PS_ARGS+=(-DocSource "$DOC_SOURCE")
[[ -n "$DOC_DEST" ]] && PS_ARGS+=(-DocDest "$DOC_DEST")
[[ -n "$MARKER" ]] && PS_ARGS+=(-Marker "$MARKER")
$SKIP_COPY && PS_ARGS+=(-SkipCopy)
$SKIP_RELAUNCH && PS_ARGS+=(-SkipRelaunch)

# (A STALE_REGISTRATION reaction loop lived here -- forced Mac-broker restarts per document-less
# registration, issue #32's workaround for the one-shot snapshot race. Deleted: the add-in now
# pushes a live snapshot refresh on document open/close/create/activate, and the ps1's wait
# accepts the "register refreshed" line directly. The streamed output below is a plain
# passthrough.)
say "running redeploy-and-verify.ps1 on the VM (one prlctl exec call, output streamed live)"
if prlctl exec "$VM_NAME" powershell -ExecutionPolicy Bypass -File "$UNC_ROOT\\revit\\dev-tooling\\redeploy-and-verify.ps1" "${PS_ARGS[@]}" 2>&1; then
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
  echo "    If it registered 0 documents with no refresh following and a document was expected, a blocking dialog" >&2
  echo "    (e.g. Revit's trial splash) may be wedging the idle loop -- check the screen:" >&2
  echo "    prlctl capture \"$VM_NAME\" --file /tmp/vm-screen.png" >&2
  exit "$status"
fi
