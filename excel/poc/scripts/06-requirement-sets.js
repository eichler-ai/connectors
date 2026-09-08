// Which ExcelApi requirement sets does this host support, and what happens calling an unsupported API?
const sets = {};
for (const v of ["1.1","1.5","1.9","1.12","1.14","1.16","1.17","1.18","1.19","1.20"]) {
  sets["ExcelApi " + v] = Office.context.requirements.isSetSupported("ExcelApi", v);
}
sets["ExcelApiOnline 1.1"] = Office.context.requirements.isSetSupported("ExcelApiOnline", "1.1");
// Something newish: workbook.getLinkedEntityCellValue-era APIs vary by host; use a benign probe.
let probe;
try {
  const sheet = context.workbook.worksheets.getActiveWorksheet();
  const r = sheet.getRange("A1");
  r.load("valuesAsJson"); // ExcelApi 1.16
  await context.sync();
  probe = { valuesAsJson: r.valuesAsJson };
} catch (e) { probe = { error: e.message, code: e.code }; }
return { diagnostics: Office.context.diagnostics, sets, probe };
