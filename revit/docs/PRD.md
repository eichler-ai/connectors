# Revit Connector

**Product & Technical Design — Draft**

Two components — the Revit MCP Bridge, a Revit add-in that executes dynamic C# against live documents, and the Revit MCP Server, which speaks MCP to Claude and other agents over a local, structured protocol — starting with Revit 2027.

- **Scope:** v1 = Revit 2027 only
- **Trust model:** fully trusted, unsandboxed
- **Transport:** TCP JSON-RPC 2.0, NDJSON framing
- **MCP Server:** Go, single-binary distribution
- **Status:** pre-implementation

> Canonical shareable copy: this document is also published as a designed artifact. When updating this file, republish the artifact from the same session/source so the two don't drift — see the `revit-connector-development` skill for the process.

## Contents

1. [Summary & goals](#01-summary--goals)
2. [Non-goals for v1](#02-non-goals-for-v1)
3. [Competitive landscape](#03-competitive-landscape)
4. [Architecture](#04-architecture)
5. [Connection & multiplexing model](#05-connection--multiplexing-model)
6. [Threading & script execution](#06-threading--script-execution)
7. [Modal dialog suppression](#07-modal-dialog-suppression)
8. [API discovery tools](#08-api-discovery-tools)
9. [File exchange](#09-file-exchange)
10. [Security model](#10-security-model)
11. [Multi-version strategy](#11-multi-version-strategy)
12. [Signing & distribution](#12-signing--distribution)
13. [Validation & test corpus](#13-validation--test-corpus)
14. [Open questions & design gaps](#14-open-questions--design-gaps)
15. [Phased roadmap](#15-phased-roadmap)

---

## 01. Summary & goals

Every existing open-source Revit-MCP project (surveyed below) wraps the Revit API in a fixed catalog of 20–170 pre-built tools — `create_wall`, `export_ifc`, and so on. This connector inverts that: the primary surface is `execute_script`, which compiles and runs arbitrary C# against the live document, with three companion commands — `list_functions`, `search_functions`, `describe_function` — that let the agent read Revit's own API documentation on demand instead of being limited to whatever the plugin author thought to expose.

Goals for v1:

- Run agent-authored C# against an open Revit 2027 document without crashing the host or corrupting the model.
- Suppress modal dialogs raised by API calls so a script's thread never hangs waiting on a click that will never come.
- Let one MCP connection reach multiple simultaneous Revit instances, addressed explicitly by instance and document.
- Give the agent a self-serve way to learn the API surface instead of hard-coding tool wrappers per capability.
- Keep generated files (exports, logs, script history) in one predictable, per-document location on disk.

> **Design principle — observability over silence.** Anywhere the add-in automatically resolves something on the agent's behalf — a suppressed dialog, an auto-dismissed transaction warning, a cancelled execution — that resolution is reported back in the result, never handled invisibly. The agent needs to detect when something was papered over and navigate around it, not just receive a clean-looking success that hides what actually happened. This shapes §07 (non-framework dialogs), §07 (transaction failures), and §06 (cancellation) identically, and should hold for any future automatic-resolution feature, not just the ones specified today.

### Observability & error reporting standard

The principle above only holds if every subsystem reports things the same way. One shared diagnostic-record shape, reused in three places — the `notices` array on any successful result, the `data` field of any JSON-RPC `error`, and every NDJSON log record in §09's `logs/` directory:

```json
{
  "severity": "debug" | "info" | "warning" | "error",
  "code": "<stable machine-readable identifier>",
  "source": "<component tag — matches the repo's module layout>",
  "message": "<specific, concrete — see requirements below>",
  "detail": { "...code-specific structured fields...": "..." },
  "remedy": ["<suggested next step>", "..."]
}
```

- **Which channel carries what.** Auto-resolved warnings (the Failures API in §07, and anything later) populate `notices[]` on the successful result — severity there is `warning`/`info`, never `error`. An error-severity condition rolls the transaction back and surfaces through the JSON-RPC `error` path instead, not `notices` — the two channels don't overlap.
- **`source` is how "find the relevant code" gets satisfied without going stale.** Values match the module names already in the repo layout directly — `mcp-bridge.core.execution`, `mcp-bridge.core.dialogs`, `mcp-server.internal.registry`, and so on — rather than a separately-invented taxonomy or a file:line reference that breaks the moment code moves.
- **`message` is a hard rule, not a suggestion.** Must include the concrete identifiers involved (`execution_id`/`instance_id`/`document_id`, whichever apply) and the actual underlying condition — never a generic wrapper like "An error occurred" or "Execution failed" that discards what a wrapped .NET/Go exception actually said. Wrap, don't replace: the original exception's own message/type is part of `message` or `detail`, never swallowed.
- **`remedy` is expected, not decorative.** "Restart Revit to recover this instance," "call `list_instances` to confirm the new `document_id`, then retry," "dismiss the listed window manually, then reissue `execute_script`." Omit it only when there's genuinely nothing actionable to suggest, not by default.

**Two channels, not one, for the same record.** MCP's `tools/call` contract expects a failed tool call to surface as a normal result with `IsError: true` and readable content — not a JSON-RPC protocol-level error — so the calling agent sees and can react to it as tool output. That's a different shape than the broker↔add-in wire protocol, which is entirely our own design and free to use raw JSON-RPC `error.data` as specified above. Resolution: the shared diagnostic record is the same in both places, just carried differently — literally in `error.data` on the internal wire protocol, and in `CallToolResult`'s structured/text output with `IsError` set at the MCP tool layer. Don't conflate the two when implementing either side.

**`execution_id` is broker-minted.** The broker generates it before forwarding a script to the owning add-in, so it can route `poll_execution`/`cancel_execution` to the right instance without a second round trip; the add-in echoes the same ID back in every response rather than generating its own.

## 02. Non-goals for v1

- **Not multi-version yet.** Build and validate against 2027 only; the project is structured so 2025/2026 support is an additive multi-target step later, not a rewrite.
- **Not sandboxed.** `execute_script` gets full Revit API access with no permission gate, blocklist, or read/write distinction — this is a local, user-initiated dev tool, not a multi-tenant service.
- **Not marketplace-listed at launch.** Signing and Autodesk Marketplace submission are a later milestone; v1 distributes as a signed installer via direct download.
- **Not a fixed-tool-catalog product.** We deliberately don't compete with the existing projects' breadth of pre-built commands — `execute_script` plus API discovery is the bet.

## 03. Competitive landscape

Six-plus open-source "Revit MCP" projects exist as of mid-2026; all are young (single-digit to low-hundreds stars) and none has become a reference standard. The pattern across all of them: a fixed tool catalog is the default, arbitrary script execution is at best a secondary escape hatch, and none expose progressive API discovery.

| Project | Tool model | Dialog handling | API discovery | Multi-instance |
|---|---|---|---|---|
| mcp-servers-for-revit/revit-mcp *(archived)* | 24 fixed tools + `send_code_to_revit` | test dialog only | none | unaddressed |
| IbrahimFahdah/revit-claude-mcp | 46 fixed tools, package-extensible | undocumented | none | unaddressed |
| LuDattilo/RevitCortex | 173 fixed tools + namespace-sandboxed `send_code_to_revit` | shows native confirms, doesn't suppress | capability hiding only, not help text | single bridge implied |
| UV-Tech/revit-claude-mcp | 40 fixed tools, no script exec | disables its own dialog, not general | health-check only | single fixed port |
| Demolinator/revit-mcp-plugin | 48 fixed tools (pyRevit-based) | — | — | — |
| Autodesk Revit Public MCP Server *(official, Tech Preview, 2027)* | fixed query/report/snapshot tools; sources conflict on whether bulk parameter-value edits are included — no version creates or deletes elements | undocumented | none | undocumented |
| AUTOM8LABS MCP Connector *(3rd-party, live on marketplace.autodesk.com)* | 37 fixed tools, read-only free tier | undocumented | none | undocumented |
| **This connector** | **execute_script as primary interface** | **DialogBoxShowing suppression, general** | **list/search/describe against RevitAPI.xml** | **explicit instance + document targeting** |

Two structural facts validate the differentiation: threading via `ExternalEvent` is already standard practice across these projects, so nothing novel is needed there — but modal-dialog suppression, real API discovery, and multi-instance addressing are gaps every surveyed project either skips or fakes.

One newer fact cuts the other way: as of mid-2026, "AI-agent MCP connector for Revit" is a real, approved marketplace category — Autodesk shipped its own official MCP server and a third-party connector is already live — but **every entrant, including Autodesk's own, deliberately avoids arbitrary code execution** in favor of fixed tool catalogs, and Autodesk's own server's write capability (if any) tops out at parameter-value edits — sources conflict on whether even that shipped, and neither confirms element creation or deletion. That consistency across both open-source and officially-sanctioned entrants is the strongest signal available on the open marketplace-review question in §14.

## 04. Architecture

Three tiers, matching the pattern used by comparable GUI-app bridges (Blender MCP, Unity MCP): the **Revit MCP Bridge** (the add-in) never speaks MCP — it's a plain TCP JSON-RPC executor. A separate process, the **Revit MCP Server** — internally called "the broker" throughout this doc — run wherever Claude runs, is the only thing that speaks MCP over stdio, and it fans out to however many Revit instances are alive.

```
Claude / Claude Code  <--stdio-->  Revit MCP Server (broker)  <--TCP/NDJSON-->  MCP Bridge (Revit instance A)
                                    runs on Claude's host                       dials out on startup
                                    registered to Claude as "revit"
                                    owns instances.json registry     <--TCP/NDJSON-->  MCP Bridge (Revit instance B)
                                                                                        dials out on startup
```

MCP tool calls exposed to the agent: `execute_script`, `poll_execution`, `cancel_execution`, `list_functions`, `search_functions`, `describe_function`, `list_instances`.

### Naming & terminology

This connector is the first of what's expected to be several in a shared connectors repo, so naming is deliberately layered rather than ad hoc:

| Context | Add-in | Agent-facing server |
|---|---|---|
| Inside the Revit connector's own directory/docs | MCP Bridge | MCP Server |
| Outside that context — repo root, cross-connector docs, anywhere "MCP Server" alone would be ambiguous | Revit MCP Bridge | Revit MCP Server |
| Inside Revit's own UI | MCP Bridge (no "Revit" prefix — redundant when you're already inside Revit) | n/a |
| Inside Claude's MCP client config | n/a | `"revit"` — the literal server registration key/slug |

"The broker" is the internal engineering shorthand used throughout this doc for the Revit MCP Server — the same single process, not a sub-component inside it; "the add-in" is the equivalent shorthand for the Revit MCP Bridge. There's no umbrella product name distinct from the connector itself — this doc refers to the whole system as "this connector" or "the Revit connector," never as "MCP Bridge" alone, to keep that term unambiguous for the add-in specifically.

> **Repo-wide convention, documented here first.** The Bridge/Server split — an in-process add-in plus a separate agent-facing MCP server — is the general pattern for any future connector to an app with the same constraints (single-threaded UI API, needs an in-process executor), not a Revit-specific naming choice. Captured in the repo root's `CONVENTIONS.md`.

The add-in stays intentionally thin: a TCP client, a Roslyn script runner, a `DialogBoxShowing` handler, and live reflection over `RevitAPI.dll`/`RevitAPI.xml` serving §08's discovery commands directly, on the connection thread rather than through `ExternalEvent`. Every piece of MCP-protocol churn — schema versions, new transport features — lives in the broker, which can be updated without resigning or redistributing the signed Revit DLL. One small piece of user-facing UI sits outside this list deliberately: a single "MCP Bridge" ribbon panel with a "Status" button (Add-Ins tab), showing live connection state and build identity in a non-modal window — a diagnostic convenience the add-in's own live-wiring development made clear was worth having, not a step toward a broader in-Revit UI surface.

### MCP Server implementation language — Go

The broker ships as a self-contained binary, not an npm-installed Node process — the target user shouldn't need a JS toolchain just to run a local bridge. That distribution requirement, not raw SDK maturity, is what decides the language: both Go (`modelcontextprotocol/go-sdk`, built with Google, ~5k stars) and Rust (`rmcp`, ~3.8k stars, actively maintained) have solid official MCP SDKs with first-class stdio transport support. Go wins on the deployment story specifically — a single build-flag cross-compiles a self-contained binary for any target with no extra toolchain install and no runtime DLL dependency; Rust's equivalent cross-compile needs an added target and linker set up first. The broker's own responsibilities (stdio MCP server, TCP listener/client for the add-in protocol, JSON-RPC/NDJSON framing, the singleton lock-or-proxy logic in §05) are all straightforward in Go's standard library, so nothing about the broker's actual workload favors Rust enough to offset the simpler cross-compile.

The primary build target is `GOOS=windows GOARCH=amd64` — local mode (§05) always runs the broker on the same Windows machine as Revit. A `GOOS=darwin` build of the same codebase exists specifically to run the broker on the Mac side of a remote-mode setup (this project's own dev environment); it isn't a second product, just a second build target of one binary.

## 05. Connection & multiplexing model

**Each Revit instance dials out to the broker** on a well-known local port at startup, rather than the broker dialing into Revit. This is the opposite of the obvious design, and it's deliberate:

- **One address, many instances.** The broker owns a single listening port; every Revit instance is just another inbound connection, tagged with an instance ID and its list of open documents. The agent addresses `{instance_id, document_id}` in every call — this is the "single multiplexed port with instance IDs" model, chosen over one-port-per-instance because ports-per-instance push discovery and routing work onto the agent for no real benefit.
- **Outbound-only avoids needing inbound port-forwarding.** Whatever machine the broker ends up bound on, the add-in only ever needs to dial *out* — it never needs a port opened for inbound connections into Revit's machine. That much holds in every topology. It does *not*, on its own, make a Mac-hosted broker reachable from a VM: a loopback-bound broker answers nothing outside its own machine regardless of which direction the dial runs. Reachability is the topology question below, not a side effect of dialing outward.

> **Framing.** JSON-RPC 2.0 defines no message boundary. We use newline-delimited JSON (one object per line) rather than LSP-style `Content-Length` headers — simpler to implement on both the C# `TcpClient` side and the Go broker side, and safe because valid JSON never contains a literal unescaped newline.

Instance registration: on connect, each add-in sends a `register` notification with instance ID (GUID, generated once per Revit process at `OnStartup` — stable for that process's lifetime, independent of any particular broker connection), PID, Revit version, and the list of currently open documents (updated on open/close). The broker maintains this as live state and also mirrors it to a local `instances.json` so the broker itself is restartable without losing visibility (Revit instances just reconnect).

### Local vs. remote topology

Reverse-dial, loopback-only binding, and file-based `broker.json` discovery all silently assumed broker and Revit share a machine and a filesystem. That's true for the real target deployment but false for this project's own Mac+Parallels dev setup, so topology is now an explicit choice instead of one flow with a hidden assumption baked in:

| | Local mode (default) | Remote mode (opt-in) |
|---|---|---|
| When | Broker and Revit on the same OS instance — the real target deployment | Broker and Revit on different machines — e.g. broker on the Mac, Revit in the Parallels VM |
| Broker bind | `127.0.0.1` only | A specific configured non-loopback interface (e.g. the Parallels shared-network host adapter address) — never `0.0.0.0` |
| Add-in discovery | Reads local `broker.json` — valid since it shares a filesystem with the broker | Same file-based discovery, pointed at the shared drive's agreed root instead of local app-data (§09 already needs this drive for file exchange, so discovery reuses it rather than inventing a second mechanism); falls back to an explicitly configured `broker_host:port` only if no shared drive exists |
| Auth | Same-user-local trust, per §10 | Not yet specified — non-loopback exposure needs a real shared secret, tracked as finding 5 in §14 |

The reconnect loop, singleton lock-or-proxy, and instance registry below behave identically in both modes — only the bind address and discovery mechanism change.

### Startup ordering & reconnection

The broker is only started on demand — spawned via stdio whenever an MCP client actually needs it — so it may come up before, after, or interleaved with any number of Revit sessions, and a Revit instance may run its entire session without a broker ever attaching. One mechanism covers all of these instead of special-casing each:

- **Add-in: a single retry loop, not an event-driven connect.** At `OnStartup`, and again immediately after any dropped connection for any reason (broker not yet running, broker crashed, broker restarted), the add-in retries dialing the broker on a backoff (1s, rising to a ~15–30s cap), indefinitely, with no dialog and negligible idle cost. First connect, reconnect-after-restart, and "Revit open with no broker ever showing up" are all this same loop — not three behaviors.
- **Broker: listens before anything else.** Binds and starts accepting TCP connections as the first action on launch, before completing its own MCP/stdio handshake, so an add-in already mid-backoff connects on its very next attempt. In **local mode** it writes `broker.json` (port, PID, start time, and a fresh auth token — see §10) to a platform-appropriate app-data directory as the discoverable source of truth for "where am I," and the add-in reads it on each retry rather than assuming a fixed port. In **remote mode** the same file is written to the shared drive's agreed root (§09) instead, falling back to a configured `broker_host:port` only if no shared drive exists — same retry loop, same file, different location.
- **Recovering state, not just the socket.** On every successful connect — first time or reconnect — the add-in re-sends `register` with its stable `instance_id` and current documents, and replays a small ring buffer of recent execution results (last N / ~10 minutes) that it keeps independently of the socket, so `poll_execution` against an `execution_id` still resolves after a broker restart, as long as Revit itself didn't also restart. If an `execution_id` genuinely can't be found (Revit/the add-in restarted), `poll_execution` returns an explicit "unknown execution_id" error rather than hanging.
- **Heartbeat, not just connection state.** A live TCP connection doesn't mean the add-in is actually responsive — Revit can wedge without the socket dropping. The add-in sends a lightweight periodic ping over the existing connection (no new message type, just piggybacked on the same NDJSON stream); the broker marks an instance `unresponsive` — distinct from `unrecoverable`, which specifically means "cancellation's grace period lapsed" (§06) — after a few missed intervals, and prunes it from the registry after a longer timeout with no ping at all. This is what actually backs `list_instances`' `status` field, rather than it being inferred solely from connection state.

### Broker singleton & port contention

Because the broker is stdio-spawned per MCP client, two concurrent Claude sessions each configured against this server would naively spawn two broker processes racing for the same TCP port. Resolved with a lock-or-proxy pattern, deliberately reusing the same "acquire or fall back" shape as the reconnect loop above rather than introducing separate promotion logic:

- On launch, every broker process attempts to take an exclusive OS-level lock (`LockFileEx`/equivalent) on a single lock file. Whichever process gets it becomes **primary**: binds the TCP port, generates a fresh auth token and writes it into `broker.json` alongside the port/PID, and owns all Revit connections and routing.
- A process that doesn't get the lock becomes **secondary**: it doesn't bind a port at all. Instead it reads the same `broker.json` the primary just wrote — which is how it discovered there already was a primary in the first place — and connects to the primary's TCP port as a client, presenting that same token, tagged at registration as an agent-client rather than a Revit instance, and simply forwards its own stdio MCP calls through that connection for the primary to route. From the agent's point of view behavior is identical regardless of which broker process it happens to be talking to.
- If the primary exits, its lock releases and its `broker.json` goes stale. A secondary that notices its upstream connection drop re-runs the same lock-acquisition step — it may become the new primary, or find another secondary got there first and keep proxying. Revit instances discover the new primary the normal way, through their own reconnect loop reading `broker.json`.

### Instance discovery — `list_instances`

Before an agent can target `{instance_id, document_id}`, it needs to see what's actually live. `list_instances` returns the broker's current registry directly — no round-trip into Revit required, since every field is already tracked from `register`/heartbeat traffic:

| Field | Scope | Notes |
|---|---|---|
| `instance_id` | instance | Stable for the life of the Revit process (§05). |
| `revit_version`, `pid` | instance | e.g. `"2027"` — relevant once multi-version ships (§11). |
| `connected_since` | instance | Timestamp of the current connection, not the Revit process's launch time — resets across a reconnect. |
| `status` | instance | `idle` / `pending` / `busy` / `unrecoverable` / `unresponsive` — the first four mirror the state from `execute_script`/`poll_execution`/`cancel_execution` (§06); `unresponsive` comes from the heartbeat above rather than execution state, and covers the case a script never even reported — Revit wedged without the socket dropping. |
| `documents[]` | per document | `document_id`, title, path (or "unsaved"), workshared flag, and whether it's the instance's active/foreground document. |

Each entry's `document_id` follows the identity scheme in §09 — a `doc-` hash for anything with a stable path (preferring the central model path over a workshared local copy's path), or a `tmp-` session GUID for anything unsaved or detached-and-not-yet-saved, so `list_instances` is legible and stable enough for an agent to pick the same target twice in a row.

## 06. Threading & script execution

The Revit API is single-threaded and callable only from the main UI thread. The TCP socket and Roslyn compilation can happen off-thread, but every actual API touch cannot. Standard pattern, used across the ecosystem (and packaged as `Revit.Async`):

1. TCP thread receives an `execute_script` request, decodes it, and hands the script text to an `IExternalEventHandler`.
2. `ExternalEvent.Raise()` wakes Revit's idle loop; `Execute(UIApplication)` runs on the correct thread.
3. Inside `Execute`, the script is compiled/run via Roslyn scripting, with a globals object exposing `Document`, `UIApplication`, and `UIDocument` into script scope. Isolation and memory lifecycle are covered separately below — the naive "cache every compiled `Script<T>` for the session" approach doesn't hold up under real agent usage.
4. The call is wrapped in a `Transaction`/`TransactionGroup` so failed scripts roll back cleanly; stdout is captured into the result, and any exception populates the JSON-RPC `error` using the shared diagnostic-record shape (§01) — never a bare wrapper message.
5. The result is signaled back to the waiting TCP thread via a blocking handoff (e.g. `TaskCompletionSource`), never returned directly from `Execute`.

> **Risk.** A script that spins forever, or an API call that genuinely blocks on I/O, occupies Revit's UI thread until it returns — there is no preemption.

### Long-running scripts & polling

`execute_script` takes an optional `timeout_ms` parameter (sensible default, e.g. 30000, if omitted). A script that finishes inside the timeout returns the normal completed result inline — no change from the common case. One that doesn't returns either `{status:"pending", execution_id}` or `{status:"running", execution_id}` instead of hanging the MCP call — see the distinction below — and a companion command, `poll_execution(execution_id, timeout_ms?)`, is called however many times it takes — same shapes — until the script actually completes or errors.

> **`pending` vs. `running`.** `ExternalEvent.Raise()` only executes when Revit is idle — it won't fire while the user is in an active edit mode (sketch editor, an in-canvas command) or while a modal dialog is pumping its own message loop. Conflating "queued, waiting for the UI thread to free up" with "actually executing" would mislead the agent, since the first case may resolve on its own shortly and the second is genuinely stuck. The add-in knows which is true — whether `Execute()` has been entered yet — so it reports `pending` until execution actually starts, then `running`. A script stuck at `pending` behind a blocking dialog is the same underlying situation as §07's non-framework-dialog fallback, not a separate problem.

> **Instance busy state.** Because Revit's UI thread runs one script at a time, a timed-out-but-still-running script leaves the whole *instance* occupied, not just that call. A second `execute_script` issued against the same instance while one is still in flight returns `{status:"busy", execution_id}` pointing at the existing run, rather than queuing silently or appearing to start a second execution — the agent is told to poll the one already running instead.

### Cancellation — cooperative, with an honest fallback

There is no safe way to force-stop arbitrary running C# on Revit's UI thread — .NET has no safe thread-abort mechanism, and forcibly killing mid-API-call execution would likely leave document state corrupt. Cancellation is therefore cooperative by design, with a distinct terminal state for the case that doesn't cooperate, rather than pretending either alone solves it:

- **Cooperative path.** The script globals object exposes a `CancellationToken` alongside `Document`/`UIApplication`/`UIDocument`. A script checks it between API calls or at loop boundaries; `cancel_execution(execution_id)` signals it. A script that observes the signal and unwinds gets its transaction rolled back cleanly and resolves to a new terminal status, `cancelled` — distinct from `error`, since the agent asked for this. `describe_function`/example content should actively teach agents to check the token in any loop-shaped script.
- **`max_duration_ms`.** A second, separate parameter on `execute_script` — a hard ceiling on total runtime, independent of the polling `timeout_ms` above (default generous, e.g. 10 minutes). When it elapses, the broker auto-issues the same cancellation signal on the agent's behalf, so a script nobody's actively polling doesn't sit forever silently occupying the instance.
- **The fallback, for scripts that don't cooperate.** Cancellation starts a grace timer (default ~5–10s). If execution hasn't actually stopped by the time it lapses — the script ignored the token, or is blocked in unmanaged/blocking code the token can't reach — the instance's status flips to a new terminal value, `unrecoverable`, both in `poll_execution`'s response and in `list_instances`' `status` field (§05). Further calls against that instance return an explicit error pointing at that state rather than queuing or reporting `busy` — the agent is told plainly the instance needs Revit restarted, not left polling a dead end. Recovery is just a normal restart, which mints a fresh `instance_id` (§05), so the unrecoverable entry naturally ages out of the registry with no special teardown logic — and in the dev environment, exactly what the `prlctl`-driven automation in §13 already does between corpus runs.

### Roslyn isolation & memory lifecycle

Shipping `Microsoft.CodeAnalysis` inside Revit's process, and compiling a fresh assembly per script, has two failure modes that only show up under real usage rather than a quick smoke test — both are addressed the same way, with a custom **collectible `AssemblyLoadContext` (ALC)**:

- **Version collisions with other add-ins.** Other add-ins active in the same Revit process — scripting or automation tooling of the kind commonly installed alongside a plugin like this — may bundle their own, different version of `Microsoft.CodeAnalysis`. .NET resolves an assembly identity once per load context; loading MCP Bridge's Roslyn dependencies into the process's default context risks colliding with whatever another add-in already loaded there. Fix: MCP Bridge loads its own Roslyn dependencies, and every script it compiles, into a dedicated custom `AssemblyLoadContext` it owns — isolated from the default context and from whatever any other add-in does there.
- **Per-script memory growth.** Agent-authored scripts are rarely byte-identical, so a session-lifetime cache of compiled `Script<T>` objects barely hits and each unique script leaves a permanently-loaded assembly behind — a slow leak under exactly the iterative usage this product is for. Fix: each execution's compiled output loads into its own short-lived, **collectible** ALC that is unloaded once the execution completes and its result is captured, so that script's memory is reclaimable by the GC rather than retained for the life of the session. A small bounded LRU (e.g. last 20–50 unique scripts) still caches the compilation itself for the case an agent deliberately re-runs something verbatim — bounded, not indefinite, unlike the original design.

## 07. Modal dialog suppression

The mechanism is `UIControlledApplication.DialogBoxShowing`, registered once in `OnStartup`. It fires before a `TaskDialog` or Revit-framework message box renders; the handler casts the event args to `TaskDialogShowingEventArgs` or `MessageBoxShowingEventArgs` and calls `OverrideResult(id)` with the desired button ID, dismissing it without ever painting a window.

> **Known limitation.** This only catches dialogs raised through Revit's own dialog framework. Third-party add-ins or raw Win32 `MessageBox.Show`/custom modal WinForms windows bypass the event entirely and can still hang the thread. v1 cannot promise 100% coverage.

Default policy: auto-answer with the "safe"/non-destructive option (typically Cancel/No) unless the script explicitly opts into a different per-call policy — an agent running an unattended script should never have Revit silently choose "Delete" or "Overwrite" on its behalf.

### Transaction failures — the Failures API

The most common dialog class a script actually triggers doesn't come from `DialogBoxShowing` at all. Commit-time warnings and errors — "Line is slightly off axis," "N elements will be deleted," unjoined geometry — route through a separate mechanism, Revit's Failures API: `IFailuresPreprocessor`, `FailuresProcessing`, `Transaction.SetFailureHandlingOptions`. A preprocessor is registered on every transaction the script wrapper opens (§06 already wraps each script in a `Transaction`/`TransactionGroup` — this hooks into that same wrapper), inspects `FailuresAccessor.GetFailureMessages()` at commit time, and resolves them programmatically instead of letting Revit render anything.

Resolution policy follows the observability principle above directly: **warnings are auto-dismissed, errors are not.** Any error-severity failure rolls the transaction back and surfaces as a normal script failure in the JSON-RPC result — the agent sees a real problem it needs to react to, not a silently-forced outcome it never asked for. Every failure the preprocessor touches, warning or error, is reported back in `notices[]` using the shared diagnostic-record shape (§01) even when the script otherwise succeeds — so "this ran, but 3 warnings were auto-dismissed" is always visible, never invisible, and always in the same place an agent would check for anything else auto-resolved.

> **Deferred, not designed away.** A more aggressive policy — confidently auto-resolving specific known error cases rather than always rolling back — is plausible later, the same way this section's non-framework-dialog handling (below) plans an allowlist-driven v2. It isn't designed now because it isn't known yet which specific errors recur often enough to justify a confident default; that list gets built from real usage, not guessed in advance.

### Fallback for non-framework dialogs

Two-stage plan, deliberately sequenced so the auto-dismiss heuristics in stage 2 are built from observed real-world dialogs rather than guessed in advance:

- **v1** On an `execute_script` timeout with no `DialogBoxShowing` event fired, the add-in enumerates top-level windows owned by the Revit process (`EnumWindows` + `GetWindowThreadProcessId` — a Win32 call from the TCP/background thread, not the blocked UI thread, so this diagnostic itself is always reachable) and returns their titles and window-class names as diagnostic data in the JSON-RPC error payload — *no automatic action is taken, and none is possible from v1*. This turns a silent hang into "here's what's actually on screen" for a human to dismiss manually. It is **diagnosis only**: a follow-up `execute_script` call cannot target the stuck window, because that follow-up would also run via `ExternalEvent` on the same UI thread the dialog is already blocking (§06) — it would report `pending` and never execute either.
- **v2** Once real usage has surfaced the actual set of recurring non-framework dialogs (expected culprits: third-party add-in message boxes, native file-recovery/corruption prompts), extend the same window-enumeration pass to auto-dismiss matches against a maintained allowlist of known title/class signatures — `WM_CLOSE` or a simulated default-button click. This has to run from the same background thread as the v1 diagnostic, off the UI thread entirely — it's the only mechanism in this section that can actually act on a stuck dialog, not an implementation detail. Deliberately allowlist-based rather than a blind "close anything modal" heuristic, since misfiring on a legitimate window is worse than leaving it to the v1 diagnostic.

## 08. API discovery tools

No surveyed project implements this — it's the clearest gap in the field. Revit ships `RevitAPI.xml` and `RevitAPIUI.xml` next to the DLLs: standard .NET XML-doc sidecar files containing `<summary>`/`<param>`/`<returns>` for every public member — the same source Visual Studio IntelliSense reads. Combined with reflection over the assemblies, this powers all three commands with zero external dependency or network access:

| Command | Implementation |
|---|---|
| `list_functions` | One-level-at-a-time tree over a persistent cache of `RevitAPI.dll`/`RevitAPIUI.dll` (plus any other loaded add-in's API): no args → namespaces; `+namespace` → type names; `+namespace+type` → member names. See "Response size & pagination" below. |
| `search_functions` | Tiered, FTS5-backed ranked match against member names, declaring type names, and XML-doc summary text. |
| `describe_function` | Full XML-doc entry (summary, params, returns) joined against the reflected signature for one fully-qualified member. |

> **Known gap.** `RevitAPI.xml` doc comments are often terse compared to community docs (revitapidocs.com, The Building Coder). v1 ships on the shipped XML alone — sufficient for "what does this method do and what does it take," insufficient for worked examples. A curated example corpus is a plausible v2 addition, sourced from the same tutorial pool as the test corpus (§13), not from the XML.

### Execution locus — live reflection, backed by a persistent cache

Reflection over `RevitAPI.dll`/`RevitAPIUI.dll` doesn't touch the Revit API context at all — no `Document`, no `UIApplication` — so unlike `execute_script` it never needs `ExternalEvent` or the UI thread. Served on the add-in's background connection thread, discovery is simply never subject to the busy/pending state machine in §06 in the first place; a script running on the UI thread has no bearing on it. There's still a language-mismatch reason the add-in has to be the one doing this rather than the broker — the broker is Go (§04) and can't reflect a .NET assembly — but that's the only thing that has to live in the add-in:

- `describe_function` is a single reflected member plus its matching XML-doc node — trivially cheap, live or cached.
- `list_functions`/`search_functions` stay fast because they're scoped by namespace/type or ranked top-N (below).

> **Revision — a persistent SQLite cache, not pure live reflection every call.** The original design here reflected fresh on every call and explicitly rejected a persistent index ("a second, driftable source of truth alongside the live DLL... not worth it to close a narrow case"). Live measurement changed that calculus: enumerating the type list alone cost ~1.5s and a first full-corpus `search_functions` scan cost ~700ms, paid again on every single Revit process launch with nothing carried over — a real, repeated cost, not a narrow edge case. `MCPBridge.Core.Discovery.DiscoveryCache` (Microsoft.Data.Sqlite + FTS5, at `%LOCALAPPDATA%\Connectors\Revit\discovery-cache.db`) now persists the reflected surface across restarts. It stays a cache, not an independent source of truth: `DiscoveryCache.Sync` diffs currently-loaded assemblies against what's stored by file hash on every add-in startup (plus one deferred re-check ~8s later, to catch add-ins that finish loading after `OnStartup` returns — Revit doesn't guarantee load order) and reconciles automatically, so drift self-heals on the next launch rather than accumulating. `search_functions` also gained a real FTS5-backed ranking in the same change, replacing the previous full-corpus hand-rolled scan the ~700ms figure above measured.

- The trade-off from the original design is unchanged: discovery needs **at least one Revit instance connected at least once** to populate the cache in the first place — it can't answer before Revit has ever been launched. What changed is that this population now survives across restarts instead of being paid again every time.

> **Deferred — a pre-indexed function graph.** `list_functions` specifically has a plausible future case for a real, precomputed index — not for availability or speed (the cache above already covers that), but for capability: cross-referencing relationships live reflection doesn't cheaply answer per-call, like "what methods return this type" or "what constructs consume it." Worth revisiting once real usage shows that kind of graph-navigation query actually comes up; not designed now, same as the other v1/v2 splits in this doc (§07 twice, and the example corpus above).

### Response size & pagination

Revit's public API surface is roughly 1,700 types — an unscoped `list_functions` call would be a multi-megabyte answer, and "progressive disclosure" is the entire point of these commands, so dumping everything defeats the design as surely as not having discovery at all. Concretely bounded by the same ceiling confirmed for this project's MCP client elsewhere in this doc (§09): Claude Code caps MCP output at 25,000 tokens by default.

`list_functions` is a strict one-level-at-a-time tree rather than a flat, scopable member dump: called with no arguments it returns namespace names only (with a per-namespace type count); given a `namespace` it returns that namespace's type names (namespace-prefix-stripped, as a single comma-separated string); given `namespace`+`type` it returns that type's distinct member names (own + inherited, type-prefix-stripped, also comma-separated) — `type` without `namespace` is a params error, not a silent resolution. Every tier is paginated (a cursor for more) even though the top tier in practice fits in one page. `describe_function` is the only way to get full per-member detail (signature, summary, params, returns) — deliberately, so browsing stays cheap and full detail is always a single explicit follow-up call, never bundled into a list response by default.

`search_functions` returns a bounded, ranked top-N (default ~20) with the same cursor pattern, and accepts an optional `namespace` filter (same scoping semantics as `list_functions`, applied before ranking). Ranking is tiered, highest first: an exact `Type.Member` match; then all query tokens matched across the member's own name and its declaring type's short name (not the summary) — what makes a query like "wall create" reliably surface `Wall.Create`; then FTS5's own BM25 ranking against name+summary+type name combined, for looser/exploratory queries. `describe_function` is inherently single-member scoped and doesn't need pagination, though a member with many overloads returns compact signatures for all of them by default rather than the full XML-doc entry per overload, to stay well under the ceiling on the common case.

### Multi-version disambiguation (§11)

Once instances of different Revit versions can be connected at once, an unscoped discovery call (no `instance_id`) is no longer safely arbitrary — 2025's and 2027's reflected API surfaces genuinely differ (confirmed live: 557 vs. 724 scoped namespace entries against the two real installs), so silently picking a connected instance would hand back version-specific data with nothing telling the caller it's version-specific, and non-deterministically across repeat calls as connections come and go. The broker's `discovery.Router` resolves the target connection with a read-only reference into the instance registry (§05): if `instance_id` is omitted and every connected instance shares one Revit version, that version is picked as before; if the connected instances span more than one version, the call errors (`ambiguous_instance_version`) with a candidates list (`instance_id` + `revit_version` per connected instance) instead of guessing — deliberately not "merge results across versions," which would create a false single-surface impression and force every caller to reason about namespace/type overlap between two genuinely different APIs. Every discovery response, scoped or not, also carries the resolved instance's `revit_version` so a caller that let the broker pick can still tell which version answered without a separate `list_instances` call.

## 09. File exchange

No established convention exists for CAD-tool-to-agent file exchange — this is original design, not a researched pattern. Keyed by document identity rather than instance PID, so relative paths an agent has already referenced stay valid across a save/reopen (PIDs are ephemeral, document identity isn't).

The workspace tree lives under a **separate root from the rest of this add-in's app data**, deliberately: `imports/`/`exports/` are content a human is meant to browse and manage directly with their own filesystem tools (below), unlike `broker.json`/`instances.json`/the discovery cache (§08), which are internal bookkeeping nobody browses and stay exactly where they already are. Burying human-facing files in `%LOCALAPPDATA%` alongside that internal state would work but be needlessly unfriendly, so the workspace root is `%USERPROFILE%\RevitMCPExchange\` instead — visible in a normal Explorer session, not three folders deep in an AppData path. Shown below for local mode; in remote mode the add-in writes the identical structure rooted at the shared folder instead (e.g. `\\psf\connectors\RevitMCPExchange\...`), per §05.

```
%LOCALAPPDATA%\Connectors\Revit\        # internal bookkeeping — never browsed by a human
  instances.json                        # live registry: instance/doc → connection state
  <revit-version>\discovery-cache.db    # §08

%USERPROFILE%\RevitMCPExchange\         # human-facing — the file-exchange workspace tree
  <document-id>\
    imports\                # files placed here for a script to consume; never auto-deleted
    exports\                # images, IFC, families written by scripts; never auto-deleted
    logs\                   # per-execution NDJSON logs, timestamped — one shared diagnostic-record shape (§01) per line; age out
    scripts\                # history of executed script text, timestamped; age out
    tmp\<instance-id>\      # scratch, per instance sharing this workspace; cleared on document close or age-out
```

Retention splits by ownership, not by a uniform rule: `imports/`/`exports/` hold content someone deliberately asked for — a human's upload, a script's requested output — and auto-deleting either would be a far worse surprise than the disk space they cost, so they're purely user-managed via normal filesystem tools. `logs/`/`scripts/` are audit-trail bookkeeping nobody asked for and nobody owns, generated on every single execution regardless of whether anyone ever reads them — those age out the same way `tmp/` already does.

Scripts reference files by path relative to their document's `RevitMCPExchange/<document-id>/` root; the broker resolves these into an actual path in every MCP tool response, per the mechanism below. Every `execute_script`/`poll_execution` result also carries a `files[]` array alongside `notices[]` (§01) — one entry per file the script published as an output (below), each with its own per-file `status`, never a single aggregate success/failure for the whole set.

### Getting bytes to the agent

A resolved path is not the same thing as file content — whether the agent can act on a bare path depends on whether it has its own filesystem access to wherever that path points, and neither of the two obvious transfer mechanisms (embedding content in a JSON-RPC response, or MCP resources) handles a large export gracefully: both would force a full binary blob through a single stdio message, materialized in memory on both ends, for a payload that couldn't usefully enter the agent's context window anyway even if it arrived. So large-file exchange is designed around *not* routing bytes through MCP at all, wherever possible:

- **Primary mechanism — shared filesystem.** In local mode this is automatic: the workspace directory is already on the one disk everything shares. In remote mode it's the same fix already used for this project's own dev environment — a Parallels shared folder, referenced by its **UNC path** (`\\psf\connectors\` on the Windows/VM side) rather than a locally-mapped drive letter, since drive-letter assignment isn't guaranteed stable — Parallels' own default "Home on 'Mac'" share already claims `Z:` in this dev environment's default configuration, and the connectors share itself landed on `X:` only because `Z:` was taken; either could reassign on a reboot or reconfiguration. The broker, which knows both roots, rewrites every path the add-in reports (Windows-native) into the agent-host-native form (e.g. `\\psf\connectors\RevitMCPExchange\doc-1a2b\exports\view.png` → `/Volumes/RevitShare/RevitMCPExchange/doc-1a2b/exports/view.png`) before it ever reaches the agent. The agent then reads the file with its own filesystem tools, entirely outside the MCP channel — no size limit, no memory-doubling, genuinely graceful for a 1GB export.
- **Fallback — `read_file(document_id, relative_path, offset?, length?)`.** For the case with no shared filesystem at all. Chunked/range-based from the outset, never whole-file-in-one-response; a request exceeding a configurable size threshold with no range specified is rejected with guidance to use the shared-path mechanism instead, rather than attempting the transfer and choking on it.
- **MCP resources** are optional polish for hosts that support them, layered on the same chunked-read primitive — not the primary mechanism. Confirmed: Claude Code does support `resources/list`/`resources/read` (referenced via `@server:protocol://path`), but caps MCP output at 25,000 tokens by default (500,000 characters even with an explicit size declaration) — roughly 100–350KB depending on encoding, nowhere near a 1GB export. This isn't a gap in Claude Code's support; it confirms resources were never going to be the large-file mechanism regardless of host, which is exactly why the shared-filesystem path above is primary and not a fallback.

> **Scoped out of the current implementation pass.** `read_file` and MCP resources only have a real trigger condition once remote mode exists — an agent host with no direct filesystem access to the shared workspace at all. Local mode (the only mode targeted so far) already gives the agent's own host direct disk access, so building either now would be speculative work for a mode not yet in scope; document identity, the workspace tree, and `Publish`/`files[]` below are the actual current build.

### Uploading files to a script

The reverse direction is deliberately the mirror image of the download story above, for the same reason: an agent has no business piping a large file's bytes through a JSON-RPC message any more than it has business receiving them that way. There is no `upload_file`-style tool that accepts file content as a parameter. Instead:

1. The agent-side host places the file(s) into `imports/` using its own filesystem tools, resolved through the same shared root as everywhere else in this section (in local mode this is trivial, since the workspace is already on the one disk everything shares; in remote mode it's the same UNC-path mechanism used for exports above, just written instead of read).
2. The agent calls `execute_script` with a script that reads from the now-present path under `imports/` via ordinary `System.IO` — no different from a script reading any other file it was told about. A script doesn't need to be told or construct that path itself: the globals object §06 already exposes `Document`/`UIApplication`/`CancellationToken` through also exposes `ImportsDirectory` (alongside `ExportsDirectory`, below) directly, so a script can do `System.IO.File.ReadAllBytes(System.IO.Path.Combine(ImportsDirectory, "model.csv"))` without the agent needing to know or embed the workspace's absolute path.

Because "the user should be able to upload or download multiple files, though a single file is by far the more common case" was an explicit requirement, both directions are batch-shaped from the outset rather than a single-path parameter with a list bolted on later: a set of files, not one file, is the base case, and per-file status (below) is what a batch of independent file operations naturally needs.

### Publishing script outputs

A script doesn't construct an `exports/` path itself — it calls `Publish(path, name?)` on the same globals object §06 already exposes `Document`/`UIApplication`/`CancellationToken` through. `Publish` copies (never moves — a script may still want to reference or log its own working file afterward) the file into that document's `exports/` directory and registers it for the response; if the script already wrote directly into `exports/`, `Publish` recognizes the file is already there and just registers it rather than copying it onto itself. Every registered file becomes one entry in the result's `files[]` array (above), each with its own `status` — a failure on one file (disk full, a locked target, a bad source path) never rolls back or blocks the others, the same per-file-independence reasoning behind the upload/download batching above.

**Collisions** are controlled by `overwrite_output_files` (default `false`), a request-level flag on `execute_script` applied uniformly across every file `Publish` touches during that run — not a per-file override, which would be more mechanism than this needs. With the default, a `Publish` call that would overwrite an existing file becomes a `status: "failed"` entry with a message naming the flag, never a silent skip (§01) and never an abort of the rest of the batch. Most real usage is iterative — an agent re-running a script against the same output name expects to overwrite — so `overwrite_output_files: true` is expected to be the common case in practice; the default stays `false` regardless, since a script silently clobbering a prior export is a worse surprise than an explicit failure asking for the flag.

### Browsing a workspace

`list_workspace_files(document_id, subdirectory?)` returns an array of `{name, size, modified_at}` for one workspace directory — metadata only, never content, the same restraint as everything else in this section. It exists so an agent can discover what's already in `imports/`/`exports/` (e.g. confirming a human's upload landed, or checking what a prior script run already produced) without needing a script round-trip just to list a directory.

> **Scoped out of the current implementation pass**, for the same reason as `read_file` above: in local mode, the agent's own host already has direct filesystem access to the workspace tree and can list a directory with its own tools — a script round-trip (`Directory.GetFiles(...)` returned as output) already covers this today with nothing new to build. This tool earns its keep once remote mode exists and that direct access goes away.

### Document identity

`document_id` is not one thing — Revit documents show up in four states that each need a different identity source, and the source has to be something stable enough to survive a re-open, not just "whatever path is open right now":

| Document state | Identity source | ID form |
|---|---|---|
| Saved, non-workshared | Local file path, compared case-insensitively (`OrdinalIgnoreCase` — Windows paths are case-preserving but not case-sensitive); a mapped network drive letter is resolved to its UNC target first (`WNetGetConnection`, best-effort — the same P/Invoke-with-safe-fallback pattern §07 already uses for its window-enumeration fallback) so the same file opened via `Z:\House.rvt` and `\\server\share\House.rvt` still hashes identically | `doc-<hash>` |
| Saved, workshared (local or cloud/ACC central) | **Central model path** (`Document.GetWorksharingCentralModelPath`) or cloud model URN — never the local copy's path, which is per-user and regenerated on every fresh local copy | `doc-<hash>` |
| Unsaved / new / detached-and-not-yet-saved | No stable path exists yet — session-scoped GUID minted on open | `tmp-<guid>` |
| Family document (.rfa) | Same rules as above, applied to the family file's own saved/unsaved state | `doc-<hash>` or `tmp-<guid>` |

The `doc-`/`tmp-` prefix is deliberate — it's visible in every `list_instances` response and every workspace path, so an agent (or a human debugging) can tell at a glance whether a given ID is durable or session-only without a lookup.

> **Promotion on first save — simplified from the original design.** An earlier version of this section specified renaming the workspace folder in place plus a short-lived old-id-to-new-id alias on a `tmp-` document's first save. Independent PR review found that design unreachable in practice — identity is only re-resolved on the next `register`/`execute_script` call after the save, by which point the old workspace folder the rename would have targeted was never the one anything actually wrote into (see the file-exchange PR's own fix for why identity must be resolved once and cached per live document, not recomputed per call) — and found the alias was never consulted anywhere in production. The actual, simpler behavior: a `tmp-` document's already-published files stay under its `tmp-` workspace for the rest of that session; once re-resolution picks up the save (a fresh `doc-<hash>`), the document gets a new workspace going forward, with no rename and no alias. A later Save-As to a different location behaves the same way. Real rename-in-place promotion remains a plausible future enhancement if this proves to matter in practice — not designed now, matching this doc's other v1/v2 splits.

> **Known gap — a `tmp-` ID is not stable across API call sites, so an unsaved document can still scatter its workspace.** The fix referenced above ("identity must be resolved once and cached per live document") caches in a `ConditionalWeakTable` keyed on the Revit `Document` object — which makes the cache *reference*-keyed. Live instrumentation while fixing an unrelated bug then established that Revit does **not** hand back the same managed `Document` wrapper for the same document across API calls: `Application.Documents`'s enumeration and `ActiveUIDocument.Document` returned demonstrably different wrappers (`refEquals=False`) for a single open document. For a **saved** document this is harmless — the ID is derived from the path, so both wrappers resolve to the identical `doc-` hash. For an **unsaved** one it is not: two wrappers mean two cache slots mean two independently minted `tmp-<guid>` values, so `register` can advertise `tmp-A` while `Publish` writes into `tmp-B`'s workspace directory. That is precisely the scattering the per-document cache was introduced to eliminate; it was closed for the saved case and remains open for the unsaved one. Fixing it properly needs an identity key that doesn't depend on wrapper identity for unsaved documents (Revit's own per-document GUID is the obvious candidate) rather than a bigger cache. Not yet addressed — recorded here because it is a §09 identity-correctness issue, not the §05 cosmetic one it was first noticed through.

### Shared workspaces & linked models

- **Two instances, one workspace.** Two Revit instances opening the same central model (or two local copies of it) legitimately produce the same `doc-<hash>` — that's correct, since the workspace is keyed by document *content identity*, not by instance. `exports/`, `logs/`, and `scripts/` already avoid collisions on their own, since every entry is timestamped per execution; `tmp/` is the one directory that isn't, so it gets an `instance_id` subfolder (`tmp/<instance_id>/`) rather than a shared flat scratch space. `list_instances` (§05) surfaces this directly — an agent looking at two entries with the same `document_id` can see they share a workspace, rather than assuming exclusive access it doesn't have.
- **Linked models don't get their own identity.** A linked document is reference data reached *through* a host document's own API surface (`RevitLinkInstance`/`RevitLinkType`, `Document.GetLinkDocument()`-style calls) inside a script's globals — it's not something an agent addresses directly the way it addresses `{instance_id, document_id}`. It gets no `doc-`/`tmp-` ID, no workspace directory, and no `list_instances` entry; a script that needs to touch a link does so via the host document it already has open, the same way any other Revit API code would.

## 10. Security model

`execute_script` is full code execution inside the Revit process, by design (§02). That makes the transport-level defaults non-negotiable even though the script trust model itself is deliberately unsandboxed:

- In **local mode** (§05, the target deployment) the broker binds to `127.0.0.1` only — never `0.0.0.0` — which is the actual protection, since nothing outside the machine can reach the port at all.
- In **remote mode** (§05) the broker binds a specific non-loopback interface by necessity, so loopback can't be the safeguard — the token below is what actually carries the weight there.
- The **broker** — not the add-in — generates a random token whenever it wins the singleton lock and becomes primary (§05), and writes it into the same `broker.json` it already publishes for discovery. Every connecting party (Revit add-ins, and secondary broker processes acting as agent-client proxies, §05) must present it on first message before any other command is accepted — this closes the specific hole where an unrelated local process could register as an agent-client and drive `execute_script` for free.
- **Honest limit:** since `broker.json` is readable by the same OS user in local mode, a genuinely malicious same-user process could read the token out of it too — the token doesn't add a boundary *within* the same-user-local-trust model already assumed here, it only filters accidental cross-talk from unrelated software. In remote mode, though, it's the only real protection the port has, which makes remote-mode security bounded by who has access to the shared drive (§09) the token now rides on — a real, named limitation, not hidden.

## 11. Multi-version strategy

Revit 2025 moved the API to .NET 8 (from .NET Framework 4.8) — a hard runtime break, not a compatibility shim situation. **Corrected during Phase 1 implementation, verified directly against the installed product**: Revit 2027 specifically requires .NET 10, not .NET 8 — `RevitAPI.runtimeconfig.json` declares `"tfm": "net10.0"` with a `Microsoft.NETCore.App`/`Microsoft.WindowsDesktop.App` version 10.0.0 requirement, and the add-in fails to compile under `net8.0-windows` against the real `RevitAPI.dll`/`RevitAPIUI.dll` (a hard `CS1705` assembly-version conflict, not a suppressible warning). v1 targets 2027 only, now on `net10.0-windows`; the project is still structured as a multi-target `.csproj` (`<TargetFrameworks>`) from day one, but the assumption that 2025/2026/2027 all share one .NET generation no longer holds — Autodesk evidently bumped the runtime again between the 2025/2026 generation and 2027. **2025 verified directly**: with Revit 2025 installed alongside 2027 on the dev VM, `RevitAPI.runtimeconfig.json` confirms `"tfm": "net8.0"` (`Microsoft.NETCore.App`/`Microsoft.WindowsDesktop.App` both `8.0.0`) — the original pre-2027-correction assumption holds for 2025 specifically, so Phase 6's `net8.0-windows` target for 2025 is confirmed, not just assumed — and has since **shipped and been live-validated** against a real 2025 install (§15 phase 06), so the project now genuinely builds and runs on two TFMs rather than one, even though v1's own scope above remains 2027. **2026 remains genuinely unverified** (not installed on this VM) — per the same lesson 2027 already taught this project, don't assume it matches 2025 just because they're adjacent releases; verify it directly the same way before Phase 6 adds a 2026 target.

## 12. Signing & distribution

Unsigned add-ins load in Revit today, with an "untrusted publisher" warning on every startup — itself a modal dialog worth suppressing during early internal testing, separate from the general dialog-suppression story. Signing is an Authenticode signature on the compiled DLL only (the `.addin` manifest XML is never signed), applied via `signtool.exe` as a post-build step; timestamping is required so the signature doesn't lapse when the certificate itself expires.

There's a relevant precedent among widely-used Revit scripting tools: the most established one has never been distributed through the Autodesk Marketplace, shipping instead as a signed installer from GitHub Releases with its own extension registry layered on top — a durable end-state, not just a stopgap. Its own maintainers, asked directly whether it could go on the App Store, had no official answer beyond a guess of "probably not." v1 follows the same path; Marketplace submission is deferred to a later milestone and treated as genuinely uncertain rather than a formality (§14).

### Installation UX — a PowerShell script, not a GUI installer

That precedent's own installer is a packaged GUI (Inno Setup) — but its update story splits in two: the core app needs the installer re-run for a new version, while installed extensions update in place via `git pull` against their own repos. Neither half of that transfers cleanly here: our extension surface isn't interpreted Python re-read on each launch, it's a single signed, compiled DLL, so there's no "just pull new files" path at all — every update, small or large, means replacing that DLL.

Given that, a full GUI installer buys less than it costs. The chosen path is a single PowerShell script (`Install-MCPBridge.ps1`), run either as a downloaded file or piped directly (`irm .../install.ps1 | iex`, matching the same pattern this project's own dev tooling already leans on via `dotnet-install.ps1`):
- **Self-upgrade reuses the exact same code path as install** — there is no separate "installer mode" vs. "updater mode" to design or maintain; the script is idempotent by construction (see below), so the ribbon's update click just re-invokes it.
- **Less to build and sign.** No separate installer-authoring toolchain; only the add-in DLL itself needs the Authenticode signature (already required regardless, per this section's first paragraph).
- **Matches the audience.** Anyone installing a connector that lets an AI agent run C# against their live Revit session is already past the comfort level a GUI wizard exists to protect.

The tradeoff, accepted deliberately: no automatic Programs & Features entry (the script writes one itself — an uninstall registry key under `HKCU`/`HKLM:\...\Uninstall\` pointing back at a `-Uninstall` invocation of itself), and a downloaded-then-double-clicked `.ps1` still hits PowerShell's execution-policy gate (the piped one-liner doesn't, since policy checks apply to script *files*, not piped content) — worth documenting the piped form as primary and the downloaded form as the fallback for anyone who wants to read it first, which this audience often will.

### Self-upgrade

The two components have genuinely different update stories, because only one of them is something Windows can hold a file lock on:

- **MCP Server (Go broker)** is a normal long-running process — nothing has it locked. It can check GitHub's latest-release API itself and a single click can download-and-relaunch the new binary immediately, no restart of anything else required.
- **MCP Bridge (the add-in DLL)** is loaded for the whole Revit session; it cannot be hot-swapped while Revit is running. The realistic ceiling on "one click" here is: detect the update, download it silently in the background, then one click either closes and relaunches Revit right away or defers to Revit's own next natural restart — "one click" means *one click commits to it*, not that the loaded DLL is patched in place.

Both are driven by the same install script (`-Update -Silent` for the non-interactive, ribbon-triggered case). The UX surfaces through the ribbon Status button already built for connection/build-identity display (§04's "intentionally thin" exception) rather than any new UI surface — extended with an "Update available (vX.Y.Z)" state, sourced from a version field added to the same status the add-in already polls from the broker.

**Idempotency is load-bearing, not incidental** — the same script runs unattended from the ribbon, so re-running it with nothing to do must be a true no-op: one `GitHub` API call, a version-string comparison against a locally-written marker, done. That comparison must check the *actual deployed DLL's presence*, not just the version marker — a marker claiming "current" while the DLL is missing (deleted by hand, a failed prior run, AV quarantine) must trigger a repair, not a silent no-op that leaves a broken install unfixed forever. Three outcomes, not two: version matches + DLL present → true no-op; version matches + DLL missing → repair (redeploy, logged as a repair, not a version bump); version differs → normal update.

### Multi-version installs

Revit's own `AddInLoader` already isolates this for us: `Addins\<year>\` is a separate folder per major version, and Revit only loads manifests from the matching year's folder (§04's deployment-location finding, extended). The script detects every installed Revit version and deploys to each one's Addins folder automatically, with no "which version?" prompt — cheap to do, and asking would be pure friction. It's bounded to versions we actually ship a build for: today that's 2027 only, so a detected-but-unsupported 2026 install is skipped with a note, not attempted. §11's already-flagged multi-version runtime split (2027 needs .NET 10; 2025 needs .NET 8; 2026 unverified) means those future variants are genuinely different builds per version, not copies — the release artifact will need one build per supported version-TFM once Phase 6 lands, which the install script's per-version loop already accommodates without needing a rewrite.

One update-flow wrinkle worth deciding now: an update click must not force-close every open Revit across every installed version just because one of them triggered it. The script should only redeploy to version-folders whose `Revit.exe` isn't currently running, leaving any version that IS running to finish updating on its own next restart — a partial "2026 updated, 2027 will finish next restart" outcome, not an all-or-nothing shutdown.

**Installed on multiple versions and usable simultaneously are different claims.** The installer only answers the first: deploying to every detected version's Addins folder says nothing about whether an agent can address a specific one of two concurrently-running Revit instances (say, 2026 and 2027 both open at once) — that's the deferred multi-instance/`list_instances`/`{instance_id, document_id}` routing half of Phase 2 (§15), not something this section solves.

### Mac + Parallels

There is no macOS build of Revit, so this was never a case for a second add-in installer — it's this project's own existing dev topology (§05 remote mode). A Mac-based user runs the Windows install script inside their Parallels VM for the add-in half exactly as above, and runs the MCP Server broker natively on the Mac host in `-mode remote`, pointed at the VM's shared folder. The Go broker already cross-compiles for macOS with no code changes (`internal/singleton`'s `lock_unix.go`/`lock_windows.go` split already exists for this). What's missing is not code but a short, separate macOS/bash counterpart to the install script — fetching and placing the Mac broker binary and wiring the remote-mode flags — not a redesign of anything above.

> **Known gap.** Nothing above works until a release pipeline actually exists to produce what the script downloads: a signed zip per supported version-TFM plus a `checksums.txt`, published to GitHub Releases with a stable asset-naming convention the script can rely on. That pipeline is unbuilt — a prerequisite for shipping this design, not just for writing the script itself.

## 13. Validation & test corpus

Correctness here isn't unit-testable in the conventional sense — the surface under test is "can an agent, given only `execute_script` + the three discovery commands, actually accomplish real Revit workflows." The validation strategy is a growing corpus of test cases drawn from real tutorials, run end-to-end against a live Revit session:

- **Sourcing:** beginner and advanced workflows pulled from Revit API tutorials (Building Coder archive, Autodesk's own API samples, Revit API forum threads, YouTube/course walkthroughs) — each becomes a natural-language task description plus an expected-outcome check (element count, parameter value, exported file exists).
- **Grading:** pass/fail per case, run against an agent that has *only* the four MCP Server tools — no hand-written wrapper tools — so the corpus directly measures whether API discovery is sufficient for an agent to self-teach the workflow.
- **Regression:** re-run the full corpus on every add-in change and before each Revit-version addition, since a passing case today is the guardrail against a future refactor silently breaking dialog suppression or the threading handoff.

### Dev-environment automation

The dev machine (Mac + Parallels running Windows/Revit) is not the target deployment — the common case is Windows-native, Claude and Revit on the same box (§05) — but it does need to run the regression corpus unattended, and needs the remote-mode topology (§05) exercised for real, not just designed on paper. Two pieces of tooling already exist for this rather than being hypothetical: `prlctl` controls VM lifecycle (`prlctl start`/`stop`/`restart`) and can execute commands inside the guest once Parallels Tools are installed (`prlctl exec <vm> ...`), enough to launch/kill Revit.exe and drive a clean-state test loop from the Mac without manual interaction in the VM window; and a Parallels shared folder (`\\psf\connectors\` on the Windows side, backed by the repo's own directory on the Mac) is already configured between the two OSes with an agreed root path, which is what §05's remote-mode discovery and §09's file exchange are both specified against. Reference it by UNC path, not a mapped drive letter — letter assignment isn't guaranteed stable across reboots, and Parallels' own default "Home on 'Mac'" share already occupies `Z:` in this environment. (`prlsrvctl` is a separate tool — it configures the Parallels Desktop service/host-level settings, not individual VMs, and isn't the right tool for this.) Together these turn "regression: re-run the full corpus on every add-in change" from an aspiration into something actually scriptable: restart the VM or just Revit between runs, wait for the add-in's reconnect loop (§05) to re-establish over the shared drive, then fire the corpus.

### Competitive coverage floor

Beyond tutorial-sourced cases, the corpus explicitly includes one task per fixed tool exposed by every surveyed fixed-catalog competitor (§03) — starting with the two broadest published tool surfaces among them. Each becomes a task phrased at the same level ("get all door widths on level 2," not "call door-width tool") and run against `execute_script` + discovery only. The bar isn't matching their tool list — it's confirming `execute_script` can reach every capability a fixed-tool competitor hard-coded, without the MCP Server ever needing an equivalent hard-coded tool of its own. Any task that fails here is a real API-discovery gap, not just a missing tutorial case, and gets priority over newly-sourced cases.

## 14. Open questions & design gaps

### Original open questions

Five gaps identified while first drafting this PRD, resolved before the adversarial review below ever ran.

**Non-Revit-framework modal dialogs** *(resolved)* — Two-stage plan (see §07): v1 ships timeout + window-inventory diagnostic only, no auto-action. v2 adds allowlist-based auto-dismiss once real usage shows which non-framework dialogs actually recur — sequenced deliberately so the heuristics are built from observed cases, not guessed.

**Long-running or runaway scripts** *(resolved)* — `execute_script` takes an optional `timeout_ms`; on lapse it returns `{status:"pending"|"running", execution_id}` instead of hanging, and `poll_execution` is called until the script completes (see §06). A second `execute_script` against a still-busy instance returns `{status:"busy", execution_id}` rather than queuing silently.

**Startup ordering & broker restart** *(resolved)* — One reconnect loop (§05) covers first-startup, Revit-before-broker, broker-before-Revit, Revit-with-no-broker-ever, and broker-crash-and-restart alike; a ring buffer of recent execution results lets `poll_execution` survive a broker restart. Concurrent Claude sessions are handled with a lock-or-proxy singleton: one broker process binds the port and routes, others proxy their stdio traffic through it rather than contending for the port.

**Document identity for the workspace key** *(resolved)* — Four states, four sources (§09): saved standalone → hashed local path; saved workshared → hashed *central* model path, never the local copy's path; unsaved/detached-not-yet-saved → session-scoped `tmp-` GUID; family documents follow the same rule as their saved state. First save promotes a `tmp-` ID to a `doc-` hash by renaming the workspace folder in place, with a short-lived alias so in-flight references don't just 404.

**Marketplace review vs. arbitrary script execution** *(researched, still genuinely open)* — Official APS Revit publisher guidelines say nothing explicit about scripting, custom code, eval, or sandboxing — the only relevant clause is a general stability requirement, and review mechanics beyond that aren't public. No case of a submission being rejected *or* approved specifically for code-exec was found either way. But the surrounding pattern is a real signal: every marketplace-listed AI-agent connector surveyed (§03) — including Autodesk's own official first-party entrant — sticks to fixed, mostly read-only tool catalogs and avoids arbitrary execution entirely, and the closest general-purpose scripting precedent was never submitted to the Marketplace at all (§12). Reading: no rule forbids it, but nothing resembling it has been tried, and Autodesk's own product choice leans away from it. **Plan:** treat Marketplace listing as optional rather than a target v1 must clear — independent signed-installer distribution (§12) is the durable path regardless of outcome; if a submission is attempted, be ready to offer a restricted/reviewed-mode fallback (e.g. a fixed high-level tool subset) rather than assuming full `execute_script` clears review.

### From adversarial review

An independent adversarial pass (Aug 2026) surfaced ten further findings, tracked here and resolved in the same one-at-a-time process as the original five.

1. **Local vs. remote topology was unspecified** *(resolved)* — The reverse-dial argument (§05) needs the VM to reach a non-loopback host address; §10's "loopback-only" rule and file-based discovery both assumed same-machine. Now split into an explicit **local mode** (default; loopback bind, file discovery — the real target deployment) and **remote mode** (opt-in; configured host:port, non-loopback bind on a specific interface, broker builds for macOS too). Remote-mode authentication is still open — see finding 5.
2. **No cancellation for runaway scripts** *(resolved)* — Cooperative `CancellationToken` in script globals plus `cancel_execution` (§06) handles well-behaved scripts; `max_duration_ms` auto-triggers it so an unpolled script doesn't run forever unnoticed. For scripts that don't cooperate, a grace-period fallback marks the instance `unrecoverable` rather than leaving the agent polling a dead end — recovery is a plain Revit restart, which mints a fresh instance_id.
3. **Dialog suppression missed the Failures API** *(resolved)* — An `IFailuresPreprocessor` on every script transaction (§07) auto-dismisses warnings, rolls back and surfaces errors as a real script failure, and always reports what it touched — never silent, per the observability principle (§01). A confident-auto-resolve mode for specific recurring errors is deferred until real usage shows which ones recur, mirroring the non-framework-dialog v1/v2 split.
4. **File exchange exposed paths, not content** *(resolved)* — Shared filesystem is now the primary mechanism (§09) — a mapped drive with an agreed root-path pair in remote mode (also now backing broker discovery, §05), automatic in local mode — with the broker rewriting paths into agent-host-native form so the agent's own filesystem tools do the I/O outside MCP entirely. A chunked `read_file` is the fallback for no-shared-filesystem setups. Confirmed Claude Code supports MCP resources, but its 25,000-token default output ceiling rules them out as the large-file mechanism regardless — validates shared-filesystem-first rather than resources-first.
5. **Singleton/token design authenticated nothing** *(resolved)* — The broker (not the add-in) now mints the token on becoming primary and writes it into `broker.json`; every add-in and secondary agent-client proxy must present it (§05/§10). Honestly scoped rather than overclaimed: in local mode it only filters accidental cross-talk (same-user processes can still read `broker.json`), but in remote mode it's the real protection, bounded by who has access to the shared drive it now rides on (§09).
6. **ExternalEvents don't always fire** *(resolved)* — New `pending` status (queued, UI thread not yet free) is now distinct from `running` (§06), surfaced in `poll_execution` and `list_instances`. §07's v1 fallback no longer claims a follow-up script can act on a stuck dialog — it's diagnosis-only by design, since any remedy has to run off the UI thread entirely, which is exactly what v2's allowlist auto-dismiss already does.
7. **Roslyn-in-process assembly collisions and memory growth** *(resolved)* — A dedicated collectible `AssemblyLoadContext` (§06) isolates MCP Bridge's own Roslyn dependencies from other add-ins' bundled versions, and each execution's compiled script loads into its own short-lived ALC that's unloaded on completion — bounding memory growth instead of retaining every unique script for the session, with a small LRU for genuine re-runs.
8. **Discovery commands' execution locus and response size were unspecified** *(resolved)* — Reflection doesn't touch the Revit API context, so it never needed `ExternalEvent` — served live on the add-in's background connection thread, discovery is simply never subject to §06's busy/pending state at all (§08). Accepted trade-off: needs at least one connected instance, no persisted index. Bounded pagination (~50 for `list_functions`, ~20 for `search_functions`) is sized against Claude Code's confirmed 25,000-token MCP output ceiling (§09). A precomputed function-graph index for `list_functions` is deferred to real usage, not designed now.
9. **Core bet validated last, not first** *(resolved)* — Flagged by the review as the single biggest project risk, reasoning from the doc alone. In practice this is substantially de-risked already: prior experience building execute_script-like capability against Revit informs the confidence behind this design, which a fresh review reading only the PRD text has no way to see. Phase 04 stays where it is — no forced pre-infrastructure spike — but the corpus's job (§13) is unchanged: it's still the thing that would surface a gap if one exists, and the deferred example-corpus idea (§08) remains available to pull forward if phase 04 results call for it.
10. **Document-identity edge cases** *(resolved)* — Two instances sharing one central model's workspace is correct-by-design; the fix was giving `tmp/` a per-instance subfolder (the only un-timestamped directory) and surfacing shared `document_id`s in `list_instances` (§09). Linked models deliberately get no top-level identity — reached through the host document's own script globals, not addressed directly. A real heartbeat now backs the `status` field, with a distinct `unresponsive` state separate from execution-driven `unrecoverable` (§05).

## 15. Phased roadmap

1. **Core loop, Revit 2027 only.** Add-in with TCP client and startup/reconnect loop; Go broker (official MCP SDK, stdio transport) built for both local and remote mode (§05), with singleton lock-or-proxy plus broker-minted token auth for concurrent sessions, `execute_script`/`poll_execution`/`cancel_execution` over the NDJSON transport, ExternalEvent threading with a per-execution collectible `AssemblyLoadContext`, unsigned dev build. Success: a script written by an agent can create/modify/query elements in an open document and get a JSON-RPC result back, including a long-runner that's polled to completion and a deliberately-cancelled one that rolls back cleanly, surviving a broker restart mid-run, with memory reclaimed after each unique script.
2. **Dialog suppression & multi-instance.** `DialogBoxShowing` handler with default-safe auto-answer policy; `IFailuresPreprocessor` for transaction warnings/errors with structured reporting; `pending`/`running` status distinction; timeout + window-inventory diagnostic (diagnosis-only in v1) fallback for non-framework dialogs; instance registry, heartbeat-backed `list_instances`, and `{instance_id, document_id}` routing through the broker with two-plus concurrent Revit instances on the same central model validated live.
3. **API discovery.** Add-in serves `list_functions`/`search_functions`/`describe_function` via live reflection over `RevitAPI.dll`/`RevitAPI.xml` on its background connection thread, with pagination, independent of §06's busy/pending state. Structured file exchange directory implemented and wired into script globals, keyed by the `doc-`/`tmp-` document-identity scheme with save-time promotion, with path rewriting for the shared-drive remote-mode case and chunked `read_file` as the no-shared-filesystem fallback.
4. **Validation corpus.** First 15–20 test cases sourced from tutorials, spanning beginner (place a wall, tag a room) to advanced (schedule generation, family parameter automation) workflows, plus the competitive-coverage floor against the broadest published tool surfaces among surveyed competitors (§03/§13); run against the agent using only the four MCP commands.
5. **Signed distribution.** Code-signing certificate, `signtool` build step, installer, GitHub Releases distribution — closes the "untrusted publisher" friction ahead of any wider testing.
6. **Multi-version + Marketplace.** *(2025 done)* Added the `net8.0-windows` TFM for Revit 2025 alongside 2027's `net10.0-windows`, cross-built by the single installed .NET 10 SDK (no second SDK — it restores the net8.0 targeting pack via NuGet), plus discovery-API multi-version disambiguation: an unscoped `list_functions`/`search_functions`/`describe_function` call errors (`ambiguous_instance_version`, with candidates) when connected instances span more than one Revit version instead of silently picking one, and every discovery response now carries which `revit_version` it reflects (§08). Live-validated against real, simultaneously-connected 2025+2027 instances. 2026 TFM and per-version manifests remain open — 2026 is genuinely unverified (not installed on the dev VM); don't assume it matches 2025 just because they're adjacent releases (§11). Marketplace submission is attempted but not required for success at this phase (§14) — independent signed distribution continues either way; if `execute_script` doesn't clear review, submit a restricted read-mostly tool subset instead, matching the pattern every other marketplace AI-agent connector already follows.
7. **Allowlist-based dialog auto-dismiss.** Once field usage from phases 03–06 has surfaced the recurring non-framework dialogs (§07), extend the window-inventory fallback to auto-dismiss known signatures via `WM_CLOSE`/default-button simulation.

---

*Synthesized from Revit API/threading research, a survey of six-plus open-source Revit-MCP projects, MCP transport/multi-instance precedent (Blender MCP, Unity MCP, Chrome DevTools Protocol), and Parallels networking behavior.*
