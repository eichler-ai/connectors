# Revit MCP Bridge (add-in)

The in-process Revit add-in. See PRD §04 (Architecture), §06 (Threading & script execution), §07 (Modal dialog suppression), §08 (API discovery), §09 (File exchange), §11 (Multi-version strategy) — [`../docs/PRD.md`](../docs/PRD.md).

Layout:

- `src/MCPBridge.AddIn/` — `IExternalApplication` entry point + `.addin` manifest: connection loop (`BridgeHost.cs`), remote/local topology config (`MCPBridgeApplication.cs`), ribbon status button. Multi-targets `net10.0-windows`/`net8.0-windows` (Revit 2027/2025, PRD §11).
- `src/MCPBridge.Core/` — testable business logic: execution orchestration, dialog/failure policy, document identity, discovery, file exchange (`Publish`/workspace paths), the script-facing globals (`ScriptGlobals`/`IScript*`).
- `src/MCPBridge.RevitAdapter/` — thin interfaces over real Revit API types (`IDocumentAdapter`, `RevitDocumentAdapter`, etc.), the seam that makes `Core` unit-testable against fakes instead of a live Revit process.
- `tests/MCPBridge.Core.Tests/`, `tests/MCPBridge.Discovery.Tests/` — unit tests, no live Revit needed. Both multi-target alongside `AddIn`/`RevitAdapter` so the `net8.0-windows` leg (Revit 2025) has real coverage, not just `net10.0-windows`.
- `tests/MCPBridge.Integration.Tests/` — scaffolded, currently empty (no real test files, only build-generated artifacts). Live testing against a real Revit instance actually happens through `../test-harness/` instead — a separate Go MCP client suite that speaks the real wire protocol end-to-end (add-in included) rather than a C# in-process test project; see its own README before assuming this project needs work.

See the `revit-connector-development` skill for the build process (multi-TFM build commands, the VM/Parallels dev-environment quirks, and the PR review checklist).
