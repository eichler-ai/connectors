// What happens if a script returns an Office.js proxy object instead of plain data?
const sheet = context.workbook.worksheets.getActiveWorksheet();
sheet.load("name,position");
await context.sync();
return sheet;
