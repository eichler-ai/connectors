// Tier-2 live harness for the Rhino connector (implementation-plan.md "Tiers"): spawns the real
// mcp-server binary, speaks MCP over its stdio, against a running Rhino with the MCP Bridge loaded.
// Build tag `harness`; excluded from `go test ./...` and type-checked only in CI.
module github.com/eichler-ai/connectors/rhino/test-harness

go 1.26.5
