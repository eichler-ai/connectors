// Round-trip: write values and a formula, read them back with the formula evaluated.
// Writes are confined to a BridgeDemo sheet so a real workbook is never touched.
let sheet = context.workbook.worksheets.getItemOrNullObject("BridgeDemo");
await context.sync();
if (sheet.isNullObject) sheet = context.workbook.worksheets.add("BridgeDemo");
const r = sheet.getRange("A1:C2");
r.values = [["Item", "Qty", "Total"], ["Widget", 3, null]];
sheet.getRange("C2").formulas = [["=B2*10"]];
const back = sheet.getRange("A1:C2");
back.load("values,formulas,address");
sheet.load("name");
await context.sync();
return { sheet: sheet.name, address: back.address, values: back.values, formulas: back.formulas };
