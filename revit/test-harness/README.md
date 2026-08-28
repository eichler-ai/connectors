# Live MCP test harness

Tier-2 tests: they spawn the real `mcp-server` binary and speak actual MCP JSON-RPC over its
stdio — the same entry point a real host (Claude Code) uses — against a real, already-running
Revit + MCP Bridge instance. No mocked wire protocol (PRD's "no fake integration tier" rule,
applied here too). Excluded from `go test ./...` by the `harness` build tag; run explicitly.

This suite does not launch, close, or otherwise manage Revit/VM lifecycle — it assumes an
instance is already up and connected, and any case that needs one SKIPs cleanly if it isn't
(never fails the suite over an environment precondition it doesn't own).

## Building the broker

The harness never builds or embeds `mcp-server` itself — build it for whatever platform is
actually running the broker, then point `-broker-exe` at the result:

```sh
cd ../mcp-server
go build -o mcp-server-mac ./cmd/mcp-server                     # broker runs on this machine
GOOS=windows GOARCH=amd64 go build -o mcp-server-win.exe ./cmd/mcp-server   # broker runs on a Windows box/VM
```

## Running

```sh
go test -tags harness ./... -v -run TestCreateLevel -broker-exe /path/to/mcp-server-mac
```

### Local mode (the common case — Revit and the broker on the same machine)

No extra flags needed. `-mode local` is the default; it always binds `127.0.0.1` and resolves
the platform app-data directory automatically (PRD §05). This is the target deployment topology
and needs nothing machine-specific.

### Remote mode (this project's own Mac + Parallels VM dev environment, or any setup where the
broker and Revit are on different machines)

Requires three additional flags (or their env-var equivalents: `MCP_SERVER_MODE`,
`MCP_SERVER_BIND`, `MCP_SERVER_APPDATA`) matching whatever the *real*, already-running primary
broker was started with — the harness becomes a secondary and proxies through it (PRD §05's
singleton lock-or-proxy design), so these values must match, not just be *valid*:

```sh
go test -tags harness ./... -v -run TestCreateLevel \
  -broker-exe /path/to/mcp-server-mac \
  -broker-mode remote \
  -broker-bind <this machine's IP on the network the other machine can reach> \
  -broker-app-data-dir <the shared-drive directory the real broker writes broker.json to>
```

Getting these values for your own setup: `-broker-bind` is the address the *other* machine
(the one running Revit) can actually reach — in this project's own Mac+Parallels environment
that's the Mac's IP on the Parallels shared network (`ifconfig` on the Mac; typically a
`10.211.55.x`/`bridge100`-style address, not `127.0.0.1`). `-broker-app-data-dir` is whatever
directory you pointed the real broker's own `-app-data-dir` flag at (PRD §05: must live on a
drive both machines can reach, e.g. a Parallels shared folder). Neither value is committed
anywhere in this repo — see `.mcp.json`'s own gitignore entry for why: they're specific to one
developer's machine and network, and would go stale the moment anyone else copied them.

**A mismatched `-broker-mode`/`-broker-app-data-dir` doesn't error** — it makes the harness spin
up its own independent, disconnected broker instead of finding the real one, and every case
SKIPs with "no Revit instance connected" rather than failing loudly. If every case is
unexpectedly skipping, this is the first thing to check.

## Layout

- `mcpclient/` — the MCP-over-stdio client the tests use (subprocess spawn, JSON-RPC framing,
  `tools/call`). Deliberately hand-rolled and minimal rather than pulling in an SDK, so it's
  obvious exactly what's being exercised.
- `harness_test.go` — the cases themselves, currently: `TestCreateLevel`,
  `TestScriptGlobalsExposeRealRevitObjects`, `TestDenylistRejectsOwnTransaction`,
  `TestLifecycleGateRequiresConfirmation`.

The last two exist here rather than in `MCPBridge.Core.Tests` because they cannot exist there.
Since PRD §14 shipped, `ScriptGlobals.Document` is the real `Autodesk.Revit.DB.Document` — sealed,
non-constructible outside a live Revit session, and living in a mixed-mode assembly a plain test
host cannot even load. Any assertion about what a script actually *gets* from the globals, or how a
denied script surfaces end to end, therefore belongs in this tier by construction.

No `corpus/`/`runner/`/`fixtures/` yet — PRD §13's tutorial-workflow corpus (place a wall, tag a
room, etc.) was blocked on there being no *sanctioned* way for scripts to reach real Revit API
elements; that is resolved (PRD §14, "Real Revit API access from scripts"), so the corpus is now
buildable, just not built. What's testable today (registration, discovery, file exchange, error
shapes, execution status transitions, the sanctioned globals, denylist rejections) is a genuine
regression suite in its own right and doesn't need that structure — a data-driven corpus format is
worth introducing once there are enough cases for one to earn its keep, not before.
