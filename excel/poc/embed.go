// Package poc embeds the add-in's static files so `excel-bridge serve` is a single self-contained
// binary. Pass -addin <dir> to serve from disk instead while iterating on taskpane.js.
package poc

import "embed"

//go:embed addin
var Addin embed.FS
