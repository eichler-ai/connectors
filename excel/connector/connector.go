// Package connector is the Excel connector (hub PRD §10): execute_script and
// get_status on top of the hub's Exec, the skill file get_skills serves, and
// the embedded task pane. There are no discovery tools in v1 — Office.js is
// well represented in model training; what the model does not know is this
// connector's contract and the host's quirks, and skill.md carries those.
package connector

import (
	"context"
	_ "embed"
	"errors"
	"fmt"
	"io/fs"
	"regexp"
	"strings"

	"github.com/eichler-ai/connectors/excel/addin"
	"github.com/eichler-ai/connectors/hub"
)

//go:embed skill.md
var skill []byte

const (
	// Slug is the connector's path segment and MCP client name.
	Slug = "excel"
	// Language is the one script tag the add-in runs.
	Language = "officejs"
	// Source labels diagnostics raised here.
	Source = "excel"
	// maxScriptBytes bounds a script before it is sent anywhere. Generated
	// scripts are a few KB; 1 MiB leaves room for an inlined table while
	// keeping a stray base64 file (which belongs in the file exchange, §10)
	// from riding through as a script.
	maxScriptBytes = 1 << 20
)

// Connector implements hub.Connector for Excel.
type Connector struct{}

// New returns the connector.
func New() *Connector { return &Connector{} }

func (*Connector) Slug() string { return Slug }

func (*Connector) Capabilities() hub.Capabilities {
	return hub.Capabilities{Bridge: true, Languages: []string{Language}}
}

func (*Connector) Static() fs.FS { return addin.FS }

func (*Connector) Skill() []byte { return skill }

func (*Connector) Validate(_ context.Context, s hub.Script) error {
	if strings.TrimSpace(s.Source) == "" {
		return errors.New("script is empty")
	}
	if len(s.Source) > maxScriptBytes {
		return fmt.Errorf("script is %d bytes, over the %d-byte limit; move bulk data out of the script", len(s.Source), maxScriptBytes)
	}
	return nil
}

func (c *Connector) Tools(reg *hub.ToolRegistry) {
	registerExecuteScript(reg, c)
	registerGetStatus(reg, c)
	registerExportFile(reg, c)
}

// namesSheet is the target-implicit heuristic (§10): a script "names a
// target sheet" when it reaches a worksheet through the collection by name
// or index, or creates one. Anything else — getActiveWorksheet, a bare
// getRange on the workbook, the selection — lands wherever the user last
// clicked, which is how the POC overwrote a real sheet. Deliberately a plain
// pattern, not a parser: a false positive costs an info notice, a false
// negative costs nothing the expect parameter cannot cover.
var namesSheet = regexp.MustCompile(`worksheets\s*\.\s*(getItem|getItemOrNullObject|add|getFirst|getLast)\s*\(`)

// touchesCells says whether a script reads or writes cells at all; a script
// that only inspects names or settings gets no notice.
var touchesCells = regexp.MustCompile(`\b(getRange|getUsedRange|getRangeByIndexes|getActiveWorksheet|getSelectedRange|getActiveCell|getActiveWorksheetOrNullObject)\s*\(`)

func targetImplicit(script string, expectSheet string) bool {
	return expectSheet == "" && touchesCells.MatchString(script) && !namesSheet.MatchString(script)
}
