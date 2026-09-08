# Connectors Hub

The hosted service connectors plug into — design in [`docs/PRD.md`](docs/PRD.md). One Go binary
serves, per connector, an MCP endpoint (`/<slug>/mcp`), the WebSocket the host application's
extension dials (`/<slug>/bridge`), and the extension's own files (`/<slug>/addin/…`,
`/<slug>/manifest.xml`). Excel is the first connector (`../excel/`).

This is phase 0: bridge protocol v1, the in-process registry, and a shared dev token standing in
for sign-in. OAuth, Firestore, file exchange and Cloud Run deployment follow.

## Run locally (`-dev`)

```sh
cd hub
export HUB_DEV_TOKEN="$(openssl rand -hex 16)"   # 16+ characters; used by the pane and the MCP client
go run ./cmd/hub -dev
```

That serves `https://localhost:8443` with a self-signed certificate written to
`~/Library/Application Support/Connectors/Hub/` (or the platform equivalent). The browser has to
trust it once — `go run ./cmd/hub -trust-cert` prints the command — then restart the browser and
check `https://localhost:8443/excel/addin/taskpane.html` loads without a warning. Text logs go to
stderr; they carry ids and outcomes, never scripts, results or tokens.

Environment: `PORT` (default 8443 in `-dev`, 8080 otherwise), `HUB_PUBLIC_URL` (external origin,
rewritten into served manifests; defaults to the local one), `HUB_ALLOWED_ORIGINS` (extra browser
origins for the bridge socket, comma-separated), `HUB_DEV_TOKEN` (required).

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

## Tests

```sh
(cd hub && gofmt -l . && go vet ./... && go test -race ./...)
(cd excel && gofmt -l connector addin && go vet ./... && go test -race ./...)
```

Everything is unit-tested over real sockets against a fake bridge (`hub/bridgetest`): hello
enforcement, origin policy, newest-wins with `replaced`, timeouts with late replies dropped,
cancel forwarding, multi-MiB fragmented results, auth on both endpoints, and the MCP tools over the
SDK's in-memory transport. What is not covered here is Excel itself — the task pane's runner and
the `expect`/target prelude run only inside Office — so a change to `excel/addin/` or to
`wrap()` in `excel/connector/execute.go` gets a live check with the steps above.

## Layout

```
hub/
  cmd/hub/            entrypoint
  protocol/           bridge protocol v1 message types (importable by a local-mode server)
  diag/               the shared diagnostic record
  auth/               Authenticator/Principal seam; the phase-0 dev token
  bridgetest/         fake bridge for tests
  internal/registry/  live bridges keyed by {user, connector, instance}; Send is the routing seam
  internal/bridge/    the WebSocket handler and exec/result correlation
  internal/devcert/   -dev certificate
  connector.go        the Connector interface (PRD §09); host.go: Exec; tools.go: get_skills, list_instances
excel/
  addin/              manifest + task pane, embedded into the binary
  connector/          hub.Connector for Excel: execute_script, get_status, skill.md
```
