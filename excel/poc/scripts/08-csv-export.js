// CSV export: no Office.js "save as", so build it from the used range. Run with -out sheet.csv.
// Uses `text` (what the cell displays) rather than `values`, matching what Excel's own CSV export writes.
const sheet = context.workbook.worksheets.getActiveWorksheet();
const used = sheet.getUsedRange(true);
used.load("text,address");
sheet.load("name");
await context.sync();
const q = s => /[",\r\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
return used.text.map(row => row.map(q).join(",")).join("\r\n") + "\r\n";
