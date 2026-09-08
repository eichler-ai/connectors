// Package addin embeds the Excel MCP Bridge task pane so the hub binary is
// self-contained: the manifest, the pane, and its icons are served from
// /excel/addin/ and /excel/manifest.xml with no install layout to get wrong.
package addin

import "embed"

// FS holds every file the hub serves for this add-in.
//
//go:embed manifest.xml taskpane.html taskpane.js icon-16.png icon-32.png icon-64.png icon-80.png
var FS embed.FS
