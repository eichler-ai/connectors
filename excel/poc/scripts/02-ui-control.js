// UI control: create/activate a sheet and move the selection. Watch Excel while this runs.
const wb = context.workbook;
let s = wb.worksheets.getItemOrNullObject("BridgeDemo");
await context.sync();
if (s.isNullObject) s = wb.worksheets.add("BridgeDemo");
s.activate();
s.getRange("D7").select();
const sel = wb.getSelectedRange();
sel.load("address");
await context.sync();
return { selected: sel.address };
