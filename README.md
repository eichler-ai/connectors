# Connectors

MCP-based connectors that let Claude and other agents drive desktop host applications
directly — starting with Revit.

Each connector follows the same two-component pattern (see [`CONVENTIONS.md`](./CONVENTIONS.md)):
an in-process **Bridge** (a plugin/add-in inside the host app) plus a standalone, agent-facing
**MCP Server** that speaks MCP and fans out to however many live app instances exist.

## Connectors

- [`revit/`](./revit/) — **Revit connector** (Revit MCP Bridge + Revit MCP Server). Rather
  than a fixed catalog of pre-built tools, its primary surface is `execute_script` — dynamic
  C# compiled and run against the live document — plus API-discovery tools that let an agent
  read Revit's own API documentation on demand. Working today against Revit 2025 and 2027:
  the core execution loop, dialog suppression, multi-instance addressing, API discovery, and
  file exchange are shipped and live-validated; signed release distribution is not yet built
  (install from source — see the [quickstart](./revit/docs/quickstart.md)). Design:
  [`revit/docs/PRD.md`](./revit/docs/PRD.md); per-phase status: PRD §15.

## Contributing & security

See [`CONTRIBUTING.md`](./CONTRIBUTING.md) and [`SECURITY.md`](./SECURITY.md) — the latter
matters here more than for most projects: these connectors execute agent-authored code inside
host applications by design, and its trust model is worth understanding before running or
extending them.

## License

Apache License 2.0 — see [`LICENSE`](./LICENSE) and [`NOTICE`](./NOTICE).
