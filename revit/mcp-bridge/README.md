# Revit MCP Bridge (add-in)

The in-process Revit add-in. See PRD §04 (Architecture), §06 (Threading & script execution), §07 (Modal dialog suppression), §08 (API discovery), §09 (File exchange), §11 (Multi-version strategy) — [`../docs/PRD.md`](../docs/PRD.md).

Layout:

- `src/MCPBridge.AddIn/` — `IExternalApplication` entry point + `.addin` manifest: connection loop (`BridgeHost.cs`), remote/local topology config (`MCPBridgeApplication.cs`), ribbon status button. Multi-targets `net10.0-windows`/`net8.0-windows` (Revit 2027/2025, PRD §11).
- `src/MCPBridge.Core/` — testable business logic: execution orchestration, dialog/failure policy, document identity, discovery, file exchange (`Publish`/workspace paths), the script-facing globals (`ScriptGlobals`), and `ScriptApiDenylist`, the compile-time guard on what a script may call (PRD §14).
- `src/MCPBridge.RevitAdapter/` — thin interfaces over real Revit API types (`IDocumentAdapter`, `RevitDocumentAdapter`, etc.), the seam that makes `Core` unit-testable against fakes instead of a live Revit process. The `IRaw*Source` interfaces (`IRawRevitSources.cs`) are the exception that proves the rule: they hand out the *real* `Document`/`UIApplication`/`UIDocument` for `ScriptGlobals`, and only the live adapters implement them — deliberately, so a fake never has to name a Revit type (see that file's doc comment; it is what keeps the test assembly loadable at all).
- `tests/MCPBridge.Core.Tests/`, `tests/MCPBridge.Discovery.Tests/` — unit tests, no live Revit needed. Both multi-target alongside `AddIn`/`RevitAdapter` so the `net8.0-windows` leg (Revit 2025) has real coverage, not just `net10.0-windows`. Live testing against a real Revit instance happens through `../test-harness/` — a separate Go MCP client suite that speaks the real wire protocol end-to-end (add-in included) rather than a C# in-process test project.

See the `revit-connector-development` skill for the build process (multi-TFM build commands, the VM/Parallels dev-environment quirks, and the PR review checklist).
