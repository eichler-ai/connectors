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
broker and Revit are on different machines) — **the normal way to run this harness in this project**

Run this NATIVELY ON THE MAC, not through the VM. This was confirmed dramatically faster once
actually tried: the same 6-subtest bundle that used to require cross-compiling for Windows,
copying the binary to the VM, dropping a launcher-agent `*.runexe` signal, and polling for a
result (minutes) ran in **28 seconds** end to end run this way instead — `go test` compiles and
runs directly, `-broker-exe` points at a `mcp-server-mac` binary already sitting in this repo, and
there is no VM round trip of any kind. Reach for the Windows cross-compiled path only if the
broker itself must physically run on the VM for some other reason.

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

**The harness's own `-broker-exe` process only lives for the duration of one `go test`
invocation** (killed in cleanup the moment that process exits) — fine when a real, independently-
running primary broker already exists for it to proxy through as a secondary, but useless as the
thing the add-in itself should register against across separate test runs. If `register`'s
document snapshot is stale (`documents: []` despite Revit genuinely having one open — PRD §05's
one-shot snapshot race, [issue #30](https://github.com/eichler-ai/connectors/issues/30)) and needs
a broker restart to force a fresh reconnect, start a genuinely long-lived STANDALONE broker first
(same "keep stdin open" trick this project uses elsewhere for exactly this reason):
```sh
cd ../mcp-server
nohup bash -c 'sleep 100000 | ./mcp-server-mac -mode remote -bind <bind-ip> -app-data-dir <dir>' &
disown
```
then let the add-in reconnect to it (check its own `connection.log` for a fresh `connected:
auth+register succeeded ... (N document(s))` line) BEFORE running `go test` against it as a
secondary. Restarting your own Claude Code session's `revit` MCP tool's broker this way (if it's
the same process) also drops that tool's own connection — `/mcp` reconnects it, nothing else does.

## Layout

- `mcpclient/` — the MCP-over-stdio client the tests use (subprocess spawn, JSON-RPC framing,
  `tools/call`). Deliberately hand-rolled and minimal rather than pulling in an SDK, so it's
  obvious exactly what's being exercised.
- `harness_test.go` — the shared client/setup helpers plus the capability cases, currently:
  `TestCreateLevel`, `TestScriptGlobalsExposeRealRevitObjects`, `TestDenylistRejectsOwnTransaction`,
  `TestLifecycleGateRequiresConfirmation`, `TestLifecycleGateCoversTheNewlyAddedMembers`,
  `TestApplicationCreatesDocuments`, `TestCreatedDocumentIsWritable`,
  `TestDialogsAreStillAutoSuppressed`.
- `denylist_bypass_test.go` — the bypass-reproduction cases, one per *shape* of reach an
  independent review found live (never one per type name — that axis is what let a hole survive a
  whole review round): `TestConnectorOwnTypesAreNotReachableFromAScript` (constructing our own
  adapters), `TestConnectorCapabilitiesAreNotReachableThroughACallback` (capturing one through a
  script-supplied callback), `TestConfirmationTierCannotBeSelfGranted` (reaching the live
  `ScriptGlobals` via `Delegate.Target` and starting a nested run with the confirmation flag set),
  and `TestScriptCannotTamperWithDialogSuppression` (mutating the static dialog context).
- `fixtures_test.go` — the fixture-system helpers PRD §13's coverage-plan corpus bundles share:
  `createBlankFixtureDocument` (creates one blank, writable document via `CreateProjectDocument`,
  returns its Title -- the only way a later `execute_script` call can find it again, since a created
  document has no `document_id`; registers a `t.Cleanup` that closes it via `closeFixtureDocument`
  when the bundle finishes), `fixtureLookupPreamble` (the by-Title re-find every subtest needs), and
  `fixtureWritePreamble` (that plus `OpenForWriting(doc)` -- use this one instead whenever a subtest
  WRITES to the fixture document; without it every write throws "Attempt to modify the model outside
  of transaction", since a created document's managed transaction commits and closes the moment the
  call that created it returns). Call `createBlankFixtureDocument` ONCE per bundle, not once per
  subtest.
- `phase_a_test.go` — the first coverage-plan corpus bundle, `TestPhaseACoreCRUDAndQuery` (core
  CRUD + query): `CreateWall`, `QueryElementsByCategory`, `GetSetParameter`, `DeleteElement`,
  `CreateSharedParameter`, `EditGroupPropagatesToAllInstances`, each an INDEPENDENT subtest (its own
  `execute_script` call, re-runnable in isolation via `-run TestPhaseACoreCRUDAndQuery/CreateWall`).
  Every script here was run live via `mcp__revit__execute_script` before being committed -- see the
  file's own comments for two real API corrections (a nonexistent
  `Application.CreateSharedParameterFile()`, and `BuiltInParameterGroup` having been removed from
  this API version) and a substantial finding on how model-group member edits actually propagate
  (there is no group-edit-scope API; the real mechanism is `UngroupMembers` → edit → `NewGroup` →
  reassign `.GroupType` on other instances).
- `memcheck_test.go` — throwaway diagnostics, not part of the coverage corpus:
  `TestOpenForWritingMemoryCycles` (N true cross-call create/write/close cycles, for the memory
  investigation logged in [issue #31](https://github.com/eichler-ai/connectors/issues/31)) and
  `TestOpenDocumentCount` (reports `Application.Documents`' current count/titles). Kept around as
  ready-made tools for revisiting that issue, not run as part of a normal test pass.

Thirteen test functions across `harness_test.go`, `denylist_bypass_test.go`, and `phase_a_test.go`
make up the actual coverage corpus; `memcheck_test.go`'s two are diagnostics, not corpus.

`TestApplicationCreatesDocuments` is the first case whose subtests are *heterogeneous* — each
asserting a different thing about one capability — rather than table-driven over a single shape
the way the two bundles above it are. That is the shape PRD §13's corpus plan calls for. It covers the
top-level `Autodesk.Revit.ApplicationServices.Application` (reached as `UIApplication.Application`)
and its `NewProjectDocument`/`NewFamilyDocument`, and it also pins the two boundaries a corpus
fixture system runs into: a created document is outside the executor's ambient transaction, and
it is addressed by a later script through `Application.Documents`, never through a `document_id`.
Each run leaves its documents open in the live Revit session on purpose — see the case's own
comment, and PRD §14.

The denylist and lifecycle cases exist here rather than in `MCPBridge.Core.Tests` because they cannot exist there.
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
