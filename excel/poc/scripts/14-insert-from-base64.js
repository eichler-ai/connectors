// "Upload" an xlsx: Office.js cannot replace the open workbook, but it can pull sheets out of an xlsx
// you hand it as base64 (ExcelApi 1.13), or open a new workbook from one (ExcelApi 1.8).
// This file is a template: substitute __BASE64__ and the sheet list before running, e.g.
//   sed "s|__BASE64__|$(base64 -i book.xlsx | tr -d '\n')|" scripts/14-insert-from-base64.js | excel-bridge run -
// The script must stay under the bridge's 1 MiB request cap (~750 KB of xlsx).
const names = context.workbook.insertWorksheetsFromBase64("__BASE64__", {
  sheetNamesToInsert: ["BridgeDemo"],   // omit to insert every sheet
  positionType: "End",
});
await context.sync();
const ws = context.workbook.worksheets; ws.load("items/name");
await context.sync();
return { insertedIds: names.value, sheets: ws.items.map(w => w.name) };

// Alternative, opens a whole new workbook instead of touching this one:
//   Excel.createWorkbook("__BASE64__"); await context.sync();
