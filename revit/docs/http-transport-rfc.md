# RFC: MCP transport direction — what local can be, and where HTTP actually pays off

Status: **proposed / north-star.** Revised after a client-capability check (§3) reversed the original
thesis. This RFC names the intended transport direction so we stop chasing an unavailable local
design and steer effort correctly. It supersedes the "hosted primary" half of
[`self-update-architecture.md`](./self-update-architecture.md) §5, and reframes PRD §05 ("Broker
singleton").

Companion issues: #202 (hosted primary — **shelved**), #211 (add-in shim — **kept**). The add-in shim
+ versioned-folder work (#218/#219) is orthogonal to transport and survives unchanged.

**Context that shapes every recommendation: the project is in dev, with no users but us.** There is no
in-the-wild install to protect, so backward-compat, migration paths, and release-gating are NOT
constraints. We can delete and rebuild freely and should optimize for the right end-state, not for a
smooth upgrade from the current one.

---

## 1. The complexity is a stdio tax (unchanged from the first draft)

The connector uses the **stdio** MCP transport: each Claude app (Desktop, Code, Cowork) *spawns its
own copy* of `mcp-server` as a subprocess over stdin/stdout. That yields **N server processes for one
Revit**, and since only one can own the Revit connection they must coordinate — elect a **primary**,
proxy the rest as **secondaries**, survive the primary dying / not releasing / being replaced, and keep
each client's session alive across all of it.

Every hard, review-heavy thing built recently exists **only** to referee that situation:

| Machinery | Issue/PR | Exists because… |
|---|---|---|
| Singleton lock, primary/secondary, election | PRD §05 | stdio spawns N processes competing for one Revit |
| Stale-image self-eviction | #201/#205 | a swapped exe keeps running in N processes |
| Dead-holder takeover, lock generations | #212 | a killed process can hold the lock+port (zombie) |
| Session-continuity replay | #220 | the client's session breaks when *its* primary changes |
| Hosted primary + cooperative yield | #202/#221 | no process is *designated*, so nothing is restartable |

The first draft of this RFC proposed erasing that tax by replacing local stdio with **one local HTTP
server** every client connects to. A capability check killed that premise.

## 2. The hard constraint (why the first draft's local plan is dead)

**Claude clients cannot connect to a *local* HTTP MCP server.** Verified against current docs (§3):
Claude Desktop and Claude Code are **stdio-only for local servers**; HTTP transport is **remote-only**
(a hosted URL added as a connector). A `http://127.0.0.1:PORT` server is not a client-configurable
local server for either.

So **the client always spawns a local stdio process** — that is fixed and outside our control. Any
local architecture therefore has ≥1 client-spawned stdio process; the only freedom is *what that
process does* and *how several of them share one Revit*. "One local HTTP server everyone connects to
directly" is not an option the platform offers.

## 3. Client transport support (the finding, with sources)

Checked against the MCP spec and the Claude client docs (client capabilities evolve; this is *true
today*, not forever):

- **MCP transports.** The spec defines **stdio** (local subprocess) and **Streamable HTTP** (the
  current HTTP transport; the older HTTP+SSE is deprecated), plus WebSocket. Streamable HTTP is aimed
  at **remote** servers, with loopback-bind + `Origin` validation + auth expected when local.
- **Claude Code (CLI):** HTTP transport is **remote-only** — `claude mcp add --transport http <name>
  <url>` expects a hosted URL; local servers must use `--transport stdio`. A `http://127.0.0.1` URL is
  not accepted for a local server.
- **Claude Desktop:** local servers in `claude_desktop_config.json` are **stdio only** (`command` +
  `args`); there is no local HTTP/URL server entry. HTTP servers appear only as remote **connectors**
  (a hosted URL in the connectors UI).
- **Claude.ai / Cowork:** remote connectors (hosted HTTP) yes; local HTTP no.

Sources: MCP transports spec (`modelcontextprotocol.io/specification/.../basic/transports`),
Claude Code MCP reference (`code.claude.com/docs/en/mcp.md`), MCP local-server quickstart
(`modelcontextprotocol.io/docs/develop/connect-local-servers`).

**Why it's stdio-only splits by client, and the split matters.** For **cloud/hosted clients**
(claude.ai web, Cowork) the reason is hard and permanent: they run in Anthropic's infrastructure and
*cannot reach your machine's* `127.0.0.1` at all, so a local server must be stdio-spawned or exposed as
a remote hosted URL. For the **local apps** (Desktop, Code) reachability is *not* the reason — they run
on your machine and could open a loopback port; stdio-only there is a design choice (clean spawn/kill
lifecycle, and a private parent-child pipe instead of a locally-reachable port that any process could
hit and that would need auth + `Origin` checks). So local HTTP is *not physically impossible* for the
local apps — it is simply not offered today and could change — whereas the cloud clients will never see
your localhost. That permanent cloud constraint is exactly what points HTTP at the **remote** hub (§4b):
a hosted server both the cloud clients and off-machine Revit instances can reach.

## 4. Corrected direction

### 4a. Local — keep the stdio-singleton design; it *is* the right local answer

Given clients must spawn stdio and cannot reach a local HTTP server, the alternatives to today's design
are:

- **Current (A):** each spawned process is a full server; they elect one primary, the rest proxy. Works,
  self-heals (#205/#212/#220), needs no supervisor and no fallback gap.
- **Dedicated server + thin stdio proxies (B):** a supervisor keeps one real server alive; each
  client-spawned process is a thin stdio→server proxy that never elects. Removes the election — but adds
  a supervisor, a "server not up / just restarted" reconnect path (which still needs #220-style replay),
  and a fallback gap (server down + not yet restarted ⇒ clients stuck). It is the hosted primary's
  tradeoff by another route, with no clear win now that the clean-HTTP version is unavailable.

So **A is the pragmatic best local design.** The singleton machinery is the price of stdio's
multi-process model, and with the self-healing pieces already merged it is a *manageable, working* price
— not worth trading for B's supervisor + fallback gap. This is also why **the hosted primary (#221) is
shelved**: the one thing that would have made a dedicated-server design clearly better (direct local
HTTP) does not exist.

### 4b. HTTP's real payoff is *remote* — a future feature, not a local migration

Where HTTP genuinely earns its place is exactly where clients *do* support it: **remote**. A hosted /
networked HTTP MCP server that fronts **multiple Revit instances** — this machine, other workstations,
eventually a server — is a strictly larger product the stdio-localhost design forecloses, and the
connector already leans that way (`list_instances`, per-instance IDs, "remote mode"). This is
client-supported (remote connectors) and is the honest home for the HTTP investment. It is a **new
capability to design on its own**, not a replacement for the local path, and it carries its own
network-security/governance work on the add-in link (TLS/WSS, auth, instance routing).

### 4c. Because we have no users, clean up *now*

Dev-mode removes the constraints that were deferring the cleanup:

- **Delete the legacy flat-deploy branch and the flat→shim *migration*** in `install.ps1` — there are
  no flat installs in the wild to migrate. Make the shim + versioned layout the *only* add-in layout.
- **Delete the deferred-update / close / pending-manifest machinery** (the old "Part 3"), no longer
  gated on a shim release, since no user needs a compatible upgrade path.
- No release is needed to unlock any of this; there is no one to release to yet.

This is the "clean up the code before the final version" the local design actually calls for — and it
is a large, safe simplification precisely because there is nothing to be backward-compatible with.

## 5. What survives, what goes

- **Keep:** the add-in shim + versioned folders and Status wording (#218/#219) — correct regardless of
  transport. And the stdio-singleton server design (§4a).
- **Delete now (dev-mode, §4c):** the legacy flat branch, the flat→shim migration, and the
  deferred/close/pending machinery.
- **Shelved:** the hosted primary (#202/#221) — the clean version needs local HTTP, which does not exist.
- **Future, separately scoped:** the remote/multi-instance HTTP hub (§4b).
- **`self-update-architecture.md` §5** (hosted primary) is superseded; §4 (add-in shim) stands.

## 6. Open questions

1. **Remote-hub design (when prioritized):** the Link-2 (server↔add-in) transport for remote — TCP+TLS
   vs WebSocket against the HTTP server — plus auth, instance routing, and the governance model for
   off-machine access to a firm's models.
2. **Re-check client local-HTTP support periodically** — if Desktop/Code ever support local HTTP
   servers, §4a's calculus changes and a dedicated local server becomes clean again.
3. Whether any part of §4c's cleanup should wait for the first real user (default: no — do it now).

## 7. Non-goals

- Not migrating the local transport (the platform does not allow it).
- Not building the remote hub here (it is the destination §4b names, scoped separately).
- Not preserving any upgrade path from the current on-disk layout (no users).
