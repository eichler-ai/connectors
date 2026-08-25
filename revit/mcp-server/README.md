# Revit MCP Server (broker)

The standalone Go process that speaks MCP to Claude/agents and TCP/NDJSON to one or more Revit MCP Bridge instances. See PRD §04 (Architecture), §05 (Connection & multiplexing) — [`../docs/PRD.md`](../docs/PRD.md).

Layout (Phase 1 — "Core loop", PRD §15 phase 1; see the `revit-connector-development` skill for the build process):

- `cmd/mcp-server/` — main entrypoint: flag/env parsing (`-mode local|remote`, `-bind`, `-port`, `-app-data-dir`), singleton lock acquisition, and either running as primary (binds the TCP listener, mints the token, writes `broker.json`, runs the MCP server over stdio) or as secondary (dials the primary and transparently pipes its own stdio through that TCP connection).
- `internal/diag/` — the shared diagnostic-record shape (PRD §01: `severity`/`code`/`source`/`message`/`detail`/`remedy`), reused everywhere an error or notice is reported.
- `internal/transport/` — JSON-RPC 2.0 message types, NDJSON framing (PRD §05 "Framing"), and `Conn`, a bidirectional request/response-correlating peer connection used for both add-in and agent-client-proxy wire traffic.
- `internal/registry/` — in-memory instance registry keyed by `instance_id`, populated from `register` notifications. Phase 1 scope only: no heartbeat, no `list_instances` tool, no pruning (Phase 2, PRD §15).
- `internal/singleton/` — lock-or-proxy logic (PRD §05): OS-level exclusive lock file (`lock_unix.go`/`lock_windows.go`), auth token generation/validation, `broker.json` read/write, and the platform-appropriate app-data directory (PRD §09).
- `internal/execution/` — routes `execute_script`/`poll_execution`/`cancel_execution` to the right instance's wire connection; implements the two-shape response contract and per-instance busy detection from PRD §06.
- `internal/broker/` — TCP connection handling: the mandatory auth-token gate (PRD §10), `register` notification handling into the registry + execution manager, and running an MCP server session over every authenticated agent-client (secondary-broker-proxy) connection.
- `internal/mcpserver/` — registers `execute_script`/`poll_execution`/`cancel_execution` against the official MCP Go SDK's `Server`, delegating to `internal/execution`.

Not yet built (later phases per PRD §15): `internal/pathmap/` (local/remote path translation, PRD §09, Phase 3), `list_instances`/heartbeat (Phase 2), `list_functions`/`search_functions`/`describe_function` (Phase 3).

Run `go test ./...` from this directory.
