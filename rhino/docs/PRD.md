# Rhino Connector

**Product & Technical Design — Draft**

Two components — the Rhino MCP Bridge, a Rhino plug-in that executes agent-authored Python or C# against live documents and Grasshopper definitions, and the Rhino MCP Server, which speaks MCP to Claude and other agents over a local, structured protocol — starting with Rhino 8 on macOS and Windows.

- **Scope:** v1 = Rhino 8, macOS and Windows, one plug-in build for both
- **Trust model:** fully trusted, unsandboxed, same-user-local
- **Transport:** TCP JSON-RPC 2.0, NDJSON framing, loopback only; the server dials in to each Rhino instance
- **Script languages:** Python 3 first, C# as an equal peer, selected per call
- **MCP Server:** Go, single-binary distribution, shared code with the Revit MCP Server
- **Status:** design draft, 2026-09-09; phase 1a spikes run 2026-09-10 (`spikes/phase-1a-findings.md`) and folded in below; phase 0 in PR #279; nothing else built

> This document is the single source of truth for the Rhino connector. It is written against the Revit connector's PRD (`revit/docs/PRD.md`) and `CONVENTIONS.md`: where a section says "as Revit", the Revit design applies unchanged and is not restated; where this connector departs, the section says why. Facts about Rhino that were verified against a live install or McNeel's own statements are marked **verified**; facts still resting on documentation or reasoning are marked **to verify** and are listed again in §17.

## Contents

1. [Summary & goals](#01-summary--goals)
2. [Non-goals for v1](#02-non-goals-for-v1)
3. [Competitive landscape](#03-competitive-landscape)
4. [Architecture](#04-architecture)
5. [Connection & multiplexing model](#05-connection--multiplexing-model)
6. [Threading & script execution](#06-threading--script-execution)
7. [Undo, rollback & the confirmation gate](#07-undo-rollback--the-confirmation-gate)
8. [Blocking prompts & dialogs](#08-blocking-prompts--dialogs)
9. [API discovery & the how-to corpus](#09-api-discovery--the-how-to-corpus)
10. [Grasshopper](#10-grasshopper)
11. [Viewport capture](#11-viewport-capture)
12. [File exchange & document identity](#12-file-exchange--document-identity)
13. [Security model](#13-security-model)
14. [Multi-version strategy](#14-multi-version-strategy)
15. [Signing & distribution](#15-signing--distribution)
16. [Validation & test corpus](#16-validation--test-corpus)
17. [Open questions & things to verify](#17-open-questions--things-to-verify)
18. [Phased roadmap](#18-phased-roadmap)

---

## 01. Summary & goals

The Revit connector's bet — one `execute_script` tool plus API discovery, instead of a fixed catalog of pre-built tools — is carried over unchanged, and the Rhino landscape (§03) validates it the same way Revit's did: every existing Rhino MCP project ships a fixed catalog, treats script execution as a secondary escape hatch if at all, and none does API discovery or addresses more than one Rhino instance.

Rhino adds two things Revit did not have. **A scripting ecosystem the agent already knows**: Rhino's users write Python against `rhinoscriptsyntax` and RhinoCommon, that corpus is what an agent has read, and Rhino 8 hosts CPython 3 in-process — so the primary script language is Python, with C# as an equal peer for the cases where RhinoCommon's typing matters. **Grasshopper**: a dataflow environment beside the document, with its own document model, solver and UI controls, that agents need to edit, run, drive and observe (§10).

> **Guiding principle — fully autonomous project-lifecycle control, done correctly, efficiently, and safely.** As Revit §01, including the "observability over silence" rule and the shared diagnostic-record shape (`severity`, `code`, `source`, `message`, `detail`, `remedy`; kebab-case codes; `source` values matching the module layout, `mcp-bridge.core.execution` and so on). Every `notices[]` entry, every error, every audit log line uses that one record. Not restated here.

Goals for v1:

- Run agent-authored Python 3 or C# against an open Rhino 8 document on macOS or Windows without crashing the host, with one Undo entry per run and rollback on failure where Rhino can provide it (§07).
- Let one agent reach every running Rhino instance and every open document, addressed explicitly, and let several agents do so at once (§05).
- Never let a script hang the instance on a prompt or dialog nobody will answer — or, where that cannot be prevented, report exactly what is on screen (§08).
- Give the agent a self-serve way to learn RhinoCommon, `rhinoscriptsyntax` and Grasshopper's API, in both languages' call shapes (§09).
- Make Grasshopper definitions editable, runnable, drivable through their UI controls, and observable per component (§10).
- Keep generated files in one predictable per-document location on disk (§12).

## 02. Non-goals for v1

- **Not Rhino 7.** Rhino 7 is .NET Framework only and has no CPython; nothing here targets it. Rhino 9 (in beta on .NET 10 as of July 2026 — **verified**, McNeel forum) is structured for, not targeted (§14).
- **Not a general sandbox.** As Revit §02: full API access, one narrow denylist of things that escape the undo boundary (§07), reflection can route around it and that is accepted.
- **Not a fixed-tool Grasshopper product.** v1 makes Grasshopper scriptable, discoverable and observable through a small helper surface; a fixed tool set over it is a later phase (§10, §18).
- **Not Rhino.Compute.** Headless geometry over HTTP is a different product with no document, no UI and no Grasshopper canvas; nothing here depends on or competes with it.
- **Not remote mode.** Rhino runs natively on the Mac, so the Mac + Parallels topology that forced Revit's remote mode does not exist. There is one mode, local (§05, §13).
- **Not cross-agent locking.** Several agents may drive one document; v1 reports who ran last and enforces nothing (§05).

## 03. Competitive landscape

Surveyed 2026-09-09. No official McNeel MCP server exists; all entrants are community projects.

| Project | Rhino side | Grasshopper side | Script exec | Multi-instance | API discovery |
|---|---|---|---|---|---|
| jingcheng-chen/rhinomcp (~1,000 stars, on the Rhino package manager, Win + Mac) | C# plug-in, TCP listener on a fixed port 1999, runs on the main thread | Full fixed catalog: open/create a definition, search, place, wire, solve | `run_command`, IronPython via `Rhino.Runtime.PythonScript`, C# via Roslyn | "Run only one server at a time" | none |
| 4kk11/RhinoMCPServer | C# plug-in speaking Streamable HTTP directly, MCP SDK in an isolated load context | 13 fixed tools: canvas read, create/delete, wire, sliders, panels, load/save, solution status | none | one server, many clients, fixed port | none |
| SerjoschDuering/rhino-mcp | Python socket server inside Rhino, port 9876 | HTTP server *inside a `.gh`*, Python in GH, canvas inspection | Python only | unaddressed | none |
| alfredatnycu/grasshopper-mcp | none | a `.gha` with a TCP server, Python bridge on 8080 | undocumented | unaddressed | none |
| dongwoosuk, veoery, sholtomaud, mustafaisildakcom, reer-ide, always-tinkering | variants and forks of the above | varying | varying | unaddressed | none |
| **This connector** | **execute_script (Python 3 + C#) as primary; server dials in** | **scriptable + discoverable + observable; fixed tools later** | **primary interface** | **explicit `{instance_id, document_id}`; N agents × M instances** | **list/search/describe over RhinoCommon + Grasshopper, both call shapes** |

Three things the survey settles. The fixed-catalog pattern is universal, so the differentiation holds. Grasshopper demand is real — the most-starred project treats it as first-class and two projects exist for Grasshopper alone — and every project converges on the same dozen Grasshopper operations, so that surface is bounded (§10). And nobody has addressed the hard parts: the solver on the UI thread, a definition that recomputes for seconds, a script blocked on a command-line prompt, or two Rhino instances. Those are exactly the classes of problem the Revit connector already solved once, and this design reuses the solutions.

## 04. Architecture

Three tiers, as Revit §04: the **Rhino MCP Bridge** (the plug-in) never speaks MCP — it is a TCP JSON-RPC executor inside the Rhino process. The **Rhino MCP Server** — "the broker" as engineering shorthand, per `CONVENTIONS.md` — runs wherever Claude runs, speaks MCP over stdio, and reaches every live Rhino instance. The one structural difference from Revit is the direction of the TCP connection, which §05 explains.

```
Claude session A  <--stdio-->  Rhino MCP Server A  --TCP-->  Rhino MCP Bridge (Rhino instance 1, listening on 127.0.0.1:<ephemeral>)
                                                   --TCP-->  Rhino MCP Bridge (Rhino instance 2, listening on 127.0.0.1:<ephemeral>)
Claude session B  <--stdio-->  Rhino MCP Server B  --TCP-->  (the same two instances)
                                                              each instance publishes instances/<pid>.json
```

MCP tools exposed to the agent, unchanged in name and shape from Revit wherever the concept carries over: `execute_script`, `poll_execution`, `cancel_execution`, `undo`, `redo`, `list_functions`, `search_functions`, `describe_function`, `search_howtos`, `describe_howto`, `submit_howto`, `get_skills`, `list_instances`, `update_connector`, plus one new tool, `capture_view` (§11). New parameters and fields are called out in the sections that introduce them (`language` in §06, the Grasshopper solve report in §10).

**Naming** follows `CONVENTIONS.md` exactly: "MCP Bridge" and "MCP Server" inside this directory and in Rhino's own UI, "Rhino MCP Bridge" and "Rhino MCP Server" elsewhere, the client registration slug `"rhino"`, app data under `Connectors/Rhino/`, and the connector's own script API under `Eichler.Connectors.Rhino` with a single public `Connector` type in its own assembly. Repo layout mirrors Revit's: `rhino/mcp-bridge/` (C#), `rhino/mcp-server/` (Go), `rhino/test-harness/`, `rhino/docs/`.

**The plug-in stays thin**, as Revit's does: a TCP listener, two script hosts (§06), a `RhinoDoc` event subscriber for the mutation report and document snapshots, a Grasshopper solution-event subscriber (§10), and live reflection over the loaded assemblies for discovery. One `MCPBridge` command family in Rhino's command line replaces Revit's ribbon panel: `MCPBridgeStatus` (a non-modal Eto panel showing connection state, build identity and update availability) and `MCPBridgeReconnect`. Rhino has no ribbon on the Mac and Eto is the one UI toolkit that renders on both platforms.

**The server is the Revit MCP Server's code, not a copy of it.** The Revit server's `internal/` packages are already app-agnostic in the places that matter — `transport`, `diag`, `registry`, `semsearch` and its manager, `howto`, `howtosearch`, `updatecheck`, `selfcheck`, `buildinfo` — and the Rhino server imports them from the shared module. What differs (connection direction, the `language` parameter, the Grasshopper report, the absence of a singleton) lives in Rhino-specific packages. Extracting the shared packages to a repo-level `internal/` is the first implementation step (§17 phase 0), and it is done before any Rhino-specific code so the Revit server keeps building throughout.

## 05. Connection & multiplexing model

**Each Rhino MCP Server dials in to every Rhino instance.** This inverts Revit §05, deliberately, and the reason is worth stating in full because it removes the most complex subsystem the Revit server has.

Revit's add-in dials *out* to one well-known listener. That was chosen for two reasons: "one address, many instances" so agents never discover ports, and the Mac + Parallels dev topology, where a listener inside the VM would have needed inbound reachability from the Mac. The consequence was that MCP clients — which spawn one stdio server per session — could produce several would-be listeners, and one had to win: the singleton lock-or-proxy design, then lock generations and an election mutex for dead lock holders (issue #212), stale-image self-eviction (#201), tool-schema skew between a primary and a newer secondary (#197), and a session-continuity replay so a promoted secondary keeps its client's session. That machinery works, and most of the Revit connector's live-debugging history is in it.

Rhino runs natively on the Mac, so the second reason is gone, and the first is a directory scan. With the plug-in listening and the server dialling:

- **The plug-in binds `127.0.0.1` on an ephemeral port at plug-in load** and writes `<app-data>/Connectors/Rhino/instances/<pid>.json`: `port`, `pid`, `instance_id` (a GUID minted once per Rhino process), `rhino_version`, `platform`, `bridge_version`, `schema_fingerprint`, `started_at`, and a per-instance auth `token` (§13). It deletes the file on clean unload. A server treats a file whose `pid` is not a live process as stale and deletes it, so a crashed Rhino leaves nothing behind that survives a scan.
- **Every server process is independent.** On launch it scans the directory, dials every live instance, authenticates, and keeps dialling on a backoff for instances that appear later or drop. No lock, no primary, no proxying, no continuity replay, no eviction. A newer server binary is simply what the client spawns next. Two Claude sessions are two servers with two connections into each plug-in; from the agent's point of view they are indistinguishable, which is the same promise Revit's design made through much more mechanism.
- **Busy state lives where the truth is.** The plug-in serialises script execution on the UI thread, so it — not the server — owns the per-instance `idle`/`pending`/`running`/`busy`/`unrecoverable` state and the ring buffer of recent results. Every connected server sees the same answer, and `poll_execution` for any `execution_id` works from any server, because the plug-in owns the record. This removes the broker-side busy latch that produced Revit's reconcile-before-answering hack (issue #54) and its half-open-socket failure (#269), and it makes "poll survives a server restart" true by construction rather than the unfulfilled promise Revit's PRD had to retract.
- **`execution_id` is still minted by the server** (Revit §01), namespaced by a per-server-process prefix so two servers can never collide, and echoed by the plug-in.
- **What is kept from Revit unchanged:** the `register` message (now the plug-in's answer to a server's `hello`, carrying the live document list, re-sent on every `DocumentOpened`/`Closed`/`Activated`); the heartbeat ping from the plug-in with a memory sample; `unresponsive` after missed pings versus `unrecoverable` after a lapsed cancellation grace; the registry's epoch guards; the "every retained buffer states its bound" rule.

**What this design gives up, and what replaces it.**

- *One shared search index per machine.* Each server would build its own `search_functions` index on attach (Revit measured ~1.4 s for a 76k-member corpus plus a ~24 MB model cache). Replacement: the built index is persisted under the server's private app-data root keyed by corpus fingerprint — the manager already computes that fingerprint to share indexes between instances — so the second server on a machine loads it instead of rebuilding. Until that lands, servers build independently; the cost is seconds and memory, not correctness.
- *A global view for cross-agent coordination.* Neither design actually prevents two agents from editing one document at cross purposes; the singleton only looked like it helped. v1 is observability only: every execution result and every `list_instances` document entry carries `last_run` (`agent_client_id`, a per-server-process id the client can name; `execution_id`; `finished_at`), so an agent that sees another client's run since its own last call can decide what to do. An advisory or exclusive per-document lease is a plug-in-side feature that fits this design without a wire change, deferred until real use shows the collision (§17).

**Platform note — the document model differs by OS, and addressing is designed for the harder case.** Rhino for Windows opens one document per process; a second file is a second Rhino instance (**verified**, McNeel forum, unchanged through Rhino 7 and, per the same source's silence, to be re-checked on 8). Rhino for Mac opens many documents in one process, one window each. `{instance_id, document_id}` is designed for the Mac case; on Windows every instance has exactly one document and `document_id` may be omitted. As `CONVENTIONS.md` requires, an omitted `document_id` is the active document and a non-matching one is a loud `document-not-found` with candidates, never a silent fallback, on both platforms from the first build.

### Instance discovery — `list_instances`

As Revit §05's table, with `platform` (`macos`/`windows`) and `bridge_version` added per instance, `last_run` added per document, and `documents[]` entries carrying `document_id`, `title`, `path` (or `unsaved`), `active`, and `grasshopper_documents[]` (§10) — the open definitions, each with its own id, so an agent can address a definition without first finding it through the canvas.

## 06. Threading & script execution

RhinoCommon must be called on Rhino's main thread (**verified**: the CPython host itself does *not* marshal — called from a background thread it ran the script on that thread and let it touch the document — so the connector marshals every run itself). The TCP listener and both script hosts' compilation run off-thread. **Every run executes inside a connector-owned command** (`MCPBridgeRun`, `CommandStyle(ScriptRunner)`), started from the connection thread with `RhinoApp.InvokeOnUiThread(() => RhinoApp.ExecuteCommand(doc, "MCPBridgeRun"))` — **verified** as the one mechanism that both runs a plug-in's command from a plug-in thread and gives the run a single undo entry (§07); `RhinoApp.RunScript` from outside a command context silently does nothing, and keystroke injection repeats the last command. The plug-in reports `pending` until the command actually starts and `running` once it has, exactly as Revit §06 distinguishes them, because the main thread may be inside a command, a modal dialog, or a Grasshopper solve.

`execute_script` takes `instance_id`, `document_id`, `language` (`"python"` | `"csharp"`, required — no default, so a wrong-language script fails at the schema rather than at the first line), and exactly one of `script` / `script_path`; plus `timeout_ms`, `max_duration_ms`, `overwrite_output_files`, `confirm_lifecycle_actions`, and `label`, all as Revit. The result shape is Revit's: `status`, `execution_id`, `output`, `return_value`, `notices[]`, `files[]`, `mutations`, plus `grasshopper` (§10) when a solve ran. `max_duration_ms` has one owner, the plug-in, which starts the clock at `running`; the server's timer is a backstop that fires only after the plug-in's deadline has passed with no terminal state — the decision Revit's issue #270 asks for, made here from the start.

### Two hosts, one contract

| | Python 3 | C# |
|---|---|---|
| Host | Rhino 8's CPython 3 (3.9.10 in 8.35 — **verified**), driven through `Rhino.Runtime.Code.RhinoCode.RunScript(code, RunContext)` with the `#! python 3` shebang; the language is loaded lazily and the plug-in warms it at load with `RhinoCode.Languages.WaitStatusComplete(LanguageSpec.Python3)` (**verified**: ~1.75 s once, then 20–45 ms per run; without it every run fails with a misleading `CodeLanguageNotFoundException`) | Roslyn `CSharpScript`, ported from Revit's `RoslynScriptRunner`: per-run collectible `AssemblyLoadContext`, bounded LRU of compiled scripts, own Roslyn isolated from other plug-ins |
| Globals | `doc` (`RhinoDoc`), `ghdoc` (the addressed Grasshopper document or `None`), `connector`, `cancel` (a token the script polls); `rhinoscriptsyntax` importable as `rs`, `scriptcontext` bound to `doc` | `Document`, `GrasshopperDocument`, `Connector`, `CancellationToken`; the real RhinoCommon types |
| stdout | `RunContext.OutputStream`/`ErrorStream` (**verified**: `print` and the traceback land there; a Python exception arrives as `ExecuteException` carrying the Python message), plus `RhinoApp.CommandWindowCaptureEnabled` for lines Rhino itself prints | AsyncLocal console capture as Revit, plus the same command-window capture |
| Return value | The value bound to `result` at script end, via `RunContext.Outputs`; formatted as Revit's `return_value` | The script's returned value, formatted as Revit's `return_value` |
| Compile-time guard (§07) | A token walk over the script text with import-alias resolution (there is no type checker for Python; the walk resolves `import … as`, `from … import`, star imports, the `doc`/`scriptcontext.doc`/`RhinoDoc.ActiveDoc` idioms, `getattr` string literals, f-string interpolations and literal command strings; a value copied into another variable first is not seen, and the dynamic-code builtins are refused for that reason) | Revit's `ScriptApiDenylist` semantic walk, retargeted |
| Cancellation | Cooperative only: `cancel.Check()` in loops (raises when cancelled; `cancel.IsRequested` is the non-raising form). **Verified** that `Rhino.Runtime.Code` exposes no interrupt for a running script, so a non-cooperating Python script resolves to `unrecoverable` after the grace period, as a non-cooperating C# one does | Cooperative `CancellationToken` as Revit |

**Two facts about the Python host carry risk, and both are named rather than assumed.** First, `Rhino.Runtime.Code` is the API McNeel's own staff describe as undocumented and "still being matured", with no stability guarantee, and the `RhinoCodePlatform.*` assemblies behind it are explicitly internal (**verified**, McNeel forum, September 2024). The older public `Rhino.Runtime.PythonScript.Create()` is IronPython 2, not CPython, and is what the most popular incumbent actually runs — so its "Python" is Python 2. This connector uses `Rhino.Runtime.Code` for real CPython 3 and pins the Rhino service release it was verified against; a service-release break is the same class of risk as Revit's `RevitAPI.dll` version pin and is handled the same way, by the live harness (§16) and the how-to sweep. Second, that host cannot interrupt a script that ignores `cancel` (**verified**, no such API), so a non-cooperating Python script resolves to `unrecoverable` after the grace period exactly as a non-cooperating C# one does (Revit §06), and `skill.md` says so.

**The connector's own API is one type, `Eichler.Connectors.Rhino.Connector`, reached as `connector` in Python and `Connector` in C#** — the same object, bound into both hosts, so its members are documented once and discovered once (§09). v1 members: `ImportsDirectory`, `ExportsDirectory`, `Publish(path, name?)`, `Grasshopper` (§10), `CaptureView(...)` (§11), `DialogResultOverrides` (§08), `Settle(doc, keep)` (§07). No `WithTransaction` — Rhino has no transactions, and §07 explains what replaces the block.

**Rhino's own script surface stays reachable.** `RhinoApp.RunScript(...)` runs any Rhino command, including ones that prompt (§08) and ones that save or quit (§07); it is not blocked, it is gated where its effects escape undo. Scripts must not be marked with Rhino's `ScriptRunner` command style themselves — the plug-in's executor command carries that attribute (**verified**: `RunScript` from a plug-in silently fails without it), which is one of the reasons scripts run inside a connector-owned command rather than a bare `InvokeOnUiThread`.

## 07. Undo, rollback & the confirmation gate

Rhino has no transactions. Its document is always modifiable, and its unit of undo is the **command**: everything a command changes is one entry in the Undo stack, named after the command, and the `_Undo` command reverts the top entry. Three facts about that model were **verified** live and shape everything below (`spikes/phase-1a-findings.md` §3): undo is *deferred to the end of the current command* — `RhinoDoc.Undo()` and `_Undo` inside a running command return true and revert nothing, and a mid-command `Undo()` then discards the entry without reverting; `BeginUndoRecord` inside a command adds no separately named entry and nested records are refused; and `RhinoApp.ExecuteCommand(doc, "_Undo")` issued from the plug-in's thread *after* a command has returned reverts exactly that command's entry, with `_Redo` restoring it. That is enough to keep Revit's two user-facing guarantees, with one honest weakening:

- **One run, one Undo entry — by construction.** §06 runs every script inside one connector-owned command, so a run is one entry. A run that changed nothing leaves no entry (**verified** for an empty record). **The entry is named `MCPBridgeRun`, always** (**verified** in phase 1 PR 2's harness: an inner `BeginUndoRecord(label)` inside the command does not rename it; the history reads `Undoing MCPBridgeRun`). Revit's `MCP: <label>` naming is therefore not offered; the caller's `label` is still carried on the result and exposed to the script as `Connector.RunLabel`, and the `undo`/`redo` tools report the label of what they reverted from the plug-in's own record, which is where a person-readable name is actually consulted.
- **The `undo`/`redo` tools are gated by evidence, not by blanket confirmation.** Revit's tools required `confirm` on every call because Revit's stack is not inspectable. Rhino's is not inspectable either, and `Command.LastCommandId` turned out to be no oracle (a read-only run of ours also leaves the run command as the last command — **verified** in PR 5's harness), so the plug-in keeps its own evidence: a clock over every document change RhinoCommon reports *outside* the connector's own commands (a person, another plug-in) and over the connector's completed runs and its own undo/redo operations, per document. An `undo` whose document has not changed outside the connector since the connector's last run that changed it acts on the connector's own work and runs without `confirm`, naming that run and its label (`undo-reverted-connector-work`); so does a `redo` right after the connector's own undo, and an `undo` right after its own redo. Anything else — a person's change since, a second undo whose entry below is unknown, a redo after a connector run emptied the redo stack — is refused with `undo-confirmation-required` naming the last command Rhino ran, and runs with `confirm: true` under `undo-reverted-other-work` (a warning). The reverted change is reported through the same mutation report as a run. `undo-nothing-to-undo`/`redo-nothing-to-redo` when Rhino has nothing (or refuses because another command is running). An undo is an execution for the busy gate: `busy` while a script runs, and scripts are `busy` while it runs. Two limits of the evidence, stated: changes to the run's own document made by a person while a script pumps the main thread (a dialog, `RhinoApp.Wait`) are counted as the connector's; and the clock sees the document events RhinoCommon raises (objects, attributes, and the layer, linetype, hatch, material, render-content, group, block, dimension-style, light, user-string tables, document properties) — a change that raises none of them is invisible to it.
- **A failed run is undone.** If the script throws, is cancelled, or exceeds `max_duration_ms`, the command returns a failure and the executor immediately issues `ExecuteCommand(doc, "_Undo")` — never `RhinoDoc.Undo()` from inside the run — then reports what it reverted in `notices[]` (`script-rolled-back`, with the mutation counts). **Verified** for a Python script that added objects and then raised: the objects stayed until the undo, and the undo removed exactly them. This is weaker than Revit's group rollback in two ways that `skill.md` states plainly: Rhino replays the entry rather than discarding an uncommitted one, so a change that is not undoable (a `RunScript` command with side effects outside the document, a file written by the script) survives; and if the user acted between the command's end and the undo — a window of one main-thread hop, but not zero — the undo would revert their action. The executor therefore checks that its own command is the most recent command Rhino ran before issuing the undo and reports `script-rollback-skipped` if it is not, rather than reverting a person's work.
- **`Connector.Settle(doc, keep)`** ends the run's command early so `Save`/`SaveAs`/`Close` can follow in the same run, as Revit. Mechanism (a nested command, or splitting the run into two commands) and whether Rhino permits a save mid-command are **to verify** in phase 1.

**The mutation report** (`mutations`: `net_added`, `net_modified`, `net_deleted`, `by_object_type`, `by_layer`) comes from `RhinoDoc.AddRhinoObject`, `ReplaceRhinoObject`, `DeleteRhinoObject` and `UndeleteRhinoObject` events subscribed for the run's duration, netted by object id — the same shape Revit derives from `DocumentChanged`.

**The denylist and the confirmation gate** apply Revit §14's one test — *would the rollback above actually undo this?* — to Rhino's API:

- **Hard-blocked, no opt-in:** `RhinoDoc.BeginUndoRecord`/`EndUndoRecord`/`ClearUndoRecords`/`Undo`/`Redo` from a script (**verified** that a mid-run `Undo()` destroys the run's own entry without reverting it, and that nested records are refused), `RhinoApp.Exit`, `RhinoApp.RunScript` with a command token matching `_Exit`/`_Quit`, and — Rhino-specific — the interactive getters (§08). Refused at analysis time, `script-api-denied`.
- **Confirmation-gated** (`confirm_lifecycle_actions: true`): `RhinoDoc.Save`/`SaveAs`/`Export`/`Write3dmFile`/`Close`, `RhinoDoc.Open` and `RhinoDoc.New` (they replace or add a document the person can see), `RhinoApp.RunScript` with `_Save*`/`_Export*`/`_Open`/`_New`/`_Close`/`_Print`-class command tokens, `GH_DocumentIO.Save*` and `GH_Document.Close` (§10), and `Connector.Settle`. Refused with `script-lifecycle-confirmation-required` naming each member; the identical call resent with the flag runs.

Detection is a token walk with alias resolution for Python (§06) and Roslyn's bound-symbol walk for C#, both keyed on *(type, member)* or *(command token)*, never bare names. `RunScript`'s argument is a string, so the gate for command tokens is a static check on literal arguments (every string literal inside the call, so `str("_Exit")` and concatenated fragments are seen too). A computed command string (`rs.Command(cmd)`) is not gated: `rhinoscriptsyntax.Command` and `RhinoApp.RunScript` call Rhino directly and never pass through the connector, so no runtime wrapper can sit in front of them without replacing those functions; the static walk is what there is, and `skill.md` says so. The Revit rule that `public` means script-reachable in the bridge's assemblies applies in full, in both languages: CPython reaches every public .NET type through interop exactly as Roslyn does.

## 08. Blocking prompts & dialogs

Rhino's dialog problem is shaped differently from Revit's, and the bigger half of it is prevention rather than suppression.

**Command-line prompts are the common case.** `Rhino.Input.RhinoGet.*`, `GetObject`, `GetPoint`, `GetString` and the `rhinoscriptsyntax` wrappers over them (`rs.GetObject`, `rs.GetPoint`, ...) block the main thread waiting for the person to click or type. Every tutorial script starts with one. They are in the hard-blocked denylist (§07): a script that calls one is refused at analysis time with a remedy that says how to select or supply the input programmatically instead (`doc.Objects.FindByLayer`, an explicit point, a `document_id`-scoped object id). This is a prevention rule with no Revit equivalent, and it is the single most important line in `skill.md`.

**Modal dialogs.** Rhino raises them through Eto on both platforms and through native Win32 boxes on Windows. There is no `DialogBoxShowing`-style pre-show event in RhinoCommon (**to verify** — `RhinoApp.CommandWindowCaptureEnabled` captures text, not dialogs). So v1 has no framework-level suppression; it has the Revit §07 v1 fallback from the start: on an `execute_script` timeout that leaves the run non-terminal, the plug-in enumerates the process's owned top-level windows off the main thread (Win32 `EnumWindows` on Windows; Core Graphics `CGWindowListCopyWindowInfo` on macOS — **not** `NSApplication.windows`, which is AppKit main-thread-only and would hang on the very blocked thread being diagnosed) and, when a candidate window is present — visible, not Rhino's own main frame, and with a non-empty title — attaches its title and class as diagnostic data (`window-inventory-timeout-fallback`, source `dialogs`), taking no action. It stays silent when only Rhino's own windows are open, so a long compute that is not actually blocked is never flagged. **v1 limitation (2026-09-11):** the non-empty-title requirement means a genuinely titleless modal is not flagged — the deliberate trade to keep the notice high-signal (a live smoke found an idle Rhino owns a visible, titleless WPF infrastructure window that would otherwise be a false positive); the timeout itself still surfaces the stuck run. `Connector.DialogResultOverrides` exists so that when a suppressible hook is found it has a place to land; until then it is documented as no-op.

**`pending` is the prompt's symptom.** A script queued behind a prompt or dialog already on screen reports `pending` and never runs; a second call returns `busy`. `skill.md` tells the agent to ask the person to look at Rhino's command line, not to retry.

**Grasshopper's own modal moments** — a component whose `AppendAdditionalMenuItems` opens a picker, the "file not found" prompt on a `.gh` that references a missing library — sit inside a solve on the main thread and surface the same way. §10 adds the per-solve report so the agent can at least see which component the solve stopped at.

## 09. API discovery & the how-to corpus

As Revit §08 in mechanism: the plug-in reflects every assembly loaded in the Rhino process into a persistent SQLite/FTS5 cache under `Connectors/Rhino/<rhino-version>/discovery-cache.db`, pages the documented corpus to the server as `dump_members`, and the server builds the BM25F + static-embedding + cross-encoder index and serves `list_functions`/`search_functions`/`describe_function`. Three departures:

- **Two corpora ranked as one.** RhinoCommon (`RhinoCommon.dll`), Grasshopper (`Grasshopper.dll`, `GH_IO.dll`), every loaded plug-in, and `Eichler.Connectors.Rhino` are indexed as `kind = core | grasshopper | addin`; kind is a tie-break and a browse ordering, never a rank (Revit's issue #97 correction applied from the start). `rhinoscriptsyntax` is a Python library, not a .NET assembly; its functions are indexed from its docstrings as a fourth kind, `rhinoscript`, because it is the layer most Python examples use and an agent searching "add a circle" should find `rs.AddCircle` beside `Rhino.Geometry.Circle`.
- **Every member is described in both call shapes.** `describe_function` returns the C# signature and the Python call form (tuple returns for `out` parameters, explicit generic arguments, `rs.` wrapper where one exists), so a member found once is usable from either language.
- **XML docs ship inside Rhino on macOS** (**verified**: `RhinoCommon.xml`, `Grasshopper.xml` and `GH_IO.xml` are in the app bundle), so the plug-in reads them beside the assemblies exactly as Revit's does. Windows is confirmed in phase 2; if it differs, the server embeds the sidecar per supported version as it already embeds the ranking models.

**The how-to corpus** (Revit §08 "How-to corpus") is reused whole: the schema, `verified.jsonl` sidecar keyed by document id × script hash × Rhino version × platform × language, `search_howtos`/`describe_howto`/`submit_howto`, the scrub-and-queue flow, `TestHowToSweep` and `TestHowToEndToEnd`. `language` and `platform` join `rhino_version` as stamp dimensions, because a script verified in Python on macOS says nothing about C# on Windows. The seed is extracted from the harness cases as Revit's was, and `get_skills` serves a Rhino `skill.md` written to the same token budget discipline.

## 10. Grasshopper

Grasshopper is in scope for v1 at the level agreed on 2026-09-09: **scriptable, discoverable, drivable and observable**, with a small helper surface and a solve-aware execution lifecycle; **fixed Grasshopper tools are a later phase** (§18), added once how-to submissions show which operations recur. Every incumbent's Grasshopper catalog is reachable from a script today through `Grasshopper.Kernel`; what the connector adds is the parts a script cannot do for itself.

### Addressing

`list_instances` reports each instance's open definitions as `grasshopper_documents[]` (`gh_document_id`, `title`, `path` or `unsaved`, `active`, `enabled`, `component_count`). `execute_script` takes an optional `gh_document_id`; when given, the script's `ghdoc`/`GrasshopperDocument` global is that `GH_Document` and the solve report below covers it. Identity follows §12's rules applied to the `.gh`/`.ghx` path.

### What a script does directly

| Need | Grasshopper API | Note |
|---|---|---|
| Edit a definition | `GH_Document.AddObject`/`RemoveObject`, `Instances.ComponentServer` to instantiate by name or GUID, `IGH_Param.AddSource`/`RemoveSource`, `Attributes.Pivot`, `GH_DocumentIO` load/save | The incumbents' entire catalog is these calls |
| Run a definition | `GH_DocumentIO` + `GH_DocumentServer.AddDocument`, `GH_Document.NewSolution(expireAll)` | Synchronous on the main thread; a slow solve is a long script and reports `running` |
| Drive UI controls | `GH_NumberSlider.SetSliderValue`, `GH_BooleanToggle.Value`, `GH_Panel.SetUserText`, `GH_ValueList.SelectItem`, button press, then `ExpireSolution` | Type-specific; the helper below unifies them |
| Read inputs and outputs | `IGH_Param.VolatileData` (the data tree after a solve), `PersistentData` | Needs a bounded serializer (below) |
| Pause and step | `GH_Document.Enabled = false`, `Locked` on components, `ExpireSolution` on one object with the solver on | No native step; these idioms are taught in `skill.md` |
| Observe | `IGH_ActiveObject.RuntimeMessages(level)`, `Phase`, `ProcessorTime`; `GH_Document.SolutionState`, `SolutionDepth`, `SolutionStart`/`SolutionEnd` | Coarse mid-solve, full after |

### What the connector adds — `Connector.Grasshopper`

- `Find(nickname | guid)` → component; `Set(nickname, value)` sets a slider, toggle, panel, value list or button by nickname, expiring the object; `Get(nickname)` returns the serialized outputs of one object.
- `Solve(gh_document, expire_all=False, timeout_ms=None)` runs a solution and returns the **solve report** (below) — the same report the execution result carries when a script triggers a solve by any other route.
- `Data(param)` serializes a data tree with bounds: paths, item counts, and per-item summaries (numbers and text verbatim; geometry as type + bounding box + a stable handle), never full geometry inline — the same 25,000-token discipline as discovery paging. `Publish` on a `.3dm`/`.gh` export is how full geometry leaves the process (§12).

### The solve report

For the run's duration the plug-in subscribes `SolutionStart`/`SolutionEnd` on the addressed definition (and on the active one if none is addressed). A run during which at least one solve ended carries `grasshopper` in its result: `solutions[]` (`started_at`, `duration_ms`, `state`, `depth`), and `components[]` for every object that reported a runtime message or ended in a phase other than `Computed` (`guid`, `nickname`, `type`, `phase`, `processor_ms`, `messages[]` as diagnostic records with `severity` mapped from GH's error/warning/remark). Errors are **not** auto-resolved — a component in `Failed` phase is reported, and whether that fails the run is the script's call, since a definition with one red component is often the intended state.

**Mid-solve progress is a named limitation.** The events fire at solution start and end; per-component phase changes during a solve are readable only by polling `Phase` from off the main thread, which RhinoCommon does not promise is safe. v1 reports which solve is running and for how long while `running`, and the full per-component report when it ends. Finer progress is **to verify** live before it is promised.

**Threading.** The solver runs on the main thread inside `NewSolution`, so it is serialised with scripts by construction and cannot interleave with one. A solve the *person* triggers while a script is `pending` delays the script exactly as a command would.

### Plug-in management — Yak-backed package tools

Real Grasshopper work leans on installed third-party plug-ins, so the connector lets an agent find and manage them through Rhino's own first-party package manager, **yak** (§15). Four server-side MCP tools shell out to the bundled `yak` CLI (`/Applications/Rhino 8.app/Contents/Resources/bin/yak`; `…\Rhino 8\System\Yak.exe` on Windows — override with `RHINO_YAK_PATH`), which operates on the per-user package folder (`~/Library/Application Support/McNeel/Rhinoceros/packages/8.0/`) independent of any running Rhino, so these tools need no `instance_id`:

- `search_packages(query, prerelease?)` and `list_packages()` are **read-only** — search the public package server (`yak.rhino3d.com`, anonymous) and list what is installed with its package directory.
- `install_package(name, version?, confirm_lifecycle_actions)` and `uninstall_package(name, confirm_lifecycle_actions)` are **gated** the same way `execute_script`'s lifecycle actions are: without `confirm_lifecycle_actions` they return a `preview` and change nothing; installing runs third-party code in the user's Rhino on its next start. Yak has no update command — updating is installing a newer version.

**A freshly installed plug-in is on disk but not loaded.** Rhino (and Grasshopper) load new packages only at startup, and there is no supported way to hot-load a `.gha` into a running Grasshopper, so every `install_package` result says a Rhino restart is required to use it. Putting a plug-in "into use" therefore pairs with a **gated restart** capability (a separate tool) that checks for unsaved documents before restarting and reopens the saved ones. The in-process `Yak.Core` API exists but is undocumented and unsupported, so the supported CLI is used instead; this keeps the bridge thin (the server, not the plug-in, does the work).

## 11. Viewport capture

An agent driving geometry cannot debug what it cannot see. The Revit connector reaches a PNG through a script plus `Publish`, which works but costs a round trip, a file the agent then has to open with its own tools, and a script the agent has to get right first. For Rhino this is a dedicated tool, `capture_view`, and it is one of the few fixed tools in the connector because it is a *connector mechanism* — getting pixels to the agent — not a Rhino capability an agent could discover.

**`capture_view(instance_id, document_id?, target, options?)`** returns the image **inline as MCP image content** (PNG, bounded — default 1280 px on the long edge, hard cap 2048, so a capture never approaches the client's output ceiling) *and* publishes the full-resolution file to the document's `exports/` (§12), reporting it in `files[]` *(the file half lands with file exchange, phase 5; PR 3 ships the inline half)*. `target` is one of:

- a Rhino viewport, by name (`Perspective`, `Top`, a named view) or `active`; options: `display_mode` (wireframe, shaded, rendered, …), `zoom` (`extents` | `selected` | none), `width`/`height`, `transparent_background`, `draw_grid`/`draw_axes`. *(Shipped in phase 1 PR 3. Two findings: a requested display mode has to go through `RhinoView.CaptureToBitmap(size, mode)` — setting the viewport's mode before a `ViewCapture` does not take effect for the shot — and that overload does not take the grid/axes/transparency flags, so those apply only when no mode is requested. `zoom: objects` and `isolate` — a temporary display conduit, never a document change — are deferred until a case needs them.)*
- a Grasshopper canvas, by `gh_document_id`; options: `zoom` (`extents` | `selected` | `components: [guids]`), `width`, `scale`;
- `all`: every viewport of the document in one call, returned as one image per viewport.

Captures run on the main thread through the same executor as scripts (`Rhino.Display.ViewCapture` for viewports, `GH_Canvas` image generation for the canvas) and so are serialised with them and report `busy` when a script is running. A capture changes nothing in the document; the temporary zoom and isolate are undone before the call returns and are reported in `notices[]` if restoring failed. The same capability is exposed to scripts as `Connector.CaptureView(...)`, returning the exported path, for the case where a script wants to capture at a specific step.

**Why inline and not only a path.** The agent's own filesystem tools can read the published file, and Claude Code renders a PNG it reads — but that is a second tool call, and the whole point is a one-call look. Inline content is bounded and cheap; the file is the full-resolution record. Both, deliberately.

## 12. File exchange & document identity

As Revit §09 with the remote-mode half deleted: the workspace tree is `~/RhinoMCPExchange/<document-id>/{imports,exports,logs,scripts,tmp/<instance-id>}` (`%USERPROFILE%\RhinoMCPExchange\` on Windows), `Connector.Publish` copies-or-registers into `exports/`, `files[]` carries per-file status, `overwrite_output_files` is request-level, `logs/`/`scripts/`/`tmp/` age out after 14 days via the audit trail's sweep, and the agent reads and writes the tree with its own filesystem tools — which always works, because there is one machine. No `read_file`, no path rewriting, no resources.

**Document identity** is Revit §09's table minus worksharing: a saved `.3dm` is `doc-<hash of case-normalised absolute path>`; an unsaved document is `tmp-<salt + title>` (Rhino auto-uniquifies unsaved titles as Revit does — **to verify**); a Grasshopper definition follows the same rule on its own path with a `gh-` prefix. Two Rhino instances with the same file open share a workspace, which `list_instances` shows; `tmp/<instance-id>/` keeps their scratch apart. Worksessions (`RhinoDoc.Worksession`, attached reference models) get no identity of their own, as Revit's linked models do not.

**Path resolution on macOS** uses the real path (symlinks and `/Volumes` mounts resolved) before hashing, the analogue of Revit's UNC resolution on Windows.

## 13. Security model

`execute_script` is full code execution inside the Rhino process, in two languages, by design. The transport defaults are therefore not optional:

- The plug-in binds `127.0.0.1` only, an ephemeral port, never `0.0.0.0` and never a configurable interface — there is no remote mode to need one.
- Each instance's `instances/<pid>.json` carries a random token minted at plug-in load; the file is written `0600` (and with an owner-only ACL on Windows). Every server presents it as the first message; anything else is rejected before any other message is read, with the same auth codes as Revit.
- **Honest limit, as Revit §10:** the token filters accidental cross-talk from unrelated software and adds no boundary inside the same-user trust model — a malicious same-user process can read the file. Unlike Revit, there is no remote mode where the token is the only protection, so this is the whole story.
- `script_path` reads a local absolute path or fetches an https URL on the server host, which is the same machine as Rhino — Revit's issue #272 does not arise.

## 14. Multi-version strategy

Rhino 8 runs .NET (Core) on both platforms — .NET 7 at release, with the RhinoCommon NuGet now targeting `net8.0` and rolling forward (**verified**, McNeel forum, July 2026) — and optionally .NET Framework 4.8 on Windows for compatibility, a mode this connector does not support (Roslyn scripting and the CPython host both assume the Core runtime). Rhino 9 is in beta on .NET 10 as of July 2026 with McNeel's migration guide saying to target `net10.0` (**verified**, same thread, after an initial staff answer said otherwise — a reminder that even vendor guidance moves).

v1 targets Rhino 8 with a single `net8.0` plug-in build for macOS and Windows (**verified on macOS**: Rhino 8.35 hosts .NET 8 with minor roll-forward, and a `net8.0` build against the bundled `RhinoCommon.dll` loads and registers commands; Windows is phase 2's first check, since the forum records plug-ins built for net7/net8 failing to load on one SR). The `.csproj` is multi-target from day one so a `net10.0` Rhino 9 build is additive. The plug-in's `rhino_version` in `instances/<pid>.json` and `list_instances` disambiguates discovery exactly as Revit's does (`ambiguous-instance-version` when instances span versions and no `instance_id` is given), and the how-to sidecar stamps per version.

## 15. Signing & distribution

Rhino has a first-party package manager, **yak**, with a public server McNeel runs and the `_PackageManager` command inside Rhino on both platforms; the most popular incumbent ships through it. A yak package is a zip with a manifest; it can carry the plug-in for both platforms and arbitrary files alongside. Rhino does not require Authenticode or notarization to load a plug-in, though macOS Gatekeeper does for the *server* binary launched by Claude.

Proposed: **one yak package carries the plug-in and both platforms' server binaries**; a Rhino command `MCPBridgeRegister` writes the MCP client registration (`claude mcp add` when the CLI is present, else the JSON snippet shown in the Status panel) pointing at the server binary the package installed. Updates are yak's own (`_PackageManager` shows them), the plug-in re-registers on load if the server path moved, and `update_connector` becomes a check plus a pointer at the package manager rather than an installer of its own — the shim/versioned-folder machinery Revit needed (issue #211) is unnecessary because yak already installs beside the running version and Rhino loads the new one at next start. The server binary's macOS notarization is the one signing cost, and it is deferred exactly as Revit's CA certificate is, with the "unidentified developer" prompt documented until then. `yak build` / `yak install <file>.yak` / `yak uninstall` are **verified** as the scriptable local install path on macOS (the Mac's `_-PlugInManager` has no load-from-path option, and Rhino only scans a package folder that carries yak's `manifest.txt` marker). Whether yak accepts a package with non-plug-in binaries at the size the server embeds (~70 MB with models) is still **to verify**; the fallback is Revit's install-script model with the models fetched on first run.

## 16. Validation & test corpus

As Revit §13 in structure — tier 1 unit tests behind the `Core`/adapter seam, tier 2 the live MCP harness against a real Rhino, no mocked middle tier — with the topology collapsed: **the harness runs natively on the Mac against a Mac Rhino**, no `prlctl`, no launcher agent, no shared-folder alias. Windows is a required release target and gets a CI build of the plug-in on a Windows runner plus a live harness pass on a Windows machine before each release; it is not a development platform.

Corpus sourcing adds two Rhino-specific pools to Revit's: the `rhinoscriptsyntax` reference examples (each is a task with an obvious expected outcome), and the Grasshopper example definitions that ship with Rhino (open, drive, solve, read — a ready-made pool for §10's acceptance). The competitive coverage floor is one task per fixed tool in the two broadest incumbent catalogs (§03), run against `execute_script` plus discovery only, in both languages.

## 17. Open questions & things to verify

Facts this document rests on that have not yet been checked against a live Rhino 8, in the order they block phases:

1. ~~`Rhino.Runtime.Code.RhinoCode.RunScript` from a plug-in~~ **Verified 2026-09-10** (`spikes/phase-1a-findings.md` §1): real CPython 3, streams captured, outputs bound, no marshalling, no interruption API.
2. ~~The exact plug-in TFM~~ **Verified on macOS**: `net8.0` on Rhino 8.35. Windows in phase 2.
3. ~~Undo-record behaviour~~ **Verified**: command-per-run, `ExecuteCommand(_Undo)` after the command; save mid-command still open (§07).
4. Whether any pre-show dialog hook exists in RhinoCommon or Eto (§08). Blocks nothing; decides whether §08 ever gains suppression.
5. Rhino for Windows single-document-per-instance, re-confirmed on 8.x (§05).
6. Unsaved-document title uniquification (§12).
7. ~~XML sidecars in an installed Rhino~~ **Verified on macOS** (§09). Windows in phase 2.
8. Off-main-thread reads of `IGH_ActiveObject.Phase` during a solve (§10).
9. yak package size and non-plug-in payload acceptance (§15) — the install path itself is verified, the size limit is not.
10. ~~The undo entry's name~~ **Verified**: always the command's name, `MCPBridgeRun`; the label rides on the result instead (§07).
11. *(new)* `Connector.Settle`'s mechanism under command-per-run (§07).

Decisions deliberately deferred, with the trigger that reopens each: cross-agent document leases (first observed collision between two agents on one document); fixed Grasshopper tools (how-to submissions showing recurring operations); Rhino 9 target (its release); a shared on-disk search index (measured multi-session memory cost).

## 18. Phased roadmap

0. **Shared server packages.** Extract the app-agnostic `internal/` packages from `revit/mcp-server` to a repo-level module the Revit server imports unchanged; CI green on Revit throughout. Success: no Revit behaviour change, one copy of `transport`/`diag`/`registry`/`semsearch`/`howto`.
1. **Core loop, Rhino 8, macOS.** Plug-in with the loopback listener, `instances/<pid>.json`, auth, `register`, heartbeat; the dial-in server; `execute_script`/`poll_execution`/`cancel_execution` in both languages; undo-record-per-run with rollback and the mutation report; the denylist and confirmation gate including the interactive-getter block; `list_instances` with `last_run`; `capture_view` for Rhino viewports (the Grasshopper canvas target lands with phase 4). Success: an agent creates, queries and modifies objects in a Mac Rhino from two concurrent Claude sessions and looks at the result in a viewport, a failed script is rolled back, a cancelled one resolves `cancelled`, and a server restart loses nothing pollable.
2. **Windows.** The same plug-in build loading on Windows Rhino 8, the Win32 window inventory, the Windows CI runner, a live harness pass. Success: phase 1's acceptance on Windows with one document per instance.
3. **API discovery.** Reflection cache over RhinoCommon, Grasshopper and plug-ins; `rhinoscriptsyntax` docstring index; both call shapes in `describe_function`; the shared ranking pipeline; `ambiguous-instance-version`. Success: Revit's `TestRealCorpusRecall` equivalent over a labelled Rhino query set, both languages.
4. **Grasshopper.** `gh_document_id` addressing, `Connector.Grasshopper`, the solve report, the pause/step idioms in `skill.md`. Success: an agent opens a shipped example definition, sets a slider, solves, reads an output tree, and reports a deliberately-broken component's error — from both languages.
5. **File exchange & audit trail.** Workspace tree, `Publish`, `files[]`, document identity including Grasshopper paths, the 14-day sweep.
6. **How-to corpus.** Seed extracted from the harness, stamped per version × platform × language, `search_howtos`/`describe_howto`/`submit_howto`, `TestHowToSweep`/`TestHowToEndToEnd`.
7. **Distribution.** The yak package, `MCPBridgeRegister`, `update_connector` as a check, macOS notarization deferred. Success: a fresh Mac and a fresh Windows machine each install from `_PackageManager` and register with Claude with no manual steps beyond one command.
8. **Later.** Fixed Grasshopper tools; document leases; Rhino 9; shared on-disk search index — each behind the trigger named in §17.

---

*Synthesized from the Revit connector's PRD and its issue history, a survey of ten community Rhino/Grasshopper MCP projects, McNeel forum threads on the .NET runtime, the CPython host and the Windows document model, and the four design decisions agreed on 2026-09-09 (Mac-first with Windows required; Python-first with C# as an equal peer; Grasshopper scriptable, drivable and observable in v1 with fixed tools later; cross-agent observability only).*
