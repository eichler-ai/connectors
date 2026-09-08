# RFC: create-and-open — Graph upload → open in browser → bridge control

Status: draft for review (2026-09-08). Owner: hub/Excel connector. Depends on: phase 1 (sign-in,
bridge tokens) done; relates to phase 2 (Excel v1 tools) and phase 3 (distribution).

## 1. The user story

> Claude has a workbook it wants the user to work in — one it generated, or one the user handed it.
> Claude creates the file in the user's OneDrive, hands back a link, the user clicks it, the
> workbook opens in Excel for the web with the MCP Bridge pane already connected, and from that
> moment Claude can read, edit and drive the live document exactly as it does today.

This is the "API path + bridge" combination the architecture anticipated (PRD §04: *a bridge is a
capability, not an assumption*; §09 `Capabilities{Bridge, API}`). The Excel connector gains an
**API path** — Microsoft Graph — used for the one thing the live-pane bridge cannot do: put a file
where Excel for the web can open it, before anything is open. Once open, the **bridge** takes over.
The two never drive the same open document at once (that is Flow 2, explicitly out of scope here —
Graph and interactive co-authoring do not compose reliably).

## 2. End-to-end sequence

```
Claude (MCP)        Hub (connector + Graph client)        Microsoft Graph / OneDrive        User's browser
     │  create_workbook(content?)  │                                   │                          │
     │ ───────────────────────────>│                                   │                          │
     │                             │  (first use) incremental consent  │                          │
     │                             │   for Files.ReadWrite.AppFolder   │                          │
     │                             │  PUT …/approot:/<name>.xlsx:/content│                        │
     │                             │ ─────────────────────────────────>│                          │
     │                             │        driveItem {id, webUrl}      │                          │
     │                             │ <─────────────────────────────────│                          │
     │  { webUrl, doc_key, hint }  │                                   │                          │
     │ <───────────────────────────│                                   │                          │
     │  "Open this to let me drive it: <webUrl>"                       │  user clicks webUrl ────>│
     │                             │                                   │   Excel for the web opens│
     │                             │   add-in auto-opens (if installed)│   pane signs in, dials   │
     │                             │ <───────────── bridge hello (document id ≈ doc_key) ─────────│
     │  list_instances → matches doc_key → execute_script … (bridge, as today)                    │
```

## 3. Pieces to build

### 3.1 Graph as the connector's API path
- The Excel connector declares `Capabilities{Bridge: true, API: true}` and gains a Graph client in
  `excel/connector`. The hub keeps the connector interface unchanged; Graph lives behind the
  connector, not the hub core.
- **Delegated token acquisition.** The hub already logs the user in through Microsoft OIDC for
  identity (scopes `openid profile email`). Graph calls need a Microsoft **access token for Graph**
  with a Files scope, plus a stored Microsoft **refresh token** to mint more without re-prompting.
  Two ways to get it:
  - **Incremental consent (recommended).** Sign-in stays minimal. The *first* time the user invokes
    a Graph-backed tool, the hub runs a second, incremental Microsoft authorization requesting
    `offline_access <Files scope>`; Microsoft shows a one-time consent for file access; the hub
    stores the resulting refresh token. Least surprise, least privilege, opt-in.
  - **Upfront.** Request the Files scope at every sign-in. Simpler code, but every user consents to
    file access even if they never use it. Rejected for v1.
- **Storing the Microsoft refresh token.** Sensitive. Encrypt at rest (AEAD with a key from Secret
  Manager, distinct from the JWT signing key), keyed by `{user_id, provider}`. Never logged. A new
  store collection `graph_tokens` (hash/ciphertext only). Revoked by `RevokeUser` alongside bridge
  tokens.

### 3.2 The `create_workbook` tool (MCP)
- Input: an optional content source and a name. Content source, in order of v1 priority:
  1. **From data** — the tool builds a minimal valid `.xlsx` server-side from rows/sheets the agent
     supplies (the common case: Claude generated a table and wants it in Excel). Keeps bytes off the
     wire and avoids the agent hand-building xlsx (which, as we found live, Excel's importer
     rejects if malformed).
  2. **From an uploaded file** — the agent puts an `.xlsx` into the hub file exchange (phase 2,
     GCS); the hub streams those bytes to Graph. No base64 in the tool call.
  3. **Blank** — a new empty workbook.
- Action: upload to the user's OneDrive **app folder** via Graph
  (`PUT /me/drive/special/approot:/<name>.xlsx:/content`; files >4 MiB use an upload session).
- Output: `{ webUrl, doc_key, open_hint }` where `webUrl` is what the user clicks, `doc_key` is the
  stable identifier the agent will match against `list_instances` once the pane connects, and
  `open_hint` is human text ("Open this and I'll take the controls").
- The file lands in the user's own OneDrive (`Apps/Eichler Connectors/…` under the app folder), so
  the user owns it and it is visible to them; the app can only touch what it created.

### 3.3 Correlation: matching the created file to the connected pane
The glue that makes "then Claude controls it" work. After `create_workbook`, the agent must know
which future bridge instance is *this* file.
- Today the pane registers each document with `id = Office.context.document.url` (the OneDrive URL).
- Graph returns a driveItem `id` and a `webUrl`, which are **not** byte-identical to the pane's
  `document.url`. So we need a stable join key. Options (decision below):
  a. Have the pane also report the OneDrive drive/item identity it can derive
     (`Office.context.document.url` → canonicalized), and have the hub canonicalize the Graph
     `webUrl`/driveItem the same way, so `doc_key` matches on both sides.
  b. Have `create_workbook` return the expected `document.url` shape rather than the Graph `webUrl`,
     computed from the driveItem, and match on that.
- Until the join is exact, fall back to: one new bridge appearing for this user shortly after
  `create_workbook`, with a matching title, is the target — good enough to prompt, not to
  auto-write. **This is the main open technical question (§6).**

### 3.4 Auto-open of the pane
- For the pane to be there when the user opens the file, the connector must be **installed for the
  user** (AppSource or admin-deployed — phase 3) and set to **auto-open**. Office supports an
  add-in opening its task pane automatically on document open (shared runtime +
  `Office.addin.setStartupBehavior(load)`, and/or the manifest auto-open capability). Reliability on
  Excel for the web needs a live check.
- **Pre-distribution**, the user must sideload the connector and open the pane manually after
  clicking the link. The tool's `open_hint` says so while distribution is pending. **Post
  distribution** it is one click. This is why the flow is specced now but ships seamless with
  phase 3.

## 4. What this is NOT
- Not Flow 2 (Graph editing a document the user has open at the same time — locking/consistency
  hazard; excluded).
- Not headless UI control — Graph has no UI; anything UI-driven waits for the pane.
- Not a replacement for the phase-2 file exchange (GCS export/import into an already-open workbook);
  this is the OneDrive path that produces an openable document.

## 5. Security & privacy
- Least privilege: `Files.ReadWrite.AppFolder` (app can touch only files it created) over
  `Files.ReadWrite` (all the user's files). v1 recommendation: AppFolder.
- Incremental, opt-in consent for file access (§3.1); the Microsoft consent screen states it.
- Microsoft refresh tokens encrypted at rest, never logged, revoked with the user.
- The created file is in the *user's* OneDrive, owned by them, not in hub storage.
- Personal Microsoft accounts have OneDrive; work/school accounts need a OneDrive/SharePoint
  license. Behaviour differs — verify per account type.

## 6. Open questions / decisions needed
1. **Consent model:** incremental (recommended) vs upfront. 
2. **File scope:** `Files.ReadWrite.AppFolder` (recommended) vs `Files.ReadWrite`.
3. **Content source priority for v1:** from-data (recommended first) / from-uploaded-file / blank —
   which do we build first?
4. **The correlation join (§3.3)** — the real technical unknown. Needs a live check: what exactly
   does the web pane report as `document.url` for a Graph-app-folder file, and can the hub compute
   the same key from the driveItem? Proposed spike: create a file via Graph, open it, read
   `Office.context.document.url` in the pane, compare to the driveItem `id`/`webUrl`.
5. **Account type for v1:** personal OneDrive only, or work/SharePoint too.

## 7. Phasing
- **Now:** this RFC + decisions.
- **Phase 2.5 (proposed):** Graph client + delegated-token acquisition + `create_workbook`
  (from-data) + the correlation spike (§6.4). Works pre-distribution with a manual pane open.
- **Phase 3:** seamless auto-open once the connector is installable from AppSource/admin deployment.
- **Later:** from-uploaded-file, upload sessions for large files, SharePoint, and — separately and
  only if justified — a guarded look at Flow 2.
