# RFC: HTTP transport — a single user-space MCP server, and the networked connector it enables

Status: **proposed / north-star.** Not scheduled. This RFC names the intended long-term architecture
so we stop investing in machinery a transport change would retire, and so incremental work can be
steered toward it. It supersedes the "hosted primary" half of
[`self-update-architecture.md`](./self-update-architecture.md) §5 (see §8 below) and reframes PRD §05
("Broker singleton & port contention").

Companion issues: #202 (hosted primary — **shelved by this RFC**), #211 (add-in shim — **kept, it is
correct either way**). The add-in shim + versioned-folder work (PRs #218/#219) is orthogonal to the
transport and survives unchanged.

---

## 1. Problem: the complexity is emergent, not essential

The connector today uses the **stdio** MCP transport. Each Claude app (Desktop, Code, Cowork) *spawns
its own copy* of `mcp-server` as a subprocess and talks to it over stdin/stdout. That yields **N
server processes for one Revit**, and since only one can own the Revit connection, those processes
must coordinate: elect one **primary** to hold the lock + TCP port, make the rest **secondaries** that
proxy their stdio through it, handle the primary dying, dying-but-not-releasing, being replaced by an
update, and keeping a client's session alive across all of that.

Every hard, review-heavy thing we have built recently exists **only** to referee that "N self-spawned
processes, one Revit" situation:

| Machinery | Issue/PR | Exists because… |
|---|---|---|
| Singleton lock, primary/secondary, election | PRD §05 | stdio spawns N processes competing for one Revit |
| Stale-image self-eviction | #201/#205 | a swapped exe keeps running in N processes |
| Dead-holder takeover, lock generations | #212 | a killed process can hold the lock+port (zombie) |
| Session-continuity replay | #220 | the client's session breaks when *its* primary changes |
| Hosted primary + cooperative yield | #202/#221 | no process is *designated*, so nothing is restartable |

None of this is essential to the connector's job. It is all a tax on the transport choice. Change the
transport and the tax — and the whole bug class it generates — goes away.

## 2. Proposal

**Run one long-lived MCP server, in user space, listening on local HTTP; every Claude app connects to
its URL instead of spawning a copy; a tiny per-user supervisor keeps it alive and applies updates.**

Four pieces:

1. **A single local HTTP MCP server.** Bind `127.0.0.1:<ephemeral>` (unprivileged — no admin, the same
   loopback listen the broker already does). Serve MCP over Streamable HTTP. All Claude apps connect to
   the one URL; none spawns a server. There is structurally **one** server, so there is nothing to
   elect and no one to yield to.

2. **A tiny per-user supervisor.** Launched at logon via an **HKCU Run key** (or Startup shortcut /
   per-user scheduled task — all no-admin, all run *as the user, in the user's session*). Its only jobs:
   keep the server process running (restart it if it exits), and apply staged updates (swap the
   versioned server payload, restart it). It is small and rarely changes — the stable anchor for the
   server, exactly mirroring the add-in shim.

3. **The Revit add-in connects once to the stable server and stays.** The add-in has always been a
   *network client* of the server over its own socket (not a stdio subprocess); it was never part of
   the singleton dance. With one non-churning server, `broker.json`/discovery stops changing and the
   add-in's reconnect machinery goes from routine to rare fallback. **No changes are forced on the
   add-in** for the local case.

4. **Self-update by shim + versioned payloads for both halves.** The add-in shim loads
   `addin/<version>/<year>/` behind an atomic `current.json` (PR #218, correct as-is); the supervisor
   loads `server/<version>/` the same way. An update is *stage new version → flip pointer → restart the
   payload*. Nothing closes except the one unavoidable "restart Revit to load the new add-in," clearly
   messaged.

## 3. Why it wins on UX, robustness, and maintainability

- **Robustness — it deletes a bug class rather than managing it.** The zombie-primary, dead-holder
  takeover, election race, and session-continuity failures (#201/#205/#212/#220) all stem from the
  multi-process model. One supervised server has simple, local failure modes: server crashes →
  supervisor restarts; Revit closes → add-in reconnects; update → atomic pointer + restart. No
  cross-process coordination, no lock hand-off, no split brain.

- **UX — the server becomes a real, nameable, restartable thing.** "MCP Server: running v0.1.7, up
  since 09:14." Updates are deterministic ("the server was restarted"), not today's hedgy "it steps
  aside within a minute, and if it's still there, reconnect the revit server in your client." That
  crispness is exactly what the hosted primary reached for — here it is free, because there is
  genuinely one server.

- **Maintainability — a large deletion.** The singleton / election / primary-secondary / proxy /
  continuity / hosted-primary code is the most intricate, most reviewed code in the project. It
  collapses to "one server, one supervisor." The shim + versioned-payload pattern becomes uniform
  across both components.

## 4. The larger prize: a networked, multi-instance connector

HTTP is not just a cleaner way to do the same job — it unlocks a strictly larger product that the
stdio-localhost design *forecloses*:

- **Two-way, networked, multi-party.** stdio is already bidirectional, but only with the single parent
  process that spawned it, on one machine. Streamable HTTP (server → client over SSE) gives one server
  live two-way conversations with *many independent, networked* clients that never spawned it.
- **Remote and multiple Revit instances through one server.** The server becomes a hub: Revit
  instances — this machine, other workstations, eventually a hosted server — register with the one
  server; Claude clients connect to the same server; Claude can address any registered instance. The
  connector is **already leaning this way** — `list_instances`, per-instance IDs, and the existing
  "remote mode" all reach toward multi-instance. HTTP is the transport that lets that reach across
  machines cleanly instead of being penned into one box. A team's Revit fleet, or a hosted broker,
  behind one connector, becomes possible.

This is the reason to treat HTTP as the north star rather than a refactor: it changes what the product
can be.

## 5. The two links (what changes, what does not)

The connector has **two** links; conflating them is the main source of confusion.

```
Link 1  Claude app  ⇄  MCP server        stdio today  →  local HTTP (this RFC)
Link 2  MCP server  ⇄  Revit add-in      TCP + NDJSON + token (unchanged for local)
```

- **Link 1 → HTTP.** This is the whole change for the local case. The client config moves from "spawn
  this exe" to "connect to this URL," and the URL's server is kept up by the supervisor.
- **Link 2 stays as-is for local.** The single HTTP server also runs the existing TCP listener the
  add-in dials — one process, two listeners. The add-in is untouched and *more stable*.
- **Link 2 → network-grade for remote (§4, future).** Reaching a Revit on another machine means Link 2
  gains TLS/WSS, real auth, and instance routing — the same treatment HTTPS gives Link 1. Well-understood
  work, but it is on the add-in link, and it is what makes remote Revit real.

## 6. Supervision and security in user space (no admin)

- **User-space throughout.** Binding loopback and creating an HKCU Run-key / per-user task need no
  elevation and run as the user with the user's `%LOCALAPPDATA%` and token. The one thing given up vs a
  Windows **service** is OS-managed auto-restart on crash — coverable by a small watchdog, a
  relaunch-on-exit wrapper, or accepting recovery at next logon (the server is stable and rarely dies).
  A service remains an optional AllUsers variant, with its known elevation + cross-user token/ACL cost.
- **Local auth.** A loopback HTTP port is reachable by any local process, so it needs a token +
  origin/bearer check — cleaner in user space (token in the user's own `%LOCALAPPDATA%`, no cross-user
  ACL) than a service would be.
- **Remote auth/governance (future).** Off-machine access raises "who may drive a firm's Revit models
  remotely" — auth scoping, per-instance authorization, audit. A design axis localhost never had; a
  gate on §4, not on the local single-server move.

## 7. Open questions to verify BEFORE committing (feasibility gates)

1. **Local HTTP MCP support in the clients — Claude Desktop especially.** Claude Code supports HTTP
   transport; whether Desktop cleanly drives a *local* HTTP MCP server (vs stdio for local, HTTP only
   for remote connectors) is make-or-break for the local single-server move. **Verify against current
   client docs — do not assume.** If Desktop won't, stdio-with-singleton stays the pragmatic local
   reality and this RFC applies only to the remote direction.
2. **Remote connector support (for §4).** Clients connecting to a *networked* HTTP MCP server (remote
   connectors) is the better-trodden path and may be more feasible than local-HTTP; confirm the setup
   and auth model for each client.
3. **Supervisor shape.** The smallest reliable per-user "keep it running + apply updates" mechanism on
   Windows without reinventing a fragile init system.
4. **Link-2 transport for remote.** TLS/WSS + auth + instance routing for the add-in link; whether to
   keep TCP/NDJSON+TLS or move the add-in onto WebSocket against the same HTTP server.

## 8. Relationship to the current codebase

- **Keep, correct either way:** the add-in shim + versioned-folder layout and its Status wording
  (#218/#219). Not legacy; the right update pattern regardless of transport.
- **Retired by the target design:** the singleton/election machinery and everything built to tame it —
  stale-image eviction (#205), dead-holder takeover (#212), session-continuity replay (#220), and the
  hosted primary (#202/#221). They remain correct and valuable *for as long as stdio is the transport*;
  the point is not to build *more* of that class (which is why **#221 is shelved**, see below), and to
  delete it if/when Link 1 becomes HTTP.
- **`self-update-architecture.md` §5 (hosted primary) is superseded** by this RFC. §4 (add-in shim) of
  that doc stands.

## 9. Migration is incremental, not a rewrite

This need not be a big-bang. A plausible order once §7.1 is confirmed:

1. Add an HTTP transport listener to the existing server (alongside stdio) — the server already has the
   MCP handlers; this is a transport addition, not a rewrite.
2. Introduce the per-user supervisor + versioned server payload; register **Claude Code** (which
   supports HTTP) against the local URL as the first client, stdio still working for others.
3. Once Desktop local-HTTP is confirmed, move it too; then the singleton machinery has no callers and
   is deleted.
4. Separately and later, pursue §4 (remote/multi-instance) by upgrading Link 2.

Each step is shippable and reversible; stdio stays as the fallback until the last client is moved.

## 10. Non-goals

- Not a mandate to migrate now — this is the target, gated on §7.1.
- Not the remote/multi-instance feature itself (§4) — that is the *destination* this transport makes
  reachable, scoped separately with its own security/governance work.
- Not a Windows service (admin) as the default — user space is the baseline; a service is an optional
  AllUsers variant.
