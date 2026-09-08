// Package hub is the hosted service connectors plug into (hub/docs/PRD.md).
// This file is the connector interface (§09); server.go composes the HTTP
// surface and tools.go holds the tools every connector gets for free.
package hub

import (
	"context"
	"io/fs"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// Connector is a host-application integration compiled into the hub. One
// binary links every connector (§16); each gets its own MCP server at
// /<slug>/mcp, bridge endpoint at /<slug>/bridge, and static files under
// /<slug>/.
//
// This is §09 as written minus Docs(): there is no discovery in v1, and an
// interface slot nothing implements is a promise nobody is keeping.
type Connector interface {
	// Slug is the lowercase path segment and MCP client name, e.g. "excel".
	Slug() string
	Capabilities() Capabilities
	// Tools registers the connector's own MCP tools. The generic get_skills
	// and list_instances are added by the hub, not here.
	Tools(reg *ToolRegistry)
	// Static serves the connector's extension files at /<slug>/addin/ and its
	// manifest at /<slug>/manifest.xml (file "manifest.xml" at the root of
	// the FS, with the connector's local dev URL substituted per Options).
	// Nil for a connector with nothing to host.
	Static() fs.FS
	// Skill is the markdown document get_skills returns.
	Skill() []byte
	// Validate is a pre-flight on a script before it is sent anywhere —
	// size, denylist. It may be a no-op.
	Validate(ctx context.Context, script Script) error
}

// Capabilities declares how a connector reaches its host (§04: a bridge is a
// capability, not an assumption).
type Capabilities struct {
	// Bridge: the host runs an extension that dials /<slug>/bridge.
	Bridge bool
	// API: the connector can act through the vendor's REST API with a stored
	// token, without a live bridge. Nothing in phase 0 uses it.
	API bool
	// Languages are the script language tags the bridge accepts.
	Languages []string
}

// Script is what a connector asks the hub to run.
type Script struct {
	// Language is one of the connector's Capabilities.Languages.
	Language string
	Source   string
	// Timeout is the cooperative deadline; zero takes the hub default.
	Timeout time.Duration
}

// Target names where a script runs. Empty InstanceID means "the user's only
// bridge for this connector" and is an error when there is not exactly one.
type Target struct {
	InstanceID string
	DocumentID string
}

// ToolRegistry is what a connector registers its tools against: the
// connector's mcp.Server plus the hub services a tool needs.
type ToolRegistry struct {
	Server *mcp.Server
	Host   *Host
}
