# Excel connector — how to drive it

Read, write, format, chart, and analyse the user's **live Excel workbook** by running Office.js in
it, move files in and out, and create new workbooks in their OneDrive.

## How it works (mental model)

Your tool call → this MCP Server (the hub) → a WebSocket → a **task pane** (the "MCP Bridge") the
user has open in Excel → `Office.js` executing against the live workbook in their browser.

- The pane must be **open and signed in as the same Microsoft account** as this session. Nothing
  routes otherwise.
- `list_instances` is your ground truth for what is connected, and reports `signed_in_as` — the
  Microsoft account **this session** is signed in as. **If it is empty**, the user hasn't opened the
  pane, isn't signed in, or the pane is signed in as a *different* account (its own "Signed in as …"
  won't match `signed_in_as`); the response's `hint` says which. Tell them the account this session
  uses and, if the pane is already open and Connected, have them click **Switch account** in the pane
  to match it.
- `execute_script` runs your JavaScript in the workbook; the other tools wrap common jobs.
- `create_workbook` is the exception: it makes a file through Microsoft Graph with **no pane
  involved**, then the user opens it and a pane connects.

## Tools

- `get_skills` — this document.
- `list_instances` — connected panes: `instance_id`, host, open `documents[]` (active sheet in
  `detail.sheet`). One connected → tools target it by default; several → pass `instance_id`.
- `get_status` — workbook name, active sheet, all sheet names, selection, `excel_api` level. Call it
  before a write to confirm where you'll land. `get_status {}`.
- `execute_script` — run Office.js (contract + example below).
  `execute_script {script: "...", expect: {workbook: "Budget.xlsx", sheet: "Q1"}}`.
- `export_file` — get file **bytes** out; returns a short-lived signed `url` + `bytes`. Use this for
  files, never `execute_script`. csv = the active sheet's used range; xlsx/pdf = the whole workbook.
  `export_file {format: "pdf"}`.
- `import_workbook` — copy an `.xlsx`'s sheets **into the open workbook** (writes, no undo — pass
  `expect`). Source is `content_base64` or `source_url` (real `.xlsx`, <10 MiB). `options`
  picks which sheets and where; `added_sheets` reports the real names (Office renames on collision).
  `import_workbook {source_url: "https://…/data.xlsx", expect: {workbook: "Report.xlsx"}}`.
- `create_workbook` — a **new** workbook in the user's OneDrive; returns `{web_url, doc_key,
  open_hint}`. Tell the user to open `web_url`; once a pane connects, `list_instances` and match a
  document's id to `doc_key` to find it before driving it. Needs Microsoft file access (personal
  OneDrive; `graph-not-connected` → the user signs in again).
  `create_workbook {name: "Forecast", data: [{name: "Sheet1", rows: [["Month","Rev"],["Jan",100]]}]}`.

## execute_script contract

- `script` is the **body** of `async function (context)`; `context` is the `Excel.RequestContext`
  from `Excel.run`. Plain JavaScript, no imports; it is already inside an async function.
- Batch pattern: `load()` what you need, `await context.sync()`, then read `.values`, `.address`, ….
  Nothing is populated before a sync.
- **Return plain values, never proxy objects.** `JSON.stringify` of an unloaded proxy is `{}` or
  throws. Return `{values: r.values}`, not `r`. The return value comes back as `result` (JSON);
  results over **16 MiB** become `{truncated: true, bytes, head}` — page big reads.
- Timeouts: default **30 s**, max **600 s** (`timeout_ms`). A simple script round-trips in ~100 ms.
- Errors come back verbatim: `error.code` is the Office.js code (`InvalidArgument`, `ItemNotFound`,
  …) or the JS error name; `error.detail.debug_info` holds the failing statement, `.stack` the stack.
  Line numbers include a short wrapper — locate failures by the statement text, not the line.

## Safety — writes have no undo

- Scripts act on the **active sheet** unless they name one:
  `context.workbook.worksheets.getItem("Name")`. A script that touches cells without naming a sheet
  gets a `target-implicit` notice; every success carries `target: {workbook, sheet}`.
- Pass `expect: {workbook, sheet}` on anything that writes. It is checked **before your code runs**
  and fails with `expect-mismatch` (nothing executed). Read before you write; write to a new sheet
  when unsure.
- Cell values: write 2-D arrays matching the range shape; formulas are strings starting with `=`.
  Dates read back as serial numbers unless you read `.text`.
- **A hung script cannot be interrupted** — the pane's single JS thread blocks until it ends, the
  tool returns `timeout`, and the user must reload the pane. Never loop waiting on time.

## Tips & host quirks (verified live on Excel for the web)

- `getRange`/`charts.add` reject multi-area addresses (`A3:A7,D3:D7`). Build a chart from one column,
  attach categories with `series.setXAxisValues(range)`.
- `getMergedAreas()` reports a merged `A1:E1` as just `A1` (the merge itself is fine).
- `getCellProperties` reads a range's formatting in one round trip; borders are `top/bottom/left/right`
  there but `EdgeTop`… in `BorderCollection`; no fill reads back as `""`.
- `chart.getImage()` returns a base64 PNG — the cheap way to see what you built.
- `workbook.getSelectedRange()` throws when the selection is not a range (e.g. a chart is selected).
- To move files, prefer `export_file`/`import_workbook` — never put base64 in a script. Office's own
  `insertWorksheetsFromBase64` needs a genuine, well-formed `.xlsx`; a hand-assembled or non-xlsx
  source is rejected up front (`not-an-xlsx`).
- `ExcelApi 1.20` is the level on the web. Check `get_status.excel_api` before using anything newer
  than 1.12 and guard with `Office.context.requirements.isSetSupported("ExcelApi", "1.x")`.

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
