# Connectors Hub

The hosted service connectors plug into — design in [`docs/PRD.md`](docs/PRD.md). One Go binary
serves, per connector, an MCP endpoint (`/<slug>/mcp`), the WebSocket the host application's
extension dials (`/<slug>/bridge`), and the extension's own files (`/<slug>/addin/…`,
`/<slug>/manifest.xml`). Excel is the first connector (`../excel/`).

Phase 0 built the bridge protocol, the in-process registry and Cloud Run deployment; phase 1
(this state) adds the authorization server: Claude clients sign in with Microsoft and call the MCP
endpoints with a JWT (see "Auth" below). The pane still uses a shared dev token in its `hello`
until pane sign-in lands. File exchange and audit follow.

## Run locally (`-dev`)

From the repository root (the Go module lives there and links `hub/` and every connector):

```sh
export HUB_DEV_TOKEN="$(openssl rand -hex 16)"   # 16+ characters; the pane's bridge token
export HUB_MS_CLIENT_ID=4fce14f7-6415-4d92-a8b0-c98f7f0a1763
export HUB_MS_CLIENT_SECRET="$(gcloud secrets versions access latest --secret=entra-client-secret --project=eichler-ai)"
go run ./hub/cmd/hub -dev
```

That serves `https://localhost:8443` with a self-signed certificate written to
`~/Library/Application Support/Connectors/Hub/` (or the platform equivalent). The browser has to
trust it once — `go run ./hub/cmd/hub -trust-cert` prints the command — then restart the browser and
check `https://localhost:8443/excel/addin/taskpane.html` loads without a warning. Text logs go to
stderr; they carry ids and outcomes, never scripts, results or tokens.

Environment: `PORT` (default 8443 in `-dev`, 8080 otherwise), `HUB_PUBLIC_URL` (external origin,
rewritten into served manifests and used as the OAuth issuer; defaults to the local one),
`HUB_ALLOWED_ORIGINS` (extra browser origins for the bridge socket, comma-separated),
`HUB_DEV_TOKEN` (required; the bridge token), `HUB_ENV` (`prod`, `staging` or `dev`; default
`dev`, and `-dev` always forces `dev` regardless of this variable), and the auth settings listed
under "Auth": `HUB_JWT_SIGNING_KEY`, `HUB_MS_CLIENT_ID`, `HUB_MS_CLIENT_SECRET`,
`HUB_FIRESTORE_PROJECT`, `HUB_FIRESTORE_DATABASE`. Without `HUB_MS_CLIENT_ID` the hub starts, but
nobody can sign in; without `HUB_FIRESTORE_PROJECT` everything is in memory and a restart signs
everyone out (fine for `-dev`, never for a deployment).

## Sideload the add-in in Excel for the web

1. With the hub running, download `https://localhost:8443/excel/manifest.xml`.
2. Open a workbook in Excel for the web (OneDrive or SharePoint). **Home → Add-ins → More Add-ins →
   My Add-ins → Upload My Add-in**, choose the manifest. If the upload option is missing, tenant
   policy blocks sideloading; a personal Microsoft account allows it.
3. An **MCP Bridge** group appears on the Home tab. Click **Open MCP Bridge**, paste the value of
   `HUB_DEV_TOKEN` into the token field and click **Save**. The pane should say *Connected*.

The pane keeps the token in its own origin's `localStorage`; pane sign-in (the next unit)
replaces the field. If the pane loads but never connects, inspect it (right-click → Inspect) — the
close reason names the cause (`token rejected`, `bridge-outdated`, …).

**Until the pane signs in**, the dev token resolves to a fixed user (`dev-<hash>`), while an MCP
session is the Microsoft user who signed in — two different `user_id`s, so `list_instances` from
an OAuth session does not see a dev-token pane. Running a script end to end therefore waits for
pane sign-in; the bridge-side tests cover that path with one identity on both ends. What *is*
live-testable now is the whole sign-in and token flow plus `get_skills` and `list_instances`
(which need no bridge).

## Connect an MCP client

```sh
claude mcp add --transport http excel https://localhost:8443/excel/mcp
```

Claude Code discovers the authorization server from the 401 challenge, registers itself, opens
the browser for Microsoft sign-in and the consent page, and stores the tokens. Node-based
clients need to trust the dev certificate too (`NODE_EXTRA_CA_CERTS=<path to localhost.crt>` for
Claude Code). Then, in a session: `get_skills`, `list_instances` (the pane should be listed with
the workbook and active sheet), `get_status`, and `execute_script` with `script: "return 1"`.

## Auth

The hub is its own OAuth 2.1 authorization server at the issuer root (`hub/internal/authserver`,
PRD §05/§06/§13) and the resource server for every `/<connector>/mcp`. Sign-in is delegated to
Microsoft (Entra, multi-tenant `common` authority: work, school and personal accounts). There are
no passwords and no local accounts.

**Endpoints.** `/.well-known/oauth-authorization-server` (also served at
`/.well-known/openid-configuration`, which the MCP spec has clients try second), `/oauth/authorize`,
`/oauth/token`, `/oauth/register`, `/oauth/jwks`, `/oauth/consent`, `/login/microsoft`,
`/login/microsoft/callback`, and per connector `/<slug>/.well-known/oauth-protected-resource`.

**What a client does.** An unauthenticated request to `/excel/mcp` gets a 401 with
`WWW-Authenticate: Bearer resource_metadata="…/excel/.well-known/oauth-protected-resource",
scope="excel"`. The metadata names the hub as the authorization server and `excel` as the scope.
The client registers — a Client ID Metadata Document (an https URL as `client_id`, fetched and
cached by the hub) or dynamic registration (`POST /oauth/register`, public clients only,
rate-limited, unused registrations collected after 24 h) — then sends the user to
`/oauth/authorize` with PKCE S256, `state`, and `resource` (the MCP URL or the hub origin; both
name the hub). The hub shows a sign-in page, sends the user to Microsoft, validates the ID token,
records the identity (`{user_id, provider, subject: oid, tenant, email, display_name}`; `user_id`
is the hub's own random id, assigned on first login), shows a consent page (client name, scopes,
redirect host), and redirects back with a code. `/oauth/token` exchanges it for a 15-minute ES256
JWT (`iss`, `aud` = the hub origin, `sub` = `user_id`, `scope`, `client_id`, `jti`) plus an opaque
refresh token (30 days, sliding). Refresh tokens rotate on every use; presenting a rotated token
again revokes the whole family. The MCP endpoints verify the JWT locally from the same key set and
require the connector's scope (403 with the scope hint otherwise). The dev token is not accepted
on `/mcp`.

**Add the hub in Claude Code:**

```sh
claude mcp add --transport http excel https://connectors.eichler.ai/excel/mcp
```

**Add the hub in claude.ai** (custom connector): Settings → Connectors → Add custom connector,
URL `https://connectors.eichler.ai/excel/mcp`, leave the client id and secret empty. claude.ai
registers dynamically (or by metadata document), then sends you through the same sign-in.

**Run the flow in `-dev` mode** against `https://localhost:8443`: the Entra registration lists
`https://localhost:8443/login/microsoft/callback` as a redirect URI, so a `-dev` hub on the
default port completes a real Microsoft sign-in. Start the hub with the three variables from
"Run locally" above (the client secret comes from Secret Manager; it is never printed), add the
server to Claude Code as shown, and `/mcp` in Claude Code to authenticate. Without
`HUB_JWT_SIGNING_KEY` a `-dev` hub creates `jwt-signing-key.pem` beside its certificate so tokens
survive restarts. Watch the hub's log: it records `client registered`, `login: new user`,
`consent: approved` and `token: issued` with ids and scopes — never a token, code, secret or
email address.

**Signing key.** `HUB_JWT_SIGNING_KEY` is PEM with one or more P-256 private keys; the first
signs, all verify, and all are published at `/oauth/jwks` with `kid` = the RFC 7638 thumbprint.
Rotate by adding a new secret version with the new key block first, deploying, waiting out the
15-minute access-token lifetime, then removing the old block. Rotating also invalidates the
authorization server's own one-hour session cookie (its HMAC key is derived from the signing
key), which is harmless.

**Kill switch.** `hub revoke-user <user_id>` (with the same `HUB_FIRESTORE_*` environment as the
service, e.g. from a laptop with application-default credentials) revokes every refresh token of
a user; their access tokens expire within 15 minutes. Disabling registration globally is not
built yet (PRD §13 lists it; it is a one-line flag when needed).

**Storage.** `hub/internal/store`: `users`, `identities` (provider+subject → user),
`oauth_clients`, `login_states`, `auth_codes`, `refresh_tokens`. Codes and refresh tokens are
stored hashed. In memory without `HUB_FIRESTORE_PROJECT`; Firestore otherwise (prod: database
`(default)`; staging: `hub-staging`, so the two never share a user or a token). TTL policies on
`expires_at` are set by `deploy.sh`.

Tools and the script contract are documented for the agent in
[`../excel/connector/skill.md`](../excel/connector/skill.md), which `get_skills` returns.

## Deploy

`hub/deploy/deploy.sh staging|prod` builds the image with Cloud Build from the repo root and
deploys it to Cloud Run (`us-central1`, project `eichler-ai`): `hub-staging` at its own `run.app`
URL, `hub` at `https://connectors.eichler.ai`. Both run one instance (min = max = 1) with session
affinity and a 60-minute request timeout, so the WebSocket bridge survives. Re-running the script is
safe — it reuses the existing secret and service, and only creates what's missing.

```sh
hub/deploy/deploy.sh staging
hub/deploy/deploy.sh prod
```

Secrets per environment in Secret Manager: `HUB_DEV_TOKEN` (`hub-staging-dev-token`,
`hub-dev-token`; the pane's bridge token) and the JWT signing key (`hub-staging-jwt-signing-key`,
`hub-jwt-signing-key`), both generated once by the script and never printed by it, plus the
shared `entra-client-secret`, which is added by hand from the Entra portal (the script refuses to
deploy without it). The Entra application id is baked into the script (`HUB_MS_CLIENT_ID`
overrides it). After deploying, the script checks `/health`, the two discovery documents, the
JWKS and the 401 challenge (`hub/deploy/verify`); a full sign-in is the live test.

Fetch a bridge token to sideload the add-in:

```sh
gcloud secrets versions access latest --secret=hub-dev-token --project=eichler-ai          # prod
gcloud secrets versions access latest --secret=hub-staging-dev-token --project=eichler-ai  # staging
```

To point Excel at the hosted hub instead of a local one, download the manifest from
`https://connectors.eichler.ai/excel/manifest.xml` (or the staging equivalent) and sideload it as
in "Sideload the add-in in Excel for the web" above, then paste the token from Secret Manager into
the pane. Staging's issuer is the URL `gcloud run services describe hub-staging` reports as
`status.url`; a Cloud Run service also answers on its deterministic `*.run.app` URL, but tokens and
discovery are bound to the configured one, so add the staging server under that exact URL. Excel for the web keys a sideloaded add-in by the manifest `<Id>`, so dev, staging and
prod each serve a distinct one: prod serves the manifest file's Id and name unchanged (that's what
the Store submission carries), while dev and staging get a deterministic Id derived from the
environment and public URL and a `DisplayName` suffixed with `(dev)`/`(staging)`, so all three can
be sideloaded side by side without one silently replacing another.

## Tests

```sh
gofmt -l hub excel/connector excel/addin internal && go vet ./... && go test -race ./...
```

Everything is unit-tested over real sockets against a fake bridge (`hub/bridgetest`): hello
enforcement, origin policy, newest-wins with `replaced`, timeouts with late replies dropped,
cancel forwarding, multi-MiB fragmented results, auth on both endpoints, and the MCP tools over the
SDK's in-memory transport. The authorization server is tested end to end over `httptest` with a
fake Microsoft (own RSA JWKS, templated issuer) and a fake metadata-document host: the full code
flow, PKCE and redirect mismatches, resource and scope rejection, refresh rotation and reuse
revocation, code replay, expired tokens and login states, wrong audience/issuer/kid, DCR
validation and rate limiting, CIMD validation, caching and SSRF refusal, ID-token faults, provider
key rotation. `hub/internal/store` has one contract test that runs against the memory store
always and against the Firestore emulator when `FIRESTORE_EMULATOR_HOST` is set
(`gcloud emulators firestore start --host-port=localhost:8900`; needs a Java 21+ JRE on PATH).
What is not covered here is Excel itself — the task pane's runner and the `expect`/target prelude
run only inside Office — and the real Microsoft sign-in, so a change to `excel/addin/`, to
`wrap()` in `excel/connector/execute.go`, or to `authserver/oidc.go` gets a live check with the
steps above.

## Layout

One Go module at the repository root (`github.com/eichler-ai/connectors`); the Revit modules and
`excel/poc` keep their own `go.mod` and are excluded.

```
go.mod                the module
internal/auth/        Authenticator/Principal seam, the ES256 JWT key set + verifier, the bridge dev
                      token (repo-level internal so connector tests can build a hub)
hub/
  cmd/hub/            entrypoint and the revoke-user subcommand
  protocol/           bridge protocol v1 message types (importable by a local-mode server)
  diag/               the shared diagnostic record
  bridgetest/         fake bridge for tests
  internal/registry/  live bridges keyed by {user, connector, instance}; Send is the routing seam
  internal/bridge/    the WebSocket handler and exec/result correlation
  internal/authserver/ the OAuth 2.1 authorization server: metadata, authorize + consent, token,
                      DCR + CIMD clients, Microsoft OIDC login, session cookie
  internal/store/     Store interface: memory and Firestore
  internal/devcert/   -dev certificate
  connector.go        the Connector interface (PRD §09); host.go: Exec; tools.go: get_skills, list_instances
excel/
  addin/              manifest + task pane, embedded into the binary
  connector/          hub.Connector for Excel: execute_script, get_status, skill.md
```
