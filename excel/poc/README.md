# Excel connector — proof of concept

Throwaway spike to answer three questions before designing an Excel connector:

1. Can an Excel for the web add-in reach a local process? (Bridge pattern: the add-in dials out.)
2. What are the limits of running agent-generated Office.js inside the add-in?
3. What does driving the UI (sheet switching, selection) feel like from outside?

It is one Go binary plus a three-file add-in. No MCP yet: a CLI plays the agent.

```
excel-bridge serve                 HTTPS host for the add-in + WebSocket bridge, https://localhost:3000
excel-bridge run scripts/01-read-write.js
excel-bridge run -e 'return context.workbook.worksheets.getActiveWorksheet().load("name") && await context.sync(), 1'
excel-bridge status                which workbook/host is connected
excel-bridge trust-cert            prints the one-time certificate trust command
```

A script is the body of an `async function (context)` where `context` is the `Excel.RequestContext`
from `Excel.run`. Whatever it returns is JSON-serialised back and printed. Errors come back with the
Office.js `code` and `debugInfo` when present.

## How it fits together

```
excel-bridge run ──POST /exec──▶ excel-bridge serve ◀──wss://localhost:3000/ws── task pane (Office.js)
     (CLI)                        (Go, one process)         dials out on load          runs Excel.run(script)
```

The add-in only runs while its task pane is open. One add-in connection is held at a time; a reload of
the pane replaces it.

## Live test procedure (Excel for the web, Chrome)

1. Build and start the bridge. The first run writes a self-signed `localhost` certificate to
   `~/Library/Application Support/Connectors/Excel/`.

   ```sh
   cd excel/poc && go build -o excel-bridge ./cmd/excel-bridge && ./excel-bridge serve
   ```

2. Trust the certificate once (`./excel-bridge trust-cert` prints the exact command), restart Chrome,
   and confirm https://localhost:3000/taskpane.html loads with no warning. The page will say it is not
   inside Excel; that is expected, and the bridge log should show a connect/disconnect.

3. Open a workbook in Excel for the web (it must live in OneDrive or SharePoint). Sideload the add-in:
   **Home → Add-ins → More Add-ins → My Add-ins → Upload My Add-in**, choose `addin/manifest.xml`.
   If the upload option is missing, tenant policy blocks sideloading; a personal Microsoft account
   with OneDrive does allow it.

4. A **Bridge** group appears on the Home tab. Click **Open Bridge**. The pane shows host, platform,
   Office.js version, highest supported `ExcelApi` set, and the connection status.
   `./excel-bridge status` should now report the workbook name.

5. Run the acceptance scripts in order and record what happens:

   | script | question it answers |
   |---|---|
   | `01-read-write.js` | round-trip values and a formula |
   | `02-ui-control.js` | does `activate()` / `select()` visibly move the UI |
   | `03-big-read.js` | payload size and latency for a 10k-cell read |
   | `04-hang.js` (use `-timeout 5s`) | what a busy-loop script does to the pane and the bridge |
   | `05-office-error.js` | what an Office.js error object carries |
   | `06-requirement-sets.js` | which API sets the web host supports; failure mode of an unsupported call |
   | `07-return-proxy.js` | what happens when a script returns a proxy object instead of data |
   | `08-csv-export.js` (`-out x.csv`) | CSV built from the used range's display text |
   | `09-xlsx-export.js` (`-out x.xlsx`) | whole document via the File API, Open XML |
   | `10-pdf-export.js` (`-out x.pdf`) | whole document via the File API, PDF |
   | `11-formatting-write.js` | fonts, fills, borders, number/date formats, merge, alignment, widths, conditional formats, validation, freeze panes |
   | `12-formatting-read.js` | reads all of the above back, per-cell via `getCellProperties` |
   | `14-insert-from-base64.js` (template) | "upload": copy sheets from an xlsx into the open workbook |
   | `13-chart-pivot.js` | column + line charts with titles/labels/formatting, a pivot table with two data fields, readback |

## Findings so far (Excel for the web, Chrome, 2026-09-07)

- The task pane connects to `wss://localhost:3000` from inside the Office iframe with no Chrome
  prompt and no mixed-content issue. Host reports `ExcelApi 1.20`.
- Simple scripts round-trip in ~80 ms. A 10k-cell read is ~260 ms and ~60 KB.
- Office.js errors arrive with `code`, the failing statement and its neighbours, and a stack.
- A busy-loop script blocks the pane until it ends; the bridge times out and the pane recovers on
  its own afterwards. Nothing can interrupt it from outside.
- `getFileAsync` works for both `Compressed` (xlsx, ~0.7 s for 400 KB) and `Pdf` (~9 s for a
  194-page, 4.9 MB render). Excel for the web supports PDF here even though the docs only promise
  it for Word and PowerPoint.
- `golang.org/x/net/websocket`'s codec returns one *frame* per receive and Chrome fragments large
  messages, so replies over ~128 KB were truncated until the hub switched to a stream JSON decoder.
- Formatting: everything in script 11 applied in one ~200 ms sync and rendered correctly in the PDF
  export. `getCellProperties` reads a range's full formatting in one round trip (sides are named
  `top/bottom/left/right` there, `EdgeTop…` in `BorderCollection`; no fill reads back as `""`).
  `getMergedAreas` on the web reported the merged title `A1:E1` as just `A1`, though the merge
  itself rendered correctly.
- Charts and pivot tables work (~500 ms for two charts + a pivot). Neither `getRange` nor
  `charts.add` accept a multi-area address like `A3:A7,D3:D7`; build the chart from one column and
  attach categories with `series.setXAxisValues`. `chart.getImage()` returns a PNG, which is a cheap
  way for an agent to see what it built without a whole-document PDF export.
- Uploading a file: no API replaces the open workbook. `insertWorksheetsFromBase64` copies chosen
  sheets from a supplied xlsx (~2 s for a 418 KB file, formatting preserved) and
  `Excel.createWorkbook(base64)` opens a new workbook from one. Whole-file replacement is a
  OneDrive/Graph operation outside Excel. Base64 travels inside the script, so the bridge's request
  cap bounds the file size; a real connector would carry files out of band.
- Scripts default to the active sheet. The first live run overwrote cells in a real workbook; the
  connector must surface the target workbook and sheet before any write.

## Things to watch for during the live test

- **Chrome local-network permission.** Chrome may prompt or silently block a page on `office.com`
  reaching `localhost`. The task pane iframe is served *from* localhost, so the socket is same-origin,
  which should avoid it. If the pane loads but the socket never opens, check `chrome://flags` for
  Local Network Access and the DevTools console of the pane (right-click the pane → Inspect).
- **Mixed content.** Both the page and the socket are HTTPS/WSS on the same origin, so there should
  be none. A `SecurityError` from the `WebSocket` constructor is logged in the pane if it happens.
- **Certificate.** Excel's iframe will not show a certificate warning; an untrusted cert just yields a
  blank pane. Step 2 exists to catch that outside Excel first.
- **Hung scripts.** The bridge times out and moves on, but the pane's JavaScript thread is stuck until
  the browser kills it or the pane is closed and reopened. Nothing outside can interrupt it.

## Not in scope here

MCP tool surface, multiple workbooks, undo, Excel desktop, packaging. Each is a design question for the
real connector once the answers above are in.
