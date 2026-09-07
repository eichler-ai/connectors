// Read formatting back in one round trip with getCellProperties (ExcelApi 1.9), plus the sheet-level
// bits (conditional formats, validation, merges, column widths, freeze panes).
const sheet = context.workbook.worksheets.getItem("BridgeDemo");
const table = sheet.getRange("A1:E8");
const props = table.getCellProperties({
  address: true,
  format: {
    font: { bold: true, italic: true, color: true, name: true, size: true, underline: true, strikethrough: true },
    fill: { color: true },
    horizontalAlignment: true, wrapText: true, columnWidth: true, rowHeight: true,
    borders: { style: true, color: true, weight: true },
  },
});
table.load("numberFormat,text,values");
const merged = table.getMergedAreasOrNullObject();
merged.load("address");
const cfs = sheet.getRange("A1:E8").conditionalFormats;
cfs.load("items/type,items/priority");
const dv = sheet.getRange("B4:B7").dataValidation;
dv.load("rule,errorAlert,valid");
const frozen = sheet.freezePanes.getLocationOrNullObject();
frozen.load("address");
await context.sync();

// Compact the per-cell dump: only cells whose formatting differs from the default.
const cells = [];
for (const row of props.value) for (const c of row) {
  const f = c.format, o = { address: c.address };
  if (f.font.bold) o.bold = true;
  if (f.font.italic) o.italic = true;
  if (f.font.underline !== "None") o.underline = f.font.underline;
  if (f.font.strikethrough) o.strike = true;
  if (f.font.color !== "#000000") o.fontColor = f.font.color;
  if (f.font.name !== "Calibri" && f.font.name !== "Aptos Narrow") o.font = f.font.name;
  if (f.font.size !== 11 && f.font.size !== 12) o.size = f.font.size;
  if (f.fill.color && f.fill.color !== "#FFFFFF") o.fill = f.fill.color; // no fill reads back as ""
  if (f.horizontalAlignment !== "General") o.align = f.horizontalAlignment;
  if (f.wrapText) o.wrap = true;
  // getCellProperties names sides top/bottom/left/right (not the EdgeTop… names of BorderCollection).
  const b = ["top", "bottom", "left", "right"].filter(k => f.borders[k].style !== "None").map(k => k + ":" + f.borders[k].style + "/" + f.borders[k].weight + "/" + f.borders[k].color);
  if (b.length) o.borders = b;
  if (Object.keys(o).length > 1) cells.push(o);
}
return {
  numberFormat: table.numberFormat, text: table.text,
  merged: merged.isNullObject ? null : merged.address,
  conditionalFormats: cfs.items.map(i => i.type),
  validation: { rule: dv.rule, errorAlert: dv.errorAlert, valid: dv.valid },
  frozen: frozen.isNullObject ? null : frozen.address,
  columnWidths: props.value[0].map(c => c.format.columnWidth),
  rowHeights: props.value.map(r => r[0].format.rowHeight),
  cells,
};
