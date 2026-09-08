// Package xlsxgen builds a genuinely valid .xlsx (Office Open XML
// SpreadsheetML) from rows in memory, for create_workbook's from-data and
// blank sources (excel/docs/rfc-graph-create-and-open.md §3.2). Live-verified
// finding that motivates this package: a hand-assembled minimal xlsx is
// rejected by both Excel's insertWorksheetsFromBase64 (excel/connector/
// skill.md) and, per the RFC's create-and-open story, by Excel for the web
// opening a file from OneDrive — it needs a real writer's output, not an
// approximation. This generator emits exactly the shape verified live and
// committed as excel/connector/testdata/sample.xlsx ([Content_Types].xml,
// _rels/.rels, xl/workbook.xml, xl/_rels/workbook.xml.rels, one
// xl/worksheets/sheetN.xml per sheet — no styles.xml, no shared strings
// table, cell text inlined with t="str"), extended to more than one sheet.
package xlsxgen

import (
	"archive/zip"
	"bytes"
	"fmt"
	"io"
	"strconv"
	"strings"
)

// Sheet is one worksheet: Rows[r][c] is the cell at row r+1 (1-based, matching
// Excel), column c+1. A cell value is a string, float64, int, bool, or nil
// (an empty cell, omitted from the XML entirely — same "sparse row" shape
// Office.js reads). A string starting with "=" is written as a formula
// rather than a literal (matching execute_script's convention in
// excel/connector/skill.md, "formulas as strings starting with =").
type Sheet struct {
	Name string
	Rows [][]any
}

const xmlHeader = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>` + "\n"

// Blank returns a single empty sheet named "Sheet1" — create_workbook's
// blank source. A workbook needs at least one sheet to open at all.
func Blank() ([]byte, error) { return Build([]Sheet{{Name: "Sheet1"}}) }

// Build assembles sheets into a .xlsx. An empty sheets list is treated as
// Blank rather than an error, since a caller building "whatever data was
// given, or empty" shouldn't need its own branch for the empty case.
func Build(sheets []Sheet) ([]byte, error) {
	if len(sheets) == 0 {
		return Blank()
	}
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)

	write := func(name, content string) error {
		w, err := zw.Create(name)
		if err != nil {
			return fmt.Errorf("xlsxgen: %s: %w", name, err)
		}
		_, err = io.WriteString(w, content)
		return err
	}

	var contentOverrides, sheetEls, sheetRels strings.Builder
	for i, sh := range sheets {
		n := i + 1
		name := sh.Name
		if strings.TrimSpace(name) == "" {
			name = fmt.Sprintf("Sheet%d", n)
		}
		fmt.Fprintf(&contentOverrides, `<Override PartName="/xl/worksheets/sheet%d.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>`, n)
		fmt.Fprintf(&sheetEls, `<sheet name="%s" sheetId="%d" r:id="rId%d"/>`, escapeXML(name), n, n)
		fmt.Fprintf(&sheetRels, `<Relationship Id="rId%d" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet%d.xml"/>`, n, n)
	}

	if err := write("[Content_Types].xml", xmlHeader+
		`<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">`+
		`<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>`+
		`<Default Extension="xml" ContentType="application/xml"/>`+
		`<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>`+
		contentOverrides.String()+`</Types>`); err != nil {
		return nil, err
	}
	if err := write("_rels/.rels", xmlHeader+
		`<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">`+
		`<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>`+
		`</Relationships>`); err != nil {
		return nil, err
	}
	if err := write("xl/workbook.xml", xmlHeader+
		`<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">`+
		`<sheets>`+sheetEls.String()+`</sheets></workbook>`); err != nil {
		return nil, err
	}
	if err := write("xl/_rels/workbook.xml.rels", xmlHeader+
		`<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">`+sheetRels.String()+`</Relationships>`); err != nil {
		return nil, err
	}
	for i, sh := range sheets {
		if err := write(fmt.Sprintf("xl/worksheets/sheet%d.xml", i+1), xmlHeader+
			`<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>`+sheetDataXML(sh.Rows)+`</sheetData></worksheet>`); err != nil {
			return nil, err
		}
	}
	if err := zw.Close(); err != nil {
		return nil, fmt.Errorf("xlsxgen: %w", err)
	}
	return buf.Bytes(), nil
}

func sheetDataXML(rows [][]any) string {
	var b strings.Builder
	for r, row := range rows {
		var cells strings.Builder
		for c, v := range row {
			if v == nil {
				continue
			}
			cells.WriteString(cellXML(colName(c)+strconv.Itoa(r+1), v))
		}
		if cells.Len() == 0 {
			continue
		}
		fmt.Fprintf(&b, `<row r="%d">%s</row>`, r+1, cells.String())
	}
	return b.String()
}

func cellXML(ref string, v any) string {
	switch val := v.(type) {
	case bool:
		iv := 0
		if val {
			iv = 1
		}
		return fmt.Sprintf(`<c r="%s" t="b"><v>%d</v></c>`, ref, iv)
	case float64:
		return fmt.Sprintf(`<c r="%s"><v>%s</v></c>`, ref, strconv.FormatFloat(val, 'g', -1, 64))
	case float32:
		return cellXML(ref, float64(val))
	case int:
		return fmt.Sprintf(`<c r="%s"><v>%d</v></c>`, ref, val)
	case int64:
		return fmt.Sprintf(`<c r="%s"><v>%d</v></c>`, ref, val)
	case string:
		if strings.HasPrefix(val, "=") && len(val) > 1 {
			return fmt.Sprintf(`<c r="%s"><f>%s</f></c>`, ref, escapeXML(val[1:]))
		}
		return fmt.Sprintf(`<c r="%s" t="str"><v>%s</v></c>`, ref, escapeXML(val))
	default:
		return fmt.Sprintf(`<c r="%s" t="str"><v>%s</v></c>`, ref, escapeXML(fmt.Sprint(val)))
	}
}

// colName converts a 0-based column index to Excel's letters (0 -> "A",
// 25 -> "Z", 26 -> "AA").
func colName(i int) string {
	s := ""
	i++
	for i > 0 {
		i--
		s = string(rune('A'+i%26)) + s
		i /= 26
	}
	return s
}

var xmlReplacer = strings.NewReplacer("&", "&amp;", "<", "&lt;", ">", "&gt;", `"`, "&quot;", "'", "&apos;")

func escapeXML(s string) string { return xmlReplacer.Replace(s) }
