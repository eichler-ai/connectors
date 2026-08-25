# Revit Connector

Lets Claude and other agents execute dynamic C# against a live, open Revit document, and progressively learn the Revit API instead of relying on a fixed tool catalog.

Two components:

- [`mcp-bridge/`](./mcp-bridge/) — the Revit MCP Bridge, a Revit add-in (.NET)
- [`mcp-server/`](./mcp-server/) — the Revit MCP Server, a standalone agent-facing process (Go)

Plus:

- [`install/`](./install/) — deployment scripts (Revit install, MCP Server registration)
- [`test-harness/`](./test-harness/) — live integration test harness and corpus

## Design

Full design doc: [`docs/PRD.md`](./docs/PRD.md).

## Development process

See the `revit-connector-development` skill (`.claude/skills/revit-connector-development/` at the repo root) for the day-to-day build/test/review process.

## Status

Pre-implementation. See the PRD's Phased Roadmap (§15) for what's next.
