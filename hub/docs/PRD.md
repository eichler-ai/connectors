# Connectors Platform

**Product & Technical Design — Draft for review (2026-09-07)**

One hosted hub at `connectors.eichler.ai` that many connectors plug into — Excel first, then Figma,
Google Sheets, Rhino, and Revit's remote mode — plus the per-connector pieces each host application
needs. This document is the platform design; each connector gets its own chapter or PRD that refers
back here. It replaces the "what's next" of the Excel proof of concept (`excel/poc/`), which is
retired as of this draft and kept only as a reference for the live findings recorded in its README.

- **Status:** proposal. Nothing in production; no users but us.
- **Host:** `connectors.eichler.ai` (Cloud Run, project `eichler-ai`), chosen over `mcp.` because
  hostnames outlive protocols and appear in user-facing URLs.
- **Auth:** self-hosted OAuth 2.1 authorization server at the host root; login delegated to
  Microsoft and Google. WorkOS/Auth0 evaluated and deferred (§06).
- **First connector:** Excel (web first, desktop later). Local-mode hybrid is a v1.1 option (§04).

## Contents

1. [Summary & goals](#01-summary--goals)
2. [Non-goals for v1](#02-non-goals-for-v1)
3. [What the proof of concept established](#03-what-the-proof-of-concept-established)
4. [Architecture](#04-architecture)
5. [URL & identity layout](#05-url--identity-layout)
6. [Authentication & authorization](#06-authentication--authorization)
7. [Session registry & routing](#07-session-registry--routing)
8. [Bridge protocol](#08-bridge-protocol)
9. [Connector interface](#09-connector-interface)
10. [Excel connector v1](#10-excel-connector-v1)
11. [Persistence, files & secrets](#11-persistence-files--secrets)
12. [Observability & audit](#12-observability--audit)
13. [Security model](#13-security-model)
14. [Distribution & updates](#14-distribution--updates)
15. [Operations](#15-operations)
16. [Repository layout](#16-repository-layout)
17. [Roadmap](#17-roadmap)
18. [Decisions needed](#18-decisions-needed)

---

## 01. Summary & goals

The Revit connector proved the shape: a **Bridge** inside the host application executes agent-written
scripts, a **Server** speaks MCP to Claude, and discovery tools plus a how-to corpus make generated
scripts good on the first try. The Excel proof of concept showed the same shape works for a browser
add-in with no code on the user's machine at all, provided the server is hosted.

The platform generalises that. Goals for v1:

- **One sign-in, many connectors.** A user authorises "Eichler Connectors" in Claude once and every
  connector they have installed works. One issuer, one identity record, one place to revoke.
- **Zero-install for browser-hosted apps.** Excel, Sheets and Figma extensions come from their own
  stores and talk to the hub; nothing runs on the user's machine.
- **Same core for desktop apps.** Revit and Rhino keep their local stdio mode and gain a remote mode
  by speaking the platform's bridge protocol to the hub — the "remote hub" the Revit transport RFC
  (`revit/docs/http-transport-rfc.md` §4b) already earmarked.
- **Shared services built once:** authorization server, session registry, bridge protocol, file
  exchange, audit trail, how-to corpus store, discovery tooling, update feed.
- **Keep the Revit engineering standards.** Observability over silence, the diagnostic-record shape,
  live tests against the real host, bounded buffers, the acting identity travels with the action
  (`CONVENTIONS.md`).

## 02. Non-goals for v1

- Multi-instance scaling of the hub. One always-on Cloud Run instance serves the first connectors
  (§15); the registry is written so adding Pub/Sub routing later is a change inside one package.
- Tenant SSO (SAML/SCIM), admin portals, per-seat billing. Design the identity record so these can
  be added; do not build them.
- Undo for Excel. Office.js edits are not reliably undoable; range snapshots are a later feature.
- Running scripts anywhere but inside the host app. The hub relays; it never interprets a script.
- Any connector other than Excel shipping in v1. Figma is the intended second (§17).

## 03. What the proof of concept established

Live against Excel for the web in Chrome, 2026-09-07 (details and scripts in `excel/poc/README.md`):

| finding | consequence for this design |
|---|---|
| A task pane inside the Office iframe opens `wss://` to a bridge with no browser prompt, both to localhost and to `connectors.eichler.ai` | The hosted topology works; the same page works locally |
| An https page inside Office can also open a **plain** `ws://localhost` socket in Chrome | The local hybrid (hosted add-in, local server, no certificate) is viable — §04 |
| Scripts round-trip in ~80 ms locally; a 10k-cell read is ~260 ms; whole-document PDF export ~9 s | Default timeouts of 30 s with cooperative deadlines are right; exports need out-of-band files |
| Office.js errors carry `code`, the failing statement and its neighbours | Self-correction loops are cheap; surface errors verbatim |
| A hung script blocks the pane until it ends; nothing outside can interrupt it | Timeouts plus a "reload the pane" notice; no hard cancellation promised |
| Excel keeps a "closed" pane alive; Script Lab ran two runner instances | Newest-connection-wins with an explicit `replaced` notice to the loser |
| `golang.org/x/net/websocket` returns one frame per receive; Chrome fragments large messages | Stream-decode JSON on the socket (or use a full WebSocket library) |
| Formatting, charts, pivots, CSV/xlsx/PDF export, `insertWorksheetsFromBase64` all work, written from model knowledge with at most one correction | The Excel tool surface is `execute_script` plus a skill file; no discovery tools in v1 |
| The first write went into the user's real workbook | Target workbook/sheet must be surfaced before any write (§10) |
| Store validation, Script Lab hosting and Cloud Run domain mapping all exercised | Distribution path is known (§14) |

## 04. Architecture

```
                 Claude (Desktop / Code / Cowork / claude.ai)
                                 │  MCP over Streamable HTTP + OAuth
                                 ▼
   ┌──────────────────────── connectors.eichler.ai ─────────────────────────┐
   │  auth server   session registry   file exchange   audit   corpus/docs  │  platform
   │  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐          │
   │  │ /excel  │ │ /figma  │ │ /sheets │ │ /rhino  │ │ /revit  │  …       │  connectors
   │  └────┬────┘ └────┬────┘ └────┬────┘ └────┬────┘ └────┬────┘          │
   └───────┼───────────┼───────────┼───────────┼───────────┼───────────────┘
           │ wss       │ wss       │ wss/API   │ wss       │ wss (remote mode)
     Excel add-in  Figma plugin  Sheets add-in  Rhino plugin  Revit add-in
```

**Two families of connector, one hub.**

- *Browser-hosted* (Excel, Google Sheets, Figma): the extension is a sandboxed web page; it can dial
  out over WebSocket and cannot run a local process. The hub is the only server they ever talk to.
  The Excel POC's `taskpane.js` is the template for all three.
- *Desktop-native* (Revit, Rhino): a real plugin on a machine. Local mode is the existing stdio
  design (Revit PRD §05) and stays the default; remote mode dials the hub with the same bridge
  protocol. The hub does not distinguish the families.

**Bridge is a capability, not an assumption.** Some connectors do part of their work without a live
bridge — Sheets can read/write through Google's API with a stored token; Figma has a REST API. A
connector declares whether it has a bridge, an API path, or both (§09).

**Topologies for a browser-hosted connector.** Both use the identical extension; the extension tries
them in order and reports which it is on.

| | Hosted (v1) | Local hybrid (v1.1, optional) |
|---|---|---|
| Server | the hub | a stdio server Claude spawns, listening on `ws://localhost:PORT` |
| Add-in | from the store, page served by the hub | same |
| Identity | OAuth sign-in (§06) | same-user-local trust, as Revit |
| Works from claude.ai / Cowork cloud | yes | no |
| Data leaves the machine | yes (relayed, not stored) | no |
| One-time setup | connect in Claude, add the add-in | run installer, add the add-in |
| Per session | ribbon click | ribbon click |
| Known risks | ops, transit | WebKit (Excel for Mac) may block plain `ws://localhost`; the Revit singleton machinery is required |

## 05. URL & identity layout

The issuer URL and the path layout are the two things that are painful to change once tokens and
manifests exist, so they are fixed here.

| path | serves |
|---|---|
| `/.well-known/oauth-authorization-server` | authorization-server metadata (issuer = `https://connectors.eichler.ai`) |
| `/oauth/authorize`, `/oauth/token`, `/oauth/register`, `/oauth/jwks` | the authorization server (§06) |
| `/login/…` | sign-in pages and the Microsoft/Google OIDC callbacks |
| `/<connector>/mcp` | that connector's MCP endpoint (Streamable HTTP) |
| `/<connector>/.well-known/oauth-protected-resource` | resource metadata pointing at the issuer |
| `/<connector>/bridge` | WebSocket the extension/plugin dials |
| `/<connector>/addin/…`, `/<connector>/manifest.xml` | static extension files where the app loads them from a URL |
| `/files/…` | signed-URL redirects into the file store (§11) |
| `/healthz` | liveness |

Naming follows `CONVENTIONS.md`: in Claude's client config a connector is its lowercase slug
(`excel`); user-facing text says "MCP Server" and "MCP Bridge", never "hub" or "broker". "Hub" is
the name of the hosted service in this document, in code and in the repo (`hub/`); users see it
only as "Eichler Connectors".

## 06. Authentication & authorization

**Decision: self-hosted authorization server, login delegated to Microsoft and Google.**

Why not WorkOS/Auth0 (evaluated 2026-09-07): both work and WorkOS has first-class MCP support, but
per-tenant SSO connections cost $65–125/month each, the custom sign-in domain is a $99/month add-on,
and the enforcing half — token verification, session binding, pane pairing — is ours either way.
Owning the issuing half is a few hundred lines against a well-specified surface. Revisit if
enterprise SSO/SCIM demand arrives before we want to build it (§18).

**Authorization server**

- OAuth 2.1: authorization code + PKCE (S256 only), refresh tokens with rotation and reuse
  detection, exact redirect-URI matching, resource indicator enforced.
- Client registration: dynamic client registration (RFC 7591) *and* Client ID Metadata Document,
  which the MCP spec added in November 2025 and Claude clients are adopting. Registration is
  rate-limited and registrations that never complete an authorization are garbage-collected.
- Access tokens: short-lived signed JWTs (15 min), one audience for the whole platform, scopes per
  connector (`excel`, `figma`, …) granted from what the user has installed. One Claude
  authorization therefore covers every connector.
- Login: no passwords. `/login` offers Microsoft (personal + work via a multi-tenant Entra app) and
  Google (OIDC). The identity record is `{user_id, provider, subject, email, display_name}`; the
  same record is what the extension signs in to (§07).
- Implementation: `ory/fosite` for the protocol mechanics; `modelcontextprotocol/go-sdk`'s
  `auth.RequireBearerToken` and `ProtectedResourceMetadataHandler` on the MCP side (already the
  SDK the Revit server uses). Signing key in Secret Manager, rotated with `kid`s.

**Extension sign-in (pairing).** The extension needs to belong to a user so the registry can route
that user's MCP requests to it. Two supported paths, in this order:

1. Sign-in in the pane with the same Microsoft/Google login → a long-lived, revocable *bridge
   token* scoped to `{user, connector}` stored by the extension (Office `OfficeRuntime.storage`,
   Figma `clientStorage`). Zero extra steps for a user already signed in to the provider.
2. Pairing code: the pane shows a 6-character code; the user pastes it to Claude, which calls the
   `pair` tool. For hosts where an OAuth popup is awkward.

Bridge tokens are presented in the WebSocket `hello`; the socket is refused otherwise.

## 07. Session registry & routing

Generalises Revit's `list_instances` (Revit PRD §05).

- A **bridge connection** is `{user_id, connector, instance_id, documents[], versions, since}`.
  `instance_id` is stable per extension runtime (per pane load in Excel, per plugin process in
  Revit); `documents[]` is live — the extension pushes a fresh `register` on workbook/document
  change, as the Revit add-in does now.
- An **MCP session** is `{user_id, connector, client}` and addresses bridges by `instance_id` (and
  `document_id` where the host has several). With one bridge for the user+connector, tools default to
  it; with several, `list_instances` disambiguates, as Revit does.
- **Newest wins, with notice.** A new bridge for the same `{user, connector, instance_id}` replaces
  the old one; the loser receives `replaced` and stops reconnecting (POC finding).
- **v1 is in-process.** Registry, pending execs and sockets live in the single hub instance;
  persisted mirror in Firestore for restart visibility. The routing interface (`Send(bridge, msg)`)
  is the seam where a Pub/Sub or Redis fan-out slots in when a second instance is needed.
- Cloud Run closes a WebSocket after 60 minutes; the extension reconnects with the same
  `instance_id` and the registry treats it as a replacement, so in-flight execs fail with a clear
  `bridge-reconnected` notice rather than hanging.

## 08. Bridge protocol

One schema for every connector, converging the Revit NDJSON JSON-RPC protocol and the POC messages.
JSON-RPC 2.0 over WebSocket text frames (hub) or NDJSON over TCP (Revit local mode); the
messages are the same either way.

| method | direction | purpose |
|---|---|---|
| `hello` | bridge → hub | `{connector, protocol_version, bridge_version, instance_id, host: {app, platform, version}, documents[], token}` — first message, must precede all others |
| `register` | bridge → hub | live `documents[]` update, same shape as in `hello` |
| `exec` | hub → bridge | `{id, document_id?, script, language, timeout_ms, limits: {result_bytes}}` |
| `result` | bridge → hub | `{id, ok, result?, error?: {name, message, code?, debug_info?, stack?}, duration_ms, truncated?, notices[]}` |
| `cancel` | hub → bridge | best-effort; bridges that cannot interrupt reply with a `cannot-cancel` notice |
| `notice` | either | out-of-band diagnostic record (§12) |
| `replaced` | hub → bridge | this connection was superseded; do not reconnect |
| `ping`/`pong` | either | liveness under proxies |

`protocol_version` is an integer; the hub supports the current and previous version and reports
`bridge-outdated` in tool results when a bridge is older than that, with the connector's update
path (§14). Scripts are opaque to the hub: `language` is a connector-defined tag (`officejs`,
`csharp`, `python`, …).

## 09. Connector interface

A connector is a Go package registered with the hub at build time (single binary, §16):

```go
type Connector interface {
    Slug() string                                  // "excel"
    Capabilities() Capabilities                    // Bridge, API, or both; supported languages
    Tools(reg *ToolRegistry)                       // MCP tools; most wrap platform.Exec
    Static() fs.FS                                 // manifest + extension files, may be nil
    Skill() []byte                                 // skill file served by get_skills
    Docs() DiscoverySource                         // optional; nil when a connector has no discovery tools
    Validate(ctx, script Script) error             // optional pre-flight (size, denylist)
}
```

The hub supplies `Exec(ctx, user, target, script) (Result, error)`, `Files`, `Audit`,
`Registry`, `get_skills`, and — for connectors that opt in — the discovery/how-to tools generically;
a connector contributes content and any host-specific tools (Excel's `get_status`, Revit's
transaction controls).

## 10. Excel connector v1

- **Tools:** `execute_script` (body of an `async (context)` function, JSON result),
  `get_skills` (returns the connector's skill file — see below), `list_instances`, `get_status`
  (workbook, sheet, selection, host, API set), `export_file` (csv/xlsx/pdf via the file exchange),
  `import_workbook` (sheets from an uploaded xlsx), `pair`.
- **No discovery tools in v1.** Office.js is well represented in model training and the POC's
  scripts were written from that knowledge with a one-error correction loop at most, so
  `describe_function`/`search_functions` and the how-to corpus are deferred (§17 phase 5) rather
  than ported from Revit. What the model does *not* reliably know is this connector's contract
  and the host's quirks, and that is what the skill file carries.
- **Skill file (`get_skills`).** A markdown document, versioned with the connector and served from
  the hub, that the agent fetches once per session: the script contract (`context` in, JSON out,
  return values not proxies), result and timeout limits, how targets are named and the safety
  notices in the next bullet, the file-exchange flow, and the live-verified host quirks the POC
  found (no multi-area addresses in `getRange`/`charts.add`, `getMergedAreas` reports the anchor
  cell on the web, `getCellProperties` border naming, no-fill reads back as `""`, hung scripts
  cannot be interrupted, `getFileAsync(Pdf)` works on the web). Same token-budget discipline as the
  Revit skill (`skill.md`): raise the budget deliberately as features land; never trim silently.
- **Safety:** every write-capable result reports `{workbook, sheet, address}` it touched; scripts
  that do not name a target sheet get a `target-implicit` notice; `execute_script` accepts an
  optional `expect: {workbook, sheet}` that fails fast on mismatch. This is the fix for the POC's
  overwritten `inputs` sheet.
- **Runtime:** the POC runner as is — `new Function` inside `Excel.run`, 16 MiB result cap with
  truncation flag, cooperative deadline, errors verbatim. Shared runtime in the manifest so the
  add-in survives a closed pane and can reconnect on workbook open.
- **Reference material for the skill file:** the POC's 14 scripts (formatting, charts/pivot,
  exports, upload, UI control) become worked examples linked from the skill, not a searchable
  corpus.
- **Files:** exports go bridge → hub → Cloud Storage; the tool returns a signed URL and, for MCP
  clients that can receive files, the content. Uploads reverse it. Nothing base64 in a script.
- **Manifest:** served by the hub with its stable Id; Store submission per §14. Excel desktop
  (Windows WebView2, Mac WebKit) is exercised in the live test matrix before the Store listing
  declares it.

## 11. Persistence, files & secrets

- **Firestore** (no instance to run, cheap at this volume): `users`, `oauth_clients`,
  `auth_codes` (TTL), `refresh_tokens`, `bridge_tokens`, `bridges` (registry mirror), `audit`
  (per user, bounded retention), `howtos`.
- **Cloud Storage:** `files/{user_id}/{connector}/{id}` with short-lived signed URLs; lifecycle rule
  deletes after 7 days.
- **Secret Manager:** JWT signing keys, Microsoft/Google client secrets. No secrets in env vars
  (the POC's `BRIDGE_TOKEN` env var is a POC shortcut, not a pattern).

## 12. Observability & audit

Adopt the Revit diagnostic record unchanged (Revit PRD §01: `{severity, code, source, message,
…}`) for `notices[]` on results, error `data`, and log lines. Every `exec` writes an audit row:
`{user, connector, instance, document, script_hash, script (bounded), ok, code, duration,
result_bytes, client}`. The hub never logs tokens or script results. Cloud Logging + an uptime
check on `/healthz` + alert on 5xx rate and on "no bridge connected for N minutes while execs
requested".

## 13. Security model

The asset is *running arbitrary code inside a user's document*. Threat model, one page, to be
written and reviewed before the first external user; the shape:

- **Isolation between users** is the property that matters most. Every exec is routed by the
  `user_id` from the verified token to a bridge that presented a bridge token for the *same*
  `user_id`. There is no path from a token to another user's bridge. Session binding in the MCP
  transport (SDK `TokenInfo.UserID`) prevents session hijack.
- **Transport:** TLS everywhere (Cloud Run managed); WebSocket `Origin` allow-list per connector
  (the extension's origin, the store's runtime origins); bridge token required in `hello`.
- **Authorization server hardening** checklist: PKCE S256 only, exact redirect match, state/nonce,
  resource indicator, refresh rotation + reuse detection, DCR rate limit and GC, key rotation,
  no tokens in logs, cookie flags, CORS closed by default. External review before launch.
- **Scripts** run with the extension's full reach, including network from the page. Documented as a
  property, not a bug; the audit trail is the compensating control. A stricter sandbox (worker
  without network) is a later option that costs direct `Excel.run` access.
- **Kill switches:** disable registration, revoke all tokens for a user or globally, drain a
  connector — each one command.
- **Hub holds no document content at rest** except audit-bounded script text and files a user
  explicitly exported/uploaded, with 7-day lifecycle.

## 14. Distribution & updates

| piece | distributed by | updated by |
|---|---|---|
| Hub + connector packages | Cloud Run deploy from `main` | every deploy; no user action |
| Extension page/code | served by the hub | every deploy; no user action |
| Extension manifest | AppSource (Partner Center) and/or M365 admin-center Integrated Apps; Figma Community; Google Workspace Marketplace | store review on change — keep manifests minimal and stable |
| Desktop plugins (Revit, Rhino) | existing installers + self-update (`update_connector`) | unchanged |
| Local hybrid server (v1.1) | one-line installer / `.mcpb` bundle for Claude Desktop | self-update as Revit; page loads its client from `http://localhost` so client and server never skew |

Sideloading a manifest URL served by the hub is the developer and early-customer path until store
listings exist. Start Partner Center registration in phase 1 (§17); it has waiting time and the
review may question an add-in that executes code received at runtime — Script Lab is the
precedent, and the listing must say plainly that scripts come from the user's own AI session.

## 15. Operations

- Cloud Run, `us-central1`, **one instance, min = max = 1**, instance-based billing (long-lived
  sockets), request timeout 60 min, session affinity on. Estimated < $40/month idle.
- Deploy: Cloud Build from the repo on merge to `main`; Dockerfile = static Go binary in
  distroless (as the POC). Staging service `connectors-staging.eichler.ai` for the live test matrix.
- DNS in Cloud DNS (`eichler-ai` zone); domain verified; certificates managed by Cloud Run.
- Scale trigger: move to N instances when connected bridges exceed a few thousand or a deploy
  blip becomes unacceptable; requires the registry fan-out (§07) and sticky routing.

## 16. Repository layout

```
hub/                    Go module: the hosted service — auth server, registry, protocol, files, audit, corpus
  cmd/hub/
  internal/{auth,registry,bridge,files,audit,corpus,discovery}/
  docs/PRD.md           this document
  (packages the local stdio servers also need — protocol, diagnostics — stay importable from here;
   a separate shared-library directory only if one genuinely emerges)
excel/
  addin/                manifest + task pane (store artefact)
  connector/            Go package implementing hub.Connector
  docs/                 Excel chapter, live test matrix
figma/ sheets/ rhino/   same shape, later
revit/                  unchanged; gains connector/ for remote mode when scheduled
CONVENTIONS.md          extended with the platform vocabulary above
```

One binary links every connector. Splitting into services behind a router later is a routing change
the path layout already permits.

## 17. Roadmap

| phase | scope | done when |
|---|---|---|
| 0 — Foundations (1–2 wks) | `platform/` skeleton, bridge protocol v1, registry, Firestore, Cloud Run + staging, CI | POC add-in pointed at `/excel/bridge` runs a script via a temporary token-authed tool |
| 1 — Auth (2 wks) | authorization server, Microsoft + Google login, MCP endpoint with `RequireBearerToken`, pairing via pane sign-in | Claude Desktop/Code/claude.ai add "Eichler Connectors", sign in, run `execute_script` in Excel for the web; Partner Center registration started |
| 2 — Excel v1 (2 wks) | tools in §10, skill file, safety notices, file exchange, audit; shared-runtime manifest | live test matrix green on Excel web (Chrome, Edge) and desktop (Win, Mac); threat model written; external review scheduled |
| 3 — Distribution (calendar-bound) | AppSource submission, admin-deployment guide, docs site page, status page | first external user installed without our help |
| 4 — Second connector (Figma) | prove the connector interface; plugin UI dials `/figma/bridge` | Figma plugin runs generated scripts through the same sign-in |
| 5 — Options | local hybrid for Excel (v1.1), Revit remote mode, Sheets API path, multi-instance hub, Excel discovery tools + how-to corpus if skill-file guidance proves insufficient | as demand dictates |

## 18. Decisions needed

1. **Identity providers at launch.** Microsoft + Google as proposed, or Microsoft only for Excel v1?
2. **Pairing default.** Pane sign-in (recommended) vs pairing code first.
3. **Scopes.** One audience with per-connector scopes (recommended) vs per-connector audiences.
4. **Excel desktop in v1.** Declare Mac/Windows desktop in the first Store listing, or web only
   until the desktop live tests pass?
5. **Local hybrid.** Keep as v1.1 option (recommended) or drop until a customer needs offline/local.
6. **Staging environment** as a second Cloud Run service now, or after phase 1.
7. **Revit remote mode** scheduling — phase 5 as written, or pulled forward to validate the
   desktop-family path earlier.
8. **Shared bridge token lifetime** (proposed 90 days, revocable) and whether extensions must
   re-authenticate on a new machine.
