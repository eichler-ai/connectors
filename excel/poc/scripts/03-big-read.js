// Payload/latency probe: fill a 200x50 block, read it all back. Result is ~100KB of JSON.
const sheet = context.workbook.worksheets.getActiveWorksheet();
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
