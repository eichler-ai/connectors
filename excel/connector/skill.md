# Excel connector — how to drive it

You run Office.js inside the user's open workbook through a task pane (the MCP Bridge) connected to
this MCP Server. The pane must be open in Excel; `list_instances` shows what is connected.

## Tools

- `get_skills` — this document.
- `list_instances` — connected bridges: `instance_id`, host, workbook (`documents[]`, with the active
  sheet in `detail.sheet`). One bridge → tools target it; several → pass `instance_id`.
- `get_status` — workbook name, active sheet, all sheet names, selection, `excel_api` level.
- `execute_script` — `script`, optional `instance_id`, `timeout_ms`, `expect: {workbook, sheet}`.
- `export_file` — `format` (`csv`, `xlsx` or `pdf`), optional `instance_id`, `filename`. Returns a
  short-lived signed `url` plus `bytes`; use this for file bytes, never `execute_script` (see below).
- `import_workbook` — copies the sheets of an `.xlsx` into the **currently open workbook**
  (`content_base64` or `source_url`, under 10 MiB decoded). **This writes to the open document and
  there is no undo**; pass `expect: {workbook}` to fail fast if the active workbook is not the one
  you mean. `options: {sheet_names, position, relative_to_sheet}` picks which sheets and where
  (default: every sheet, after the last existing one). Office renames the incoming sheet on a name
  collision — `added_sheets` reports the actual resulting names, not what you asked for. The file
  must be a genuine `.xlsx` from a real writer; a hand-assembled or truncated one is rejected before
  it reaches the workbook (`not-an-xlsx`).

## Script contract

- `script` is the **body** of `async function (context)`; `context` is the `Excel.RequestContext`
  from `Excel.run`. The pane compiles it with `new Function` — plain JavaScript, no imports, no
  top-level `await` restrictions (it is inside an async function).
- Use the batch pattern: `load()` what you need, `await context.sync()`, then read `.values`,
  `.address`, etc. Nothing is populated before a sync.
- **Return plain values**, never proxy objects. `JSON.stringify` of an unloaded proxy is `{}` or
  throws. Return `{values: r.values}`, not `r`.
- The return value comes back as `result` (JSON). Results over **16 MiB** are replaced by
  `{truncated: true, bytes, head}` with `truncated: true` on the tool result. Page big reads.
- Timeouts: default **30 s**, maximum **600 s** (`timeout_ms`). Simple scripts round-trip in
  ~100 ms, a 10k-cell read in ~300 ms, a whole-document PDF export in ~10 s.
- **A hung script cannot be interrupted.** The pane's JavaScript thread is blocked until it ends;
  the tool returns `timeout` and the user must reload the pane. Never loop waiting on time.
- Errors come back verbatim: `error.code` is the Office.js error code (`InvalidArgument`,
  `ItemNotFound`, `GeneralException`, …) or the JavaScript error name, `error.detail.debug_info`
  holds the failing statement and its neighbours, `error.detail.stack` the stack. Line numbers
  include a short wrapper before your script; locate failures by the statement text, not the line.

## Targets and safety

- Scripts act on the **active sheet** unless they name one:
  `context.workbook.worksheets.getItem("Name")`. The first POC run overwrote a real sheet this way.
- Every successful result carries `target: {workbook, sheet}` — the active workbook and sheet when
  the script started. A script that touches cells without naming a sheet also gets a
  `target-implicit` notice.
- Pass `expect: {workbook, sheet}` on anything that writes. It is checked against the active
  workbook/sheet **before your code runs** and fails with `expect-mismatch` (nothing executed).
- There is **no undo**. Read before you write; write to a new sheet when in doubt.
- Cell values: write 2-D arrays matching the range shape; formulas as strings starting with `=`.
  Dates arrive as serial numbers unless you read `.text`.

## Host quirks (verified live on Excel for the web)

- `getRange` and `charts.add` reject multi-area addresses (`A3:A7,D3:D7`). Build a chart from one
  column and attach categories with `series.setXAxisValues(range)`.
- `getMergedAreas()` on the web reports a merged `A1:E1` as just `A1`; the merge itself is fine.
- `getCellProperties` reads a range's formatting in one round trip; borders are named
  `top/bottom/left/right` there but `EdgeTop`… in `BorderCollection`. No fill reads back as `""`.
- `chart.getImage()` returns a base64 PNG — the cheap way to look at what you built.
- `workbook.getSelectedRange()` throws when the selection is not a range (a chart is selected).
- Whole-document export works on the web: `Office.context.document.getFileAsync(Office.FileType.Pdf
  | Compressed)` — ~1 s for xlsx, ~10 s for a 200-page PDF; `export_file` runs it for you and never
  puts the bytes through a script. csv has no `getFileAsync` type — it is the active sheet's used
  range, not the whole workbook, serialised from `.text` the way Excel's own CSV export reads it.
- No API replaces the open workbook. `workbook.insertWorksheetsFromBase64(xlsxBase64)` copies sheets in;
  `Excel.createWorkbook(base64)` opens a new one. Do not put base64 in a script; use `import_workbook`.
- `insertWorksheetsFromBase64` throws `InvalidArgument` on a malformed or minimal `.xlsx` — it needs a
  real, well-formed file from an actual writer, not a hand-assembled zip.
- Excel keeps a "closed" pane alive: after the X is clicked the bridge stays connected until the
  workbook tab closes. Two panes for the same runtime → newest connection wins.
- `ExcelApi 1.20` is the level seen on the web. Check `get_status.excel_api` before using anything
  newer than 1.12, and guard with `Office.context.requirements.isSetSupported("ExcelApi", "1.x")`.

## Example

```js
const ws = context.workbook.worksheets.getItem("Data");
const r = ws.getRange("A1:C3");
r.values = [["Item", "Qty", "Total"], ["Bolt", 4, "=B2*1.5"], ["Nut", 10, "=B3*0.5"]];
r.format.font.bold = true;
const used = ws.getUsedRange(); used.load("address,values");
await context.sync();
return { address: used.address, rows: used.values.length };
```
