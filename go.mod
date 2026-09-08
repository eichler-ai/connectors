// One module for the hub and every connector it links (hub/docs/PRD.md §16:
// "one binary links every connector"). The hub package defines the Connector
// interface, a connector package implements it, and hub/cmd/hub imports both —
// two modules would need mutual replace directives to express that, which is a
// module cycle in all but name. Import paths keep their directory shape
// (github.com/eichler-ai/connectors/hub, .../excel/connector).
//
// revit/mcp-server, revit/test-harness and excel/poc carry their own go.mod
// and are excluded from this module by the toolchain.
module github.com/eichler-ai/connectors

go 1.25.0

require (
	github.com/coder/websocket v1.8.14
	github.com/modelcontextprotocol/go-sdk v1.7.0
)

require (
	github.com/google/jsonschema-go v0.4.3 // indirect
	github.com/segmentio/asm v1.1.3 // indirect
	github.com/segmentio/encoding v0.5.4 // indirect
	github.com/yosida95/uritemplate/v3 v3.0.2 // indirect
	golang.org/x/oauth2 v0.35.0 // indirect
	golang.org/x/sync v0.20.0 // indirect
	golang.org/x/sys v0.41.0 // indirect
	golang.org/x/time v0.15.0 // indirect
)
