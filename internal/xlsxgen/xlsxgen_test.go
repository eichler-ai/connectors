package xlsxgen

import (
	"archive/zip"
	"bytes"
	"encoding/xml"
	"io"
	"strings"
	"testing"
)

// cellXML/rowXML mirror just enough of SpreadsheetML to check what Build
// wrote, the same way a reader would: unzip, parse the sheet part, read
// cell values back out. No dependency on a real Excel/OOXML library — the
// point of this test is that Build's own output parses as valid XML inside
// a valid zip with the parts a reader expects, matching the shape verified
// live and captured in excel/connector/testdata/sample.xlsx.
type xmlCell struct {
	Ref     string `xml:"r,attr"`
	Type    string `xml:"t,attr"`
	Value   string `xml:"v"`
	Formula string `xml:"f"`
}
type xmlRow struct {
	Ref   string    `xml:"r,attr"`
	Cells []xmlCell `xml:"c"`
}
type xmlSheetData struct {
	Rows []xmlRow `xml:"row"`
}
type xmlWorksheet struct {
	SheetData xmlSheetData `xml:"sheetData"`
}

func unzip(t *testing.T, b []byte) map[string][]byte {
	t.Helper()
	zr, err := zip.NewReader(bytes.NewReader(b), int64(len(b)))
	if err != nil {
		t.Fatalf("not a valid zip: %v", err)
	}
	out := map[string][]byte{}
	for _, f := range zr.File {
		rc, err := f.Open()
		if err != nil {
			t.Fatal(err)
		}
		data, err := io.ReadAll(rc)
		rc.Close()
		if err != nil {
			t.Fatal(err)
		}
		out[f.Name] = data
	}
	return out
}

func parseSheet(t *testing.T, files map[string][]byte, name string) xmlWorksheet {
	t.Helper()
	data, ok := files[name]
	if !ok {
		t.Fatalf("zip has no %s (has: %v)", name, keys(files))
	}
	var ws xmlWorksheet
	if err := xml.Unmarshal(data, &ws); err != nil {
		t.Fatalf("%s did not parse as XML: %v", name, err)
	}
	return ws
}

func keys(m map[string][]byte) []string {
	var out []string
	for k := range m {
		out = append(out, k)
	}
	return out
}

func TestBlankIsOneEmptySheet(t *testing.T) {
	b, err := Blank()
	if err != nil {
		t.Fatal(err)
	}
	files := unzip(t, b)
	for _, want := range []string{"[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/worksheets/sheet1.xml"} {
		if _, ok := files[want]; !ok {
			t.Errorf("blank workbook missing %s", want)
		}
	}
	if !strings.Contains(string(files["xl/workbook.xml"]), `name="Sheet1"`) {
		t.Errorf("blank workbook sheet name: %s", files["xl/workbook.xml"])
	}
	ws := parseSheet(t, files, "xl/worksheets/sheet1.xml")
	if len(ws.SheetData.Rows) != 0 {
		t.Errorf("blank sheet has rows: %+v", ws.SheetData.Rows)
	}
}

func TestBuildFromDataRoundTrips(t *testing.T) {
	sheets := []Sheet{
		{Name: "Data", Rows: [][]any{
			{"Item", "Qty", "Total"},
			{"Bolt", 4.0, "=B2*1.5"},
			{"Nut & Washer", true, nil},
		}},
		{Name: "Second <sheet>", Rows: [][]any{{"only one cell"}}},
	}
	b, err := Build(sheets)
	if err != nil {
		t.Fatal(err)
	}
	files := unzip(t, b)

	wb := string(files["xl/workbook.xml"])
	if !strings.Contains(wb, `name="Data"`) || !strings.Contains(wb, `name="Second &lt;sheet&gt;"`) {
		t.Fatalf("workbook.xml sheet names: %s", wb)
	}
	types := string(files["[Content_Types].xml"])
	if !strings.Contains(types, "sheet1.xml") || !strings.Contains(types, "sheet2.xml") {
		t.Fatalf("[Content_Types].xml: %s", types)
	}

	ws := parseSheet(t, files, "xl/worksheets/sheet1.xml")
	if len(ws.SheetData.Rows) != 3 {
		t.Fatalf("sheet1 rows: %+v", ws.SheetData.Rows)
	}
	row1 := ws.SheetData.Rows[0]
	if len(row1.Cells) != 3 || row1.Cells[0].Ref != "A1" || row1.Cells[0].Value != "Item" || row1.Cells[0].Type != "str" {
		t.Fatalf("row1: %+v", row1.Cells)
	}
	row2 := ws.SheetData.Rows[1]
	if row2.Cells[0].Value != "Bolt" {
		t.Fatalf("A2: %+v", row2.Cells[0])
	}
	if row2.Cells[1].Ref != "B2" || row2.Cells[1].Value != "4" || row2.Cells[1].Type != "" {
		t.Fatalf("B2 (numeric, no t attr): %+v", row2.Cells[1])
	}
	if row2.Cells[2].Ref != "C2" || row2.Cells[2].Formula != "B2*1.5" {
		t.Fatalf("C2 (formula, leading = stripped): %+v", row2.Cells[2])
	}
	row3 := ws.SheetData.Rows[2]
	if len(row3.Cells) != 2 { // the nil third cell is omitted entirely
		t.Fatalf("row3 (nil cell must be omitted): %+v", row3.Cells)
	}
	if row3.Cells[0].Value != "Nut & Washer" {
		t.Fatalf("A3 escaping round trip: %+v", row3.Cells[0])
	}
	if row3.Cells[1].Ref != "B3" || row3.Cells[1].Type != "b" || row3.Cells[1].Value != "1" {
		t.Fatalf("B3 (bool): %+v", row3.Cells[1])
	}

	ws2 := parseSheet(t, files, "xl/worksheets/sheet2.xml")
	if len(ws2.SheetData.Rows) != 1 || ws2.SheetData.Rows[0].Cells[0].Value != "only one cell" {
		t.Fatalf("sheet2: %+v", ws2.SheetData.Rows)
	}
}

func TestBuildEmptySheetsIsBlank(t *testing.T) {
	b, err := Build(nil)
	if err != nil {
		t.Fatal(err)
	}
	files := unzip(t, b)
	if _, ok := files["xl/worksheets/sheet1.xml"]; !ok {
		t.Fatal("Build(nil) did not fall back to a blank sheet")
	}
}

func TestUnnamedSheetGetsADefaultName(t *testing.T) {
	b, err := Build([]Sheet{{Rows: [][]any{{"x"}}}})
	if err != nil {
		t.Fatal(err)
	}
	files := unzip(t, b)
	if !strings.Contains(string(files["xl/workbook.xml"]), `name="Sheet1"`) {
		t.Fatalf("unnamed sheet default: %s", files["xl/workbook.xml"])
	}
}

func TestColName(t *testing.T) {
	cases := map[int]string{0: "A", 1: "B", 25: "Z", 26: "AA", 27: "AB", 51: "AZ", 52: "BA", 701: "ZZ", 702: "AAA"}
	for i, want := range cases {
		if got := colName(i); got != want {
			t.Errorf("colName(%d) = %q, want %q", i, got, want)
		}
	}
}

func TestManySheetsProduceDistinctRIDs(t *testing.T) {
	var sheets []Sheet
	for i := 0; i < 5; i++ {
		sheets = append(sheets, Sheet{Rows: [][]any{{float64(i)}}})
	}
	b, err := Build(sheets)
	if err != nil {
		t.Fatal(err)
	}
	files := unzip(t, b)
	rels := string(files["xl/_rels/workbook.xml.rels"])
	for i := 1; i <= 5; i++ {
		if !strings.Contains(rels, "sheet"+itoa(i)+".xml") {
			t.Errorf("rels missing sheet%d: %s", i, rels)
		}
	}
}

func itoa(i int) string {
	if i == 0 {
		return "0"
	}
	s := ""
	for i > 0 {
		s = string(rune('0'+i%10)) + s
		i /= 10
	}
	return s
}
