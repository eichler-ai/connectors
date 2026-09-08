# Connectors Hub

The hosted service connectors plug into — design in [`docs/PRD.md`](docs/PRD.md). One Go binary
serves, per connector, an MCP endpoint (`/<slug>/mcp`), the WebSocket the host application's
extension dials (`/<slug>/bridge`), and the extension's own files (`/<slug>/addin/…`,
`/<slug>/manifest.xml`). Excel is the first connector (`../excel/`).

This is phase 0: bridge protocol v1, the in-process registry, and a shared dev token standing in
for sign-in. OAuth, Firestore, file exchange and Cloud Run deployment follow.

## Run locally (`-dev`)

From the repository root (the Go module lives there and links `hub/` and every connector):

```sh
export HUB_DEV_TOKEN="$(openssl rand -hex 16)"   # 16+ characters; used by the pane and the MCP client
go run ./hub/cmd/hub -dev
```

That serves `https://localhost:8443` with a self-signed certificate written to
`~/Library/Application Support/Connectors/Hub/` (or the platform equivalent). The browser has to
trust it once — `go run ./hub/cmd/hub -trust-cert` prints the command — then restart the browser and
check `https://localhost:8443/excel/addin/taskpane.html` loads without a warning. Text logs go to
stderr; they carry ids and outcomes, never scripts, results or tokens.

Environment: `PORT` (default 8443 in `-dev`, 8080 otherwise), `HUB_PUBLIC_URL` (external origin,
rewritten into served manifests; defaults to the local one), `HUB_ALLOWED_ORIGINS` (extra browser
origins for the bridge socket, comma-separated), `HUB_DEV_TOKEN` (required), `HUB_ENV` (`prod`,
`staging` or `dev`; default `dev`, and `-dev` always forces `dev` regardless of this variable).

## Sideload the add-in in Excel for the web

1. With the hub running, download `https://localhost:8443/excel/manifest.xml`.
2. Open a workbook in Excel for the web (OneDrive or SharePoint). **Home → Add-ins → More Add-ins →
   My Add-ins → Upload My Add-in**, choose the manifest. If the upload option is missing, tenant
   policy blocks sideloading; a personal Microsoft account allows it.
3. An **MCP Bridge** group appears on the Home tab. Click **Open MCP Bridge**, paste the value of
   `HUB_DEV_TOKEN` into the token field and click **Save**. The pane should say *Connected*.

The pane keeps the token in its own origin's `localStorage`; phase 1 replaces the field with
sign-in. If the pane loads but never connects, inspect it (right-click → Inspect) — the close
reason names the cause (`token rejected`, `bridge-outdated`, …).

## Connect an MCP client

```sh
claude mcp add --transport http excel https://localhost:8443/excel/mcp \
  --header "Authorization: Bearer $HUB_DEV_TOKEN"
```

Node-based clients need to trust the dev certificate too (`NODE_EXTRA_CA_CERTS=<path to
localhost.crt>` for Claude Code). Then, in a session: `get_skills`, `list_instances` (the pane
should be listed with the workbook and active sheet), `get_status`, and `execute_script` with
`script: "return 1"`.

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

Each environment's `HUB_DEV_TOKEN` lives in Secret Manager (`hub-staging-dev-token`,
`hub-dev-token`), generated once by the script and never printed by it. Fetch one to sideload the
add-in or connect an MCP client:

```sh
gcloud secrets versions access latest --secret=hub-dev-token --project=eichler-ai          # prod
gcloud secrets versions access latest --secret=hub-staging-dev-token --project=eichler-ai  # staging
```

To point Excel at the hosted hub instead of a local one, download the manifest from
`https://connectors.eichler.ai/excel/manifest.xml` (or the staging equivalent) and sideload it as
in "Sideload the add-in in Excel for the web" above, then paste the token from Secret Manager into
the pane. Excel for the web keys a sideloaded add-in by the manifest `<Id>`, so dev, staging and
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
SDK's in-memory transport. What is not covered here is Excel itself — the task pane's runner and
the `expect`/target prelude run only inside Office — so a change to `excel/addin/` or to
`wrap()` in `excel/connector/execute.go` gets a live check with the steps above.

## Layout

One Go module at the repository root (`github.com/eichler-ai/connectors`); the Revit modules and
`excel/poc` keep their own `go.mod` and are excluded.

```
go.mod                the module
internal/auth/        Authenticator/Principal seam; the phase-0 dev token (repo-level internal so
                      connector tests can build a hub)
hub/
  cmd/hub/            entrypoint
  protocol/           bridge protocol v1 message types (importable by a local-mode server)
  diag/               the shared diagnostic record
  bridgetest/         fake bridge for tests
  internal/registry/  live bridges keyed by {user, connector, instance}; Send is the routing seam
  internal/bridge/    the WebSocket handler and exec/result correlation
  internal/devcert/   -dev certificate
  connector.go        the Connector interface (PRD §09); host.go: Exec; tools.go: get_skills, list_instances
excel/
  addin/              manifest + task pane, embedded into the binary
  connector/          hub.Connector for Excel: execute_script, get_status, skill.md
```
