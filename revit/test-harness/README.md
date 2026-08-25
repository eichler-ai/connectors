# Live integration test harness

Drives a real Revit instance (via `prlctl` in the dev environment, or directly on a Windows-native install) through the test corpus against a real Revit MCP Bridge + Revit MCP Server pair. See PRD §13 (Validation & test corpus) — [`../docs/PRD.md`](../docs/PRD.md).

- `corpus/` — test case definitions
- `runner/` — orchestrates Revit/VM lifecycle and executes cases
- `fixtures/` — sample `.rvt` documents

Not yet scaffolded.
