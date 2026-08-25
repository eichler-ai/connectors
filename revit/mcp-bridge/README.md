# Revit MCP Bridge (add-in)

The in-process Revit add-in. See PRD §04 (Architecture), §06 (Threading & script execution), §07 (Modal dialog suppression), §08 (API discovery), §09 (File exchange) — [`../docs/PRD.md`](../docs/PRD.md).

Planned layout:

- `src/MCPBridge.AddIn/` — `IExternalApplication` entry point + `.addin` manifest, thin glue only
- `src/MCPBridge.Core/` — testable business logic (execution orchestration, dialog/failure policy, document identity, discovery)
- `src/MCPBridge.RevitAdapter/` — thin interfaces over Revit API types, the seam that makes `Core` unit-testable
- `tests/MCPBridge.Core.Tests/`, `tests/MCPBridge.Discovery.Tests/` — unit tests, no live Revit needed
- `tests/MCPBridge.Integration.Tests/` — needs a live Revit instance, run via `../test-harness/`

Not yet scaffolded — see the `revit-connector-development` skill for the build process once code exists here.
