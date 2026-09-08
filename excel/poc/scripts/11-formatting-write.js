// Cell formatting: number formats, font, fill, borders, alignment, column width, merged title,
// conditional formatting and data validation. Confined to BridgeDemo, which is cleared first.
let sheet = context.workbook.worksheets.getItemOrNullObject("BridgeDemo");
await context.sync();
if (sheet.isNullObject) sheet = context.workbook.worksheets.add("BridgeDemo");
sheet.getRange().clear();            // values + formats
sheet.activate();

// Title: merged, large bold, centred, dark fill with white text.
const title = sheet.getRange("A1:E1");
title.merge();
title.values = [["Bridge formatting demo", null, null, null, null]];
title.format.font.bold = true;
title.format.font.size = 16;
title.format.font.color = "#FFFFFF";
title.format.fill.color = "#1D6F42";
title.format.horizontalAlignment = "Center";
title.format.rowHeight = 28;

// Header row.
const header = sheet.getRange("A3:E3");
header.values = [["Item", "Qty", "Unit price", "Total", "Due"]];
header.format.font.bold = true;
header.format.fill.color = "#D9EAD3";
header.format.borders.getItem("EdgeBottom").style = "Continuous";
header.format.borders.getItem("EdgeBottom").weight = "Medium";
header.format.horizontalAlignment = "Center";

// Body: values, formulas, per-column number formats.
const body = sheet.getRange("A4:E8");
body.values = [
  ["Widget", 3, 12.5, null, 46000],
  ["Gadget", 12, 3.99, null, 46010],
  ["Gizmo", 0, 199, null, 46020],
  ["Doohickey", 250, 0.25, null, 46030],
  ["Total", null, null, null, null],
];
sheet.getRange("D4:D7").formulas = [["=B4*C4"], ["=B5*C5"], ["=B6*C6"], ["=B7*C7"]];
sheet.getRange("D8").formulas = [["=SUM(D4:D7)"]];
sheet.getRange("B4:B8").numberFormat = [["#,##0"], ["#,##0"], ["#,##0"], ["#,##0"], ["#,##0"]];
sheet.getRange("C4:D8").numberFormat = Array(5).fill(["$#,##0.00", "$#,##0.00"]);
sheet.getRange("E4:E7").numberFormat = Array(4).fill(["yyyy-mm-dd"]);
sheet.getRange("A8:E8").format.font.bold = true;
sheet.getRange("A8:E8").format.borders.getItem("EdgeTop").style = "Double";
sheet.getRange("B4:E8").format.horizontalAlignment = "Right";
sheet.getRange("A4:A8").format.font.italic = true;
sheet.getRange("A4").format.font.color = "#C00000";
sheet.getRange("A5").format.font.name = "Courier New";
sheet.getRange("A6").format.font.underline = "Single";
sheet.getRange("A7").format.font.strikethrough = true;
sheet.getRange("A7").format.wrapText = true;

// Outline border around the whole table.
for (const edge of ["EdgeTop", "EdgeBottom", "EdgeLeft", "EdgeRight"]) {
  const b = sheet.getRange("A3:E8").format.borders.getItem(edge);
  b.style = "Continuous"; b.color = "#1D6F42"; b.weight = "Thin";
}

// Conditional formatting: highlight zero quantities, data bars on totals.
const cf = sheet.getRange("B4:B7").conditionalFormats.add("CellValue");
cf.cellValue.format.fill.color = "#FFC7CE";
cf.cellValue.format.font.color = "#9C0006";
cf.cellValue.rule = { formula1: "0", operator: "EqualTo" };
sheet.getRange("D4:D7").conditionalFormats.add("DataBar").dataBar.barDirection = "LeftToRight";

// Data validation: quantities must be whole numbers 0..1000.
sheet.getRange("B4:B7").dataValidation.rule = {
  wholeNumber: { formula1: 0, formula2: 1000, operator: "Between" }
};
sheet.getRange("B4:B7").dataValidation.errorAlert = { showAlert: true, style: "Stop", title: "Qty", message: "0 to 1000" };

// Column widths, freeze the header, select the table so it's visible.
sheet.getRange("A:A").format.columnWidth = 110;
sheet.getRange("B:E").format.columnWidth = 80;
sheet.freezePanes.freezeRows(3);
sheet.getRange("A3:E8").select();
await context.sync();
return "formatted";
