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

**Scope decision (2026-09-08): broad file access.** With `Files.ReadWrite` over the user's whole
OneDrive (§9), the API path does more than create-and-open: Claude can also **open an existing**
OneDrive workbook (take its URL, hand it back to open, then drive it) and **read/edit existing
files headlessly** via Graph when nothing is open. The create-and-open story below is the primary
one; the existing-file capabilities come with the same permission and are specced as §3.5.

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
  - **Upfront at sign-in (chosen 2026-09-08).** The Microsoft login requests
    `openid profile email offline_access Files.ReadWrite` from the start, so one consent covers
    identity and file access and there is no second prompt later. The Microsoft refresh token from
    that login is stored (§3.1, encrypted) and reused for Graph. Consequence to accept: the consent
    screen names broad file access at first sign-in, and an **unverified** publisher with this scope
    triggers the strongest warning and is blocked outright in many work tenants — so this scope must
    not reach work tenants before publisher verification (Partner Center, already in progress). Fine
    for the personal-account v1 target.
  - **Incremental (not chosen).** Would keep sign-in minimal and prompt for files only on first use.
    Recorded as the fallback if the upfront consent screen proves too heavy in practice.
- **Storing the Microsoft refresh token.** Sensitive. Encrypt at rest (AEAD with a key from Secret
  Manager, distinct from the JWT signing key), keyed by `{user_id, provider}`. Never logged. A new
  store collection `graph_tokens` (hash/ciphertext only). Revoked by `RevokeUser` alongside bridge
  tokens.

### 3.2 The `create_workbook` tool (MCP)
- Input: a content source and a name. **All three sources are in v1 (chosen 2026-09-08)**; build in
  this order:
  1. **From data** — the tool builds a valid `.xlsx` server-side from rows/sheets the agent supplies
     (the common case: Claude generated a table and wants it in Excel). Keeps bytes off the wire and
     avoids the agent hand-building xlsx (which, as we found live, Excel's importer rejects if
     malformed). Build first.
  2. **From an uploaded file** — the agent puts an `.xlsx` into the hub file exchange (phase 2,
     GCS); the hub streams those bytes to Graph. No base64 in the tool call. Depends on the phase-2
     file exchange, so it lands with or after that.
  3. **Blank** — a new empty workbook. Trivial once the upload path exists.
- Action: upload to a chosen path in the user's OneDrive via Graph
  (`PUT /me/drive/root:/<folder>/<name>.xlsx:/content`; files >4 MiB use an upload session). With the
  broad `Files.ReadWrite` scope (§9) the target can be any folder, defaulting to a visible
  `Eichler Connectors/` folder rather than a hidden app folder.
- Output: `{ webUrl, doc_key, open_hint }` where `webUrl` is what the user clicks, `doc_key` is the
  stable identifier the agent will match against `list_instances` once the pane connects, and
  `open_hint` is human text ("Open this and I'll take the controls").
- The file lands in the user's own OneDrive, owned and visible to them.

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

### 3.5 Existing-file capabilities (unlocked by the broad scope)
With `Files.ReadWrite` over the whole OneDrive, two more tools become possible on the same
plumbing; specced here, scheduled after create-and-open:
- **`open_workbook`** — given a file the user names (by Graph search or a URL they paste), return its
  `webUrl` + `doc_key` so the user can open it and the bridge can then drive it. Same correlation
  and auto-open story as §3.3–§3.4.
- **`edit_workbook_headless`** — read/edit an existing OneDrive workbook via the Graph Excel API
  with **nothing open** (ranges, tables, formulas, recalculation). This is the headless at-rest path
  from the earlier discussion. It must refuse or warn when the file is currently open interactively
  (Graph vs live co-authoring do not compose — the Flow 2 hazard), e.g. detect the lock and return a
  `document-open-elsewhere` notice rather than risk a conflicting write.

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
- **Scope is `Files.ReadWrite` over the whole OneDrive (chosen §9.2)** — the connector can read and
  write any of the user's workbooks, not just ones it created. This is a broad grant; the
  compensating controls are the audit trail (PRD §12, every exec and every Graph write logged with
  user/file/outcome), `RevokeUser` cutting Graph access with everything else, and — before this
  reaches work tenants — publisher verification so the consent screen is trustworthy and not blocked.
- Upfront consent at sign-in (§3.1); the Microsoft consent screen names the file access.
- Microsoft refresh tokens encrypted at rest, never logged, revoked with the user.
- Created/edited files are in the *user's* OneDrive, owned by them, not in hub storage.
- Personal Microsoft accounts have OneDrive; work/school accounts need a OneDrive/SharePoint
  license. Behaviour differs — verify per account type.

## 6. Decisions (resolved 2026-09-08)
1. **Consent model:** upfront at sign-in (§3.1). Incremental kept as the fallback.
2. **File scope:** `Files.ReadWrite` — all the user's files (§5, §3.5).
3. **Content sources for v1:** all three (from-data first, then from-uploaded-file, then blank; §3.2).
4. **Account type for v1:** personal OneDrive first; work/SharePoint later (needs verification + more
   surface).

**Still open — one technical unknown, not a preference:** the correlation join (§3.3). Needs a live
check: what exactly does the web pane report as `Office.context.document.url` for a Graph-created
OneDrive file, and can the hub compute the same key from the driveItem `id`/`webUrl`? Proposed
spike: create a file via Graph, open it, read `document.url` in the pane, compare. This gates
"then Claude controls *this* file" being exact rather than best-guess.

## 7. Phasing
- **Now:** this RFC + decisions.
- **Phase 2.5 (proposed):** the correlation spike (§6) *first* — it is cheap and de-risks the rest —
  then Graph client + upfront `Files.ReadWrite` token acquisition and storage + `create_workbook`
  (from-data). Works pre-distribution with a manual pane open.
- **Phase 3:** seamless auto-open once the connector is installable from AppSource/admin deployment;
  publisher verification lands here too, which is the gate for the broad scope reaching work tenants.
- **Later:** create_workbook from-uploaded-file + blank, `open_workbook` and
  `edit_workbook_headless` (§3.5), upload sessions for large files, SharePoint, and — separately and
  only if justified — a guarded look at Flow 2.
