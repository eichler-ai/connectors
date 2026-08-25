# Revit MCP Server (broker)

The standalone Go process that speaks MCP to Claude/agents and TCP/NDJSON to one or more Revit MCP Bridge instances. See PRD §04 (Architecture), §05 (Connection & multiplexing) — [`../docs/PRD.md`](../docs/PRD.md).

Planned layout:

- `cmd/mcp-server/` — main entrypoint
- `internal/mcp/` — tool registration, request/response schemas
- `internal/transport/` — TCP/NDJSON client to the add-in, framing
- `internal/registry/` — instance registry, heartbeat, `list_instances`
- `internal/singleton/` — lock-or-proxy logic (PRD §05)
- `internal/pathmap/` — local/remote path translation (PRD §09)

Not yet scaffolded — see the `revit-connector-development` skill for the build process once code exists here.
