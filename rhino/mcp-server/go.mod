// The Rhino MCP Server (rhino/docs/PRD.md §04, §05): speaks MCP over stdio to one client and dials
// in to every running Rhino MCP Bridge it finds in the instances directory. No singleton: every
// stdio server process is independent (PRD §05). The app-agnostic core is the shared
// internal/servercore module; dependency versions are pinned to match it.
module github.com/eichler-ai/connectors/rhino/mcp-server

go 1.26.5

require (
	github.com/eichler-ai/connectors/internal/servercore v0.0.0
	github.com/google/uuid v1.6.0
	github.com/modelcontextprotocol/go-sdk v1.7.0
	golang.org/x/sys v0.47.0
)

require (
	github.com/google/jsonschema-go v0.4.3 // indirect
	github.com/segmentio/asm v1.1.3 // indirect
	github.com/segmentio/encoding v0.5.4 // indirect
	github.com/yosida95/uritemplate/v3 v3.0.2 // indirect
	golang.org/x/oauth2 v0.35.0 // indirect
	golang.org/x/sync v0.22.0 // indirect
	golang.org/x/time v0.15.0 // indirect
)

replace github.com/eichler-ai/connectors/internal/servercore => ../../internal/servercore
