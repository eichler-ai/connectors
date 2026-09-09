# Excel Connector

Lets Claude and other agents read, write, format, chart and analyse a user's **live Excel
workbook** by running Office.js inside it — move files in and out, and create new workbooks in the
user's OneDrive. Unlike the Revit connector, which installs a desktop add-in, the Excel connector is
a **hosted service**: the agent talks to a Cloud Run endpoint, and the workbook talks to the same
endpoint from a task pane running inside Excel for the web.

## What it can do

- **`execute_script`** — run arbitrary Office.js (`async function (context)`) against the live
  workbook: read/write cells, format, build charts, add sheets. This is the primary surface — the
  other tools wrap common jobs.
- **`get_status` / `list_instances`** — see which panes are connected, the open workbook, active
  sheet, selection, and the `ExcelApi` level, so the agent knows where a write will land.
- **`export_file`** — pull the workbook (or the active sheet) out as `csv` / `xlsx` / `pdf`; returns
  a short-lived signed URL.
- **`import_workbook`** — copy another `.xlsx`'s sheets into the open workbook.
- **`create_workbook`** — create a brand-new workbook in the user's OneDrive through Microsoft
  Graph (no pane needed), and hand the user a link to open it.

Every write is guarded — a script can assert the workbook/sheet it expects (`expect`) before it runs,
and touching cells without naming a sheet raises a notice — because **Excel writes have no undo**.
The agent-facing contract (tools, the script model, and host quirks verified live) is in
[`connector/skill.md`](connector/skill.md), which the `get_skills` tool returns.

## How it works

```
Claude / MCP client                    Excel for the web
      │                                      │
      │ MCP (HTTPS, JWT)                      │  Office.js
      ▼                                      ▼
 ┌───────────────── Connectors Hub (Cloud Run) ─────────────────┐
 │  /excel/mcp  ──────────── routes by user_id ──────────  /excel/bridge (WebSocket)
 │       ▲                                                        ▲
 │   OAuth 2.1 (Microsoft sign-in)              MCP Bridge task pane (signed in, same account)
 └──────────────────────────────────────────────────────────────┘
```

An agent's tool call arrives at the hub's `/excel/mcp` MCP endpoint (authenticated with a JWT from
the hub's own OAuth 2.1 server, which delegates sign-in to Microsoft). The hub forwards it over a
WebSocket to the **MCP Bridge task pane** the user has open in Excel, which executes it with Office.js
against the live workbook and returns the result.

The one rule that ties it together: **the pane must be open and signed in as the same Microsoft
account as the MCP session.** The hub routes by `user_id`, so a session and a pane meet only when
both signed in as the same account. If `list_instances` is empty, the pane isn't open, isn't signed
in, or is a different account. (`create_workbook` is the exception — it acts on OneDrive through
Graph with no pane involved, then the user opens the new file and a pane connects.)

The connector code lives here; the service that hosts it — the bridge protocol, the authorization
server, file exchange, the audit trail, and Cloud Run deploy — is the [Connectors Hub](../hub/),
whose [README](../hub/README.md) is the developer/operator reference.

## Connect and use

You need a Microsoft account (personal Microsoft accounts work; a work/school account works if tenant
policy allows sideloading).

**1. Add the connector to your MCP client.**

Claude Code:

```sh
claude mcp add --transport http excel https://connectors.eichler.ai/excel/mcp
```

claude.ai (custom connector): Settings → Connectors → Add custom connector, URL
`https://connectors.eichler.ai/excel/mcp`, leave client id/secret empty.

Either way the client discovers the hub's authorization server from the 401 challenge, registers
itself, and sends you through Microsoft sign-in in the browser. (Staging runs the same way at
`https://connectors.eichler.ai/excel-staging/mcp`.)

**2. Open the MCP Bridge pane in Excel and sign in.** In Excel for the web, sideload the add-in:
download [`https://connectors.eichler.ai/excel/manifest.xml`](https://connectors.eichler.ai/excel/manifest.xml),
then **Home → Add-ins → More Add-ins → My Add-ins → Upload My Add-in** and choose it. An **MCP
Bridge** button appears on the Home tab — open it, click **Sign in with Microsoft**, and use the
**same account** you used in step 1. The pane shows *Signed in as …* and *Connected*, and reconnects
on later workbooks without signing in again (the token lasts 90 days; **Sign out** revokes it,
**Switch account** picks a different one).

**3. Drive it.** In a session: `list_instances` should show the pane with its workbook and active
sheet; then `get_status`, and `execute_script` with e.g. `script: "return 1"`.

## Status

- **Live in production** at `connectors.eichler.ai/excel/mcp` — OAuth sign-in, pane sign-in, and the
  full tool surface (execute/status/export/import/create) are shipped and live-verified.
- **Distribution is still sideload.** The add-in is uploaded by hand per the steps above; one-click
  install from **AppSource** and Microsoft publisher verification are the next steps toward a
  non-technical install ([tracked](https://github.com/eichler-ai/connectors/issues), #246/#247).
- **`create_workbook` is personal-OneDrive first.** It creates in OneDrive through Microsoft Graph;
  work/school tenants without a SharePoint Online license can't create yet.
- **Excel for the web** is the supported host; desktop Excel isn't verified.

## Contents

```
connector/     the hub.Connector for Excel: execute_script, get_status, export_file,
               import_workbook, create_workbook, and skill.md (the agent-facing contract)
addin/         the MCP Bridge task pane: manifest, taskpane.html/js (embedded into the hub binary)
docs/          rfc-graph-create-and-open.md — the Graph create/open ("Flow 1") design
poc/           the earlier standalone proof-of-concept (its own go.mod; superseded by this connector)
```

## More

- [`connector/skill.md`](connector/skill.md) — the agent-facing tool contract and live-verified host
  quirks (what `get_skills` returns).
- [`../hub/README.md`](../hub/README.md) — running the hub locally, the OAuth design, file exchange,
  the audit trail, and deploying to Cloud Run.
- [`../hub/docs/PRD.md`](../hub/docs/PRD.md) — the hub's design doc.
- [`docs/rfc-graph-create-and-open.md`](docs/rfc-graph-create-and-open.md) — the create-in-OneDrive,
  open-in-browser, drive-from-the-pane flow.
