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

## Fast subset for local sanity checks

The full suite (all `Test*` functions in this package, `memcheck_test.go`'s two diagnostics
excluded by their own `MCP_HARNESS_MEMCHECK` gate) takes 10-12+ minutes end to end against a real
Revit session — confirmed live, `751.3s` for a full run. `TestApplicationCreatesDocuments` and
`TestCreatedDocumentIsWritable` alone account for roughly 40% of that (206s and 90-104s
respectively): both are dominated by genuine Revit document create/write/close latency (15-30s+ per
document is normal), not artificial waiting -- there's nothing to trim there without weakening what
they verify (behavior *at document-creation time itself*: ambient transaction boundaries,
writability, rollback). Investigated live rather than assumed; see the `revit-connector-development`
skill's changelog for the methodology.

For a quick local sanity check while iterating -- NOT a substitute for the full suite before a PR
that touches document lifecycle/transaction code -- skip those two:

```sh
go test -tags harness ./... -v -skip 'TestApplicationCreatesDocuments|TestCreatedDocumentIsWritable' \
  -broker-exe /path/to/mcp-server-mac -broker-mode remote -broker-bind <ip> -broker-app-data-dir <dir>
```

This is a LOCAL dev-loop convenience only, not a CI concern: `.github/workflows/ci.yml` never runs
any tier-2 test here at all (type-check only, `go vet -tags harness`, per CONTRIBUTING.md's
testing-tiers section) -- there is no CI pipeline to tier by test speed. `-run <pattern>` remains
the way to target one bundle/subtest during focused iteration, same as always; `-skip` is for
"everything except these two" during a broader but still time-boxed sanity pass.

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
- `document_routing_test.go` — `TestDocumentIdRouting`: the live pin of `document_id` routing and
  the issue #30 live snapshot push as one loop (open a background copy, watch it appear in
  `list_instances` with no reconnect, route a script to it by id, assert it ran there with
  `UIDocument` null).
- `execution_lifecycle_test.go` — `TestExecutionLifecycle`: PRD §06's non-inline contract end to
  end (pending/running shape with an `execution_id`, the busy latch pointing at the in-flight run,
  `poll_execution`, cooperative `cancel_execution` resolving to `cancelled`, instance freed after).
- `file_exchange_test.go` — `TestPublishFileExchange`: PRD §09's `Publish`/`files[]` contract
  (per-file `published`; collision without `overwrite_output_files` fails THAT FILE, naming the
  flag, without failing the run; the flag makes the identical publish succeed).
- `zz_cleanup_check_test.go` — `TestZZDocumentCleanupRoundTrip`: runs last by file-name order and
  verifies the cleanup discipline itself, baseline-relative (create → appears in `list_instances`
  via the snapshot push → `closeDocumentByTitle` → disappears).
- `fixtures_test.go` — the fixture-system helpers PRD §13's coverage-plan corpus bundles share:
  `createBlankFixtureDocument` (creates one blank, writable document via `CreateProjectDocument`,
  returns its Title; registers a `t.Cleanup` that closes it via `closeDocumentByTitle` when the
  bundle finishes), `closeDocumentByTitle` (the shared confirm-gated close-and-optionally-delete
  cleanup every document-creating case registers), `cleanupTitles`/`registerCreatedDocumentCleanup`
  (extract the `cleanup-title=` stdout markers scripts print when their return value is spoken
  for), `fixtureLookupPreamble` (the by-Title re-find every subtest needs), and
  `fixtureWritePreamble` (that plus `OpenForWriting(doc)` -- use this one instead whenever a subtest
  WRITES to the fixture document; without it every write throws "Attempt to modify the model outside
  of transaction", since a created document's managed transaction commits and closes the moment the
  call that created it returns). Call `createBlankFixtureDocument` ONCE per bundle, not once per
  subtest.
- `open_for_writing_test.go` — `TestOpenForWritingSafety`: the `OpenForWriting` global's
  headline rollback-on-throw guarantee plus its two negative paths ("adopt the ambient
  document", "adopt the same document twice"), added after an independent review round found
  the adopted-document origin had zero coverage at any tier.
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

Eighteen test functions across `harness_test.go`, `denylist_bypass_test.go`,
`document_routing_test.go`, `execution_lifecycle_test.go`, `file_exchange_test.go`,
`open_for_writing_test.go`, `phase_a_test.go`, and `zz_cleanup_check_test.go` make up the actual
coverage corpus; `memcheck_test.go`'s two are diagnostics, not corpus. The remaining gaps issue
#36 tracked — broker-restart replay and a live heartbeat→`unresponsive` transition — stay
deferred with reasons on record there (disruptive to the shared dev broker; not practically
scriptable without killing Revit).

`TestApplicationCreatesDocuments` is the first case whose subtests are *heterogeneous* — each
asserting a different thing about one capability — rather than table-driven over a single shape
the way the two bundles above it are. That is the shape PRD §13's corpus plan calls for. It covers the
top-level `Autodesk.Revit.ApplicationServices.Application` (reached as `UIApplication.Application`)
and its `NewProjectDocument`/`NewFamilyDocument`, and it also pins a boundary a corpus fixture
system runs into: a created document is outside the executor's ambient transaction. (In-script
addressing through `Application.Documents` is pinned there too; since the v1 remediation series a
created document also gets a routable `tmp-` `document_id`, so that is a convenience rather than
the only mechanism.) Every document-creating case now registers a `closeDocumentByTitle` cleanup —
the old leave-everything-open posture ended when the live snapshot push made leftovers visible in
`list_instances`.

The denylist and lifecycle cases exist here rather than in `MCPBridge.Core.Tests` because they cannot exist there.
Since PRD §14 shipped, `ScriptGlobals.Document` is the real `Autodesk.Revit.DB.Document` — sealed,
non-constructible outside a live Revit session, and living in a mixed-mode assembly a plain test
host cannot even load. Any assertion about what a script actually *gets* from the globals, or how a
denied script surfaces end to end, therefore belongs in this tier by construction.

No `corpus/`/`runner/`/`fixtures/` yet — PRD §13's tutorial-workflow corpus (place a wall, tag a
room, etc.) was blocked on there being no *sanctioned* way for scripts to reach real Revit API
elements; that is resolved (PRD §14, "Real Revit API access from scripts"), so the corpus is now
buildable, just not built. What the suite actually covers today — registration, error shapes,
the sanctioned script globals, the denylist/lifecycle-gate rejections, core CRUD + query, and
the `OpenForWriting` safety cases — is a genuine regression suite in its own right and doesn't
need that structure; a data-driven corpus format is worth introducing once there are enough
cases for one to earn its keep, not before. Known end-to-end coverage gaps — `poll_execution`
/ `cancel_execution` lifecycle transitions and the `Publish`/`files[]` file-exchange path have
no harness cases yet — are tracked in
[issue #36](https://github.com/eichler-ai/connectors/issues/36).
