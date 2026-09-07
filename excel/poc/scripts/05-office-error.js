// What does an Office.js error carry? Ask for a sheet that does not exist.
const s = context.workbook.worksheets.getItem("NoSuchSheet");
s.load("name");
await context.sync();
return s.name;
