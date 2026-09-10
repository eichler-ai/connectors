# RFC: silent pane sign-in via Nested App Authentication (NAA)

Status: draft for review (2026-09-09). Owner: hub/Excel connector. Depends on: phase 1 (sign-in,
bridge tokens) done. Supersedes, for the common case, the pairing-code fallback (PRD §06 path 2,
issue #255); relates to #251 (account mismatch) and #250 (desktop). Gated on one live check (§7).

## 1. The problem

Today the pane authenticates with **path 1** (PRD §06): it opens the hub's `/bridge/authorize` in
an Office dialog / popup to run the Microsoft sign-in, then keeps the 90-day bridge token it hands
back. That popup is a hard dependency, and the pane already has to handle it failing — the browser
blocking it (`taskpane.js` reports Office dialog code `12011`, "Allow pop-ups…"), a dialog already
open (`12007`), or no dialog API at all (it falls back to a plain popup, which can also be blocked).
When the popup can't open — locked-down tenants, some mobile/WebView contexts, aggressive blockers —
the user is stuck with **no way to connect the pane**, and the symptom is exactly #251: "no bridge"
with no hint why.

The natural question: the user is *already signed in to Microsoft in the workbook* — why must the
pane sign in again? Because the task pane is a **cross-origin sandbox**: it is our content
(`taskpane.html` on `connectors.eichler.ai`) in an iframe (web) / WebView (desktop), and same-origin
policy walls it off from the host's Microsoft cookies and session. It genuinely cannot read the
workbook's credential. But Office provides a sanctioned bridge for exactly this, and as of 2026 the
modern one is **Nested App Authentication (NAA)**.

## 2. What NAA is (and the state of it, 2026)

NAA lets the task pane use **MSAL.js directly**, treating the add-in as a nested app the Office host
brokers. The pane calls `ssoSilent` and gets a token for the **already-signed-in Office user with no
popup**, falling back to a popup only on first consent / MFA. This is the "reuse the host's
identity" path. It replaces the older `getAccessToken` SSO, which Microsoft's own docs now label
**"legacy but still supported"** and steer new work away from.

Verified against Microsoft Learn (Office Add-ins docs, current as of the March–April 2026 revisions):

- **GA on Excel** — Web, Windows, Mac, iPad (Android n/a); "ready for production usage." *(NAA
  requirement-set page, ms.date 2026-03-31.)* An older cached "in preview" note exists and is
  **stale** — trust the newer requirement-set doc.
- **Personal Microsoft accounts are supported.** "NAA supports both Microsoft Accounts (MSA), such
  as personal outlook.com accounts, and Microsoft Entra ID (work/school) identities." (Not Azure AD
  B2C.) This was the make-or-break for our personal-OneDrive-first target, and on paper it is a yes.
- **Pane API:** `createNestablePublicClientApplication({ auth: { clientId, authority:
  ".../common" }})`, then for Excel **`ssoSilent(request)`** (the docs say `ssoSilent`, *not*
  `acquireTokenSilent`) with a **login hint** from `Office.auth.getAuthContext().userPrincipalName`
  (required in the browser), then **`acquireTokenPopup`** on `InteractionRequiredAuthError`. Gate on
  `Office.context.requirements.isSetSupported("NestedAppAuth", "1.1")` and keep a fallback.
- **Token shape:** the pane gets an access token **issued for our own Entra app's client id**,
  acquired silently for the Office user, usable directly as a Graph bearer from the pane. Scopes are
  **incremental/dynamic**; the request must carry ≥1 scope beyond `openid/profile/email/
  offline_access`. No server-side On-Behalf-Of exchange is needed.

## 3. End-to-end sequence

```
Pane (MSAL/NAA)              Office host        Hub (/bridge/naa)         Microsoft Entra        Registry
   │ isSetSupported(NAA)? ──> │                      │                          │                   │
   │ ssoSilent({loginHint, scopes:[openid…, <graph>]}) ───────────────────────>│  (silent, brokered)│
   │ <──────────────── id_token (aud = our client id) + access_token ──────────│                   │
   │  POST /excel/bridge/naa { id_token }  │        │                          │                   │
   │ ──────────────────────────────────────────────>│ verify id_token (JWKS,   │                   │
   │                                        │        │  iss, aud, exp) → oid    │                   │
   │                                        │        │ upsertUser(oid) → user_id│                   │
   │                                        │        │ PutBridgeToken{user,excel}│                  │
   │ <──────────── { token, user, expires_at } ──────│                          │                   │
   │  store in OfficeRuntime.storage; WebSocket hello(token) ─────────────────────────────────────>│
   │                                        │        │  registry keys under the SAME user_id as an  │
   │                                        │        │  MCP session for that Microsoft account (§07)│
```

The pane never opens a sign-in window in the common case; on `InteractionRequiredAuthError` it shows
one `acquireTokenPopup` (first consent / MFA only), then proceeds identically.

## 4. Pieces to build

### 4.1 Pane (excel/addin/taskpane.js)
- Add MSAL.js (pinned) and an NAA sign-in path alongside the existing popup path. On load: if
  `isSetSupported("NestedAppAuth","1.1")`, try `ssoSilent` with the Office login hint; on success,
  hand the resulting **id_token** to the hub (§4.2) and store the returned bridge token exactly as
  path 1 does. On `InteractionRequiredAuthError`, `acquireTokenPopup`. If NAA is unsupported, or any
  step fails, **fall back to the existing `/bridge/authorize` popup** unchanged — NAA is additive,
  never a regression.
- Keep **Sign out** and **Switch account** (§6). "Switch account" under NAA is an explicit
  `acquireTokenPopup({ prompt: "select_account" })`, since silent auto-adopts the Office user.

### 4.2 Hub: a token-in mint endpoint (hub/internal/authserver)
- New `POST /<slug>/bridge/naa` (unauthenticated like `/bridge/authorize`, since it *is* the
  authentication): body carries the pane's **id_token**. The handler validates it with the OIDC
  machinery already in `oidc.go` — `VerifyJWS` against the provider JWKS, issuer per-tenant, audience
  = our client id, expiry — then reads `oid`, runs the existing `upsertUser` seam (login.go:147) to
  resolve the **same `user_id`** an OAuth MCP session for that account carries, and mints a bridge
  token via `PutBridgeToken` (the `TODO(pair)` "the hook is minting" note in bridge.go). Returns the
  same `{token, user, expires_at}` shape the pane already consumes.
- **Refactor, small:** today `Exchange` (oidc.go) both redeems a code *and* validates the id_token.
  NAA presents an id_token directly, so factor the id-token validation + claims into a function both
  paths call. No new crypto, just a seam.
- **Nonce/replay:** the code flow binds the id_token with a hub-issued nonce; a directly-presented
  NAA token has none the hub can check. Mitigate with a **fresh-issued check** (small `iat` skew),
  TLS-only, and the fact that the token only mints a *revocable, `{user,connector}`-scoped* bridge
  token — never a session or an OAuth grant. Flag for security review; do not skip.

### 4.3 Entra app registration (nick@eichler.ai — client `4fce14f7-…`)
- Add an SPA redirect URI of the broker form **`brk-multihub://connectors.eichler.ai`** (origin
  only, no path). For **web** hosts, also add a plain-`https` SPA redirect for the task-pane page
  (`https://connectors.eichler.ai/excel/addin/taskpane.html`) — the docs require it or web token
  acquisition fails. Supported account types already include personal accounts. **No
  `WebApplicationInfo` manifest block is needed** (that belongs to legacy SSO). Per-environment: the
  staging/dev origins need their own redirects (or a separate registration) — the hub already
  rewrites the manifest origin per environment.

### 4.4 Scope boundary — NAA replaces pane sign-in, NOT the server-side Graph path
This is the crux and easy to get wrong. NAA's token lives **in the pane** and is short-lived. The
hub's `create_workbook` (and the planned headless edit, #252) must run with **no pane open**, using
the Microsoft **refresh token** the hub stores encrypted (rfc-graph-create-and-open.md §3.1). NAA
does not and cannot feed that. So:
- The one-time **MCP-session OAuth sign-in** (adding the connector to Claude) still runs the existing
  code flow and still captures the Graph refresh token — **unchanged**.
- NAA removes only the **pane's** extra popup. Both paths resolve to the same `user_id` (same `oid`),
  so nothing about routing or the stored Graph token changes.
- Net: one browser sign-in (the MCP session, which the user does anyway), and the pane is silent.

### 4.5 Interaction with #251 (account mismatch)
NAA silently signs the pane in as **whatever account the Office host is using** — usually right, but
if that differs from the MCP session's account you get the same silent mismatch, now with no popup to
consciously switch. So the `signed_in_as` / mismatch hint shipped in #268 stays load-bearing, and the
pane keeps an explicit **Switch account** (`prompt: "select_account"`) so the user can override the
auto-adopted account.

## 5. What this is NOT
- Not a removal of the popup path — it stays as the fallback (non-NAA hosts, Google sign-in, and
  `InteractionRequiredAuthError`).
- Not a change to server-side Graph / `create_workbook` token acquisition (§4.4).
- Not Google — NAA is Microsoft-only; Google users keep path 1.
- Not the pairing code (#255) — NAA makes pairing a rarely-needed last resort, not the primary
  fallback. Whether to build pairing at all can wait until NAA's coverage is known.

## 6. Security & privacy
- The pane obtains a token for **our own** Entra app for the signed-in Office user; the hub validates
  the id_token exactly as it validates the login id_token today (signature/issuer/audience/expiry).
- The mint endpoint issues only a **bridge token** — opaque, hashed at rest, `{user,connector}`-
  scoped, 90 days, revocable, and covered by `RevokeUser`. No new long-lived secret enters the pane.
- Id_token replay is the one new surface (§4.2); mitigations there, and it is a security-review gate.
- No tokens logged, per the standing rule. Third-party-cookie blocking (Chrome, strict-privacy
  browsers) can make even silent add-in auth prompt more; plan for the popup fallback firing there.

## 7. Decisions (proposed) and the gating unknown
**Proposed decisions (for review):**
1. **Adopt NAA as the primary pane sign-in** on supported hosts; keep the `/bridge/authorize` popup
   as the fallback. (§4.1)
2. **Hub consumes the id_token** and mints a bridge token through `upsertUser` + `PutBridgeToken`;
   NAA does not touch the server-side Graph refresh-token path. (§4.2, §4.4)
3. **Pairing code (#255) deferred** — NAA covers the UX problem it was for; revisit only if NAA
   coverage proves too narrow.

**The gating unknown — one live check, before any build.** NAA on the **web** is documented to work
only for workbooks opened from **SharePoint Online / OneDrive**, and the docs do **not** explicitly
confirm that *consumer / personal* OneDrive with an **MSA** satisfies that gate — which is precisely
our v1 target. Everything above depends on this. It is cheap to settle:

> **Spike:** register the two Entra redirects (§4.3) for one origin; drop a minimal `ssoSilent` call
> in the pane (or a throwaway page) behind the NAA capability check; open a workbook from a
> **personal** OneDrive in **Excel for the web**, signed in with an **MSA**; observe whether a token
> comes back **silently** (vs `InteractionRequiredAuthError`, vs an outright NAA-unsupported error),
> and whether a `Files.ReadWrite` scope is granted. Repeat once in desktop Excel (#250) for coverage.

Pass → proceed to §4. `InteractionRequired`-but-works → proceed, accept a one-time consent popup.
Hard fail on consumer OneDrive → NAA is work/school-only for us today; keep the popup, and the
pairing code (#255) comes back onto the table for the personal-account case.

## 8. Phasing
- **Now:** this RFC.
- **Next (cheap, gating):** the §7 live spike. Needs the Entra change + a sideload + a person to
  observe; it decides everything.
- **If it passes:** §4.2 hub mint endpoint + id-token validation refactor (unit-testable in full,
  the way #241/#268 were), then §4.1 pane NAA with the popup fallback intact, then live-verify the
  end-to-end silent sign-in and the same-`user_id` join. Deploy to staging; prod after live check.
- **Later / separate:** Google silent sign-in (its own mechanism, out of scope here); revisiting
  #255 only if NAA coverage is narrow; folding NAA into the desktop matrix (#250).
