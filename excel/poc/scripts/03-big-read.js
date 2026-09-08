// Payload/latency probe: fill a 200x50 block, read it all back. Result is ~100KB of JSON.
// Writes are confined to a BridgeDemo sheet so a real workbook is never touched.
let sheet = context.workbook.worksheets.getItemOrNullObject("BridgeDemo");
await context.sync();
if (sheet.isNullObject) sheet = context.workbook.worksheets.add("BridgeDemo");
const rows = 200, cols = 50;
const data = [];
for (let i = 0; i < rows; i++) { const row = []; for (let j = 0; j < cols; j++) row.push(i * cols + j); data.push(row); }
const r = sheet.getRangeByIndexes(0, 0, rows, cols);
r.values = data;
await context.sync();
const used = sheet.getUsedRange();
used.load("values,address,rowCount,columnCount");
await context.sync();
return { address: used.address, rows: used.rowCount, cols: used.columnCount, values: used.values };
