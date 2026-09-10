# Rhino Connector

Lets Claude and other agents run Python 3 and C# against live Rhino 8 documents and Grasshopper definitions, and learn the Rhino API on demand, in the same mold as the [Revit connector](../revit/README.md).

Two components (`CONVENTIONS.md` "Bridge + Server"):

- [`mcp-bridge/`](./mcp-bridge/) — the Rhino MCP Bridge, a Rhino plug-in (.NET 8, `Rhino.MCPBridge.*`). Listens on loopback and publishes `instances/<pid>.json`.
- [`mcp-server/`](./mcp-server/) — the Rhino MCP Server, a Go process that speaks MCP over stdio and dials in to every running Rhino. No singleton: each server process is independent.

Plus [`test-harness/`](./test-harness/) (live tier-2 suite) and [`dev-tooling/`](./dev-tooling/) (`deploy-plugin.sh`: build, yak-package, install, restart Rhino).

## Design

[`docs/PRD.md`](./docs/PRD.md) is the source of truth; [`docs/implementation-plan.md`](./docs/implementation-plan.md) is the phase plan; [`docs/spikes/`](./docs/spikes/) records what was verified live before the design was committed.

## Status

Phase 1 in progress. Landed: the plug-in skeleton (listener, instance file, auth, live `register`, heartbeat), the server's dialer and registry, `list_instances`, the tier-1 suites on both sides and a four-case live harness. Not yet: `execute_script` in either language, discovery, Grasshopper, file exchange.

## Developing on the Mac

```sh
brew install dotnet@8                         # once; the deploy script sets DOTNET_ROOT itself
rhino/dev-tooling/deploy-plugin.sh            # build + yak install + restart Rhino (discards unsaved work)
cd rhino/mcp-server && go build -o mcp-server-mac ./cmd/mcp-server
cd ../test-harness && go test -tags harness ./... -v -broker-exe ../mcp-server/mcp-server-mac
```

Tier 1: `dotnet test rhino/mcp-bridge` and `go test -race ./...` in `rhino/mcp-server` — both run in CI on every push. The harness needs an unlocked session and a Rhino with a model open; it skips, never fails, when no instance is connected.
