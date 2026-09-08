// Charts and pivot tables on the BridgeDemo table written by script 11 (header A3:E3, data A4:E7).
const sheet = context.workbook.worksheets.getItem("BridgeDemo");
sheet.activate();

// Remove leftovers from a previous run so the script is repeatable.
sheet.charts.load("items/name"); sheet.pivotTables.load("items/name");
await context.sync();
sheet.charts.items.forEach(c => c.delete());
sheet.pivotTables.items.forEach(p => p.delete());
sheet.getRange("G1:M40").clear();

// Column chart of Total by Item, with a title, axis title, data labels, and a fixed position.
// Neither getRange() nor charts.add accept a multi-area source; build from the value column and
// attach the non-adjacent category column through the series API (ExcelApi 1.7).
const chart = sheet.charts.add("ColumnClustered", sheet.getRange("D3:D7"), "Columns");
chart.series.getItemAt(0).setXAxisValues(sheet.getRange("A4:A7"));
chart.name = "TotalsByItem";
chart.title.text = "Total by item";
chart.title.format.font.size = 14;
chart.axes.valueAxis.title.text = "USD";
chart.axes.valueAxis.numberFormat = "$#,##0";
chart.dataLabels.showValue = true;
chart.dataLabels.numberFormat = "$#,##0.00";
chart.legend.visible = false;
chart.series.getItemAt(0).format.fill.setSolidColor("#1D6F42");
chart.setPosition("G12", "M28");

// Line chart of Qty over Due date on the same sheet, below.
const line = sheet.charts.add("Line", sheet.getRange("B3:B7"), "Columns");
line.series.getItemAt(0).setXAxisValues(sheet.getRange("E4:E7")); // categories from a non-adjacent column
line.name = "QtyOverTime";
line.title.text = "Qty by due date";
line.axes.categoryAxis.numberFormat = "mmm d";
line.setPosition("G30", "M44");

// Pivot: rows = Item, values = sum of Total and sum of Qty, placed at G3.
const pivot = sheet.pivotTables.add("DemoPivot", sheet.getRange("A3:E7"), sheet.getRange("G3"));
pivot.rowHierarchies.add(pivot.hierarchies.getItem("Item"));
const totalField = pivot.dataHierarchies.add(pivot.hierarchies.getItem("Total"));
totalField.name = "Sum of Total";
totalField.numberFormat = "$#,##0.00";
totalField.summarizeBy = "Sum";
const qtyField = pivot.dataHierarchies.add(pivot.hierarchies.getItem("Qty"));
qtyField.summarizeBy = "Sum";
pivot.layout.layoutType = "Tabular";
pivot.layout.showRowGrandTotals = true;
await context.sync();

// Read back what was created.
chart.load("name,chartType,height,width,top,left");
line.load("name,chartType");
const pivotRange = pivot.layout.getRange();
pivotRange.load("address,values");
pivot.rowHierarchies.load("items/name"); pivot.dataHierarchies.load("items/name,items/summarizeBy");
await context.sync();
return {
  charts: [{ name: chart.name, type: chart.chartType, size: [chart.width, chart.height], at: [chart.left, chart.top] }, { name: line.name, type: line.chartType }],
  pivot: { range: pivotRange.address, rows: pivot.rowHierarchies.items.map(h => h.name), data: pivot.dataHierarchies.items.map(h => h.name + "/" + h.summarizeBy), values: pivotRange.values },
};
