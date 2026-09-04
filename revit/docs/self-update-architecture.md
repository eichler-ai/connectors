# Self-update architecture: hosted primary + shimmed versioned add-in

Status: **design / not started.** Supersedes the design notes on issues #211 (add-in shim) and
#202 (hosted primary), and folds in the cleanup items #209 and #210. Companion to PRD §05
("Broker singleton") and §12 ("Installation UX"), which this design revises.

This document specifies one coherent target for connector self-update, replacing the piecemeal
patches that have accumulated (#192, #193, #195, #196, #201, #205, #212). It is written to be built
in phases by more than one person, so each part names what it deletes as well as what it adds.

---

## 1. Why update is temperamental today

Every hard-to-reason-about behaviour in self-update traces to a single constraint:

> **A component only takes an update when the OS process holding its files on disk exits — and no
> process is *designated* to be that one, so "make it exit" is a guess.**

Two instances of the same constraint, one per component:

- **Add-in.** Revit loads `MCPBridge.AddIn.dll` and its dependencies and holds them open for the life
  of the process. Windows refuses to overwrite an open image, so an add-in update must **close every
  running Revit of that version**. Everything temperamental follows: the 30 s close wait, the deferred
  path with its process watcher / logon task / pending manifest (#210), the "did it close, will it
  close?" uncertainty, the per-version `deployed`/`deferred`/`skipped` bookkeeping, and a Status
  window that must explain which of those states each Revit is in (#209).
- **Server.** The primary broker is whichever client process won the singleton lock. An update swaps
  the exe on disk, but the *running* primary keeps serving the old image, and "restart your MCP
  client" is true only for the client that happens to own it. When such a process is killed at the
  wrong moment it becomes a zombie holding the lock and port until reboot (#212). Stale-image
  self-eviction (#205) and dead-holder takeover (#212) contain this, but the ownership question — *no
  single process an installer or a person can point at and restart* — remains.

The target design removes the constraint on both halves. An update becomes **download → verify →
stage beside the running copy → flip an atomic pointer**, and it **takes effect at the component's
next natural start with nothing asked to close**. The two halves are independent — either ships
alone and helps — but they share one lifecycle, one Status vocabulary, and one uninstall.

---

## 2. Goals and non-goals

**Goals**

1. An update never requires closing Revit or hunting for the process that owns the server.
2. The moment an update is "applied" is atomic and observable; a half-applied update is impossible.
3. One update lifecycle and one set of Status words for both components.
4. No elevation for the default per-user install; an AllUsers install remains possible.
5. The failure of any single mechanism degrades to a working (if less convenient) path, never to a
   silent stuck state.
6. Backward compatible: a machine on today's layout upgrades into the new one exactly once, then
   never pays that cost again.

**Non-goals**

- Changing remote mode (the Mac broker in the Parallels dev topology). It already runs a deliberate,
  known broker process; §6 leaves it untouched.
- Signing / a CA certificate (PRD §12 defers it). This design makes signing *cheaper to adopt* (the
  shim is the only assembly Revit's trust prompt keys on for the stable part) but does not require it.
- Auto-applying updates without user intent. "Check" stays automatic; "apply" stays user-initiated,
  as today.

---

## 3. Target architecture at a glance

Two long-lived, stable anchors own the two components; versioned payloads sit behind atomic pointers.

```
Add-in (per Revit version)                    Server (per user, or per machine for AllUsers)
──────────────────────────                    ──────────────────────────────────────────────
Addins\<year>\MCPBridge.addin  ─┐             a hosted "primary" process (logon task / service)
Addins\<year>\MCPBridge.Shim.dll│  stable      runs  mcp-server --mode local --primary-only
                                │              binds the port, writes broker.json, owns the lock
%LOCALAPPDATA%\Programs\MCPBridge\             every client-spawned server is a secondary
  addin\current.json   {"version":"v0.1.5"}   %LOCALAPPDATA%\Programs\MCPBridge\
  addin\v0.1.4\2025\...  v0.1.4\2027\...         mcp-server.exe        (swapped in place, staged)
  addin\v0.1.5\2025\...  v0.1.5\2027\...         server-current.json?  (optional, see §6.4)
```

- **Add-in:** a tiny **shim** is the thing Revit actually loads. It reads `current.json` and loads the
  real add-in from `addin\<version>\<year>\`. Updating writes a new version folder and flips
  `current.json`. Running Revits keep the version they loaded; the new one loads on their next start.
- **Server:** a **hosted primary** process is the designated broker. Clients always become secondaries
  and proxy to it. Updating swaps `mcp-server.exe` and restarts the one hosted process. The
  lock-or-proxy election (the #212 work) is retained as the *fallback* when the host is not running.

The invariant both halves share: **the pointer flip (or the host restart) is the only "apply" step,
it is atomic, and it never contends with a running reader.**

---

## 4. Part A — Add-in: stable shim + versioned folders (#211)

### 4.1 On-disk layout

```
%APPDATA%\Autodesk\Revit\Addins\<year>\
    MCPBridge.addin        Type=Application, Assembly=MCPBridge.Shim.dll, same AddInId as today
    MCPBridge.Shim.dll     small; changes only when the shim contract changes (rarely)

%LOCALAPPDATA%\Programs\MCPBridge\addin\
    current.json           {"version":"v0.1.5"}     written last, atomically (write temp + rename)
    v0.1.4\2025\  v0.1.4\2027\    full payload per Revit year (today's addin-<year>/ contents verbatim)
    v0.1.5\2025\  v0.1.5\2027\
```

The `.addin` manifest keeps the current `AddInId` (`7C1B8C2E-…`) so Revit treats the shim as the same
add-in across the migration — no duplicate-add-in prompt, and user trust carries over.

### 4.2 The shim (`MCPBridge.Shim.dll`, `IExternalApplication`)

Responsibilities, in order, all inside a top-level try/catch that logs to `startup-errors.log` and
returns `Result.Failed` on any fault (never takes Revit down — same contract as today's
`MCPBridgeApplication.OnStartup`):

1. Resolve `%LOCALAPPDATA%\Programs\MCPBridge\addin\current.json`; read `version`.
2. Compute `addin\<version>\<year>\` where `<year>` is `application.ControlledApplication.VersionNumber`.
   **Fallback:** if that folder has no `MCPBridge.AddIn.dll` (a release shipped for only one Revit
   year), walk versions newest-first and load the newest folder that does. If none, log and fail.
3. Register an `AssemblyResolve`/`AssemblyLoadContext.Resolving` handler scoped to that folder, so the
   real add-in's dependencies resolve from it. The real add-in *already* runs
   `AssemblyResolution.Register()` (deps.json-scoped) and the Roslyn/SQLite isolation contexts; the
   shim's handler only needs to make the versioned folder the probe root before those run.
4. `Assembly.LoadFrom(addin\<version>\<year>\MCPBridge.AddIn.dll)`, reflect the real
   `MCPBridge.AddIn.MCPBridgeApplication`, instantiate it, and forward `OnStartup` / `OnShutdown`.

The shim holds **no** MCP logic, no version knowledge beyond `current.json`, and no ribbon code. It is
deliberately small and stable so that (a) Revit's trust prompt for the *manifest-named* assembly fires
at most once across many releases, and (b) it almost never needs an update that would reintroduce the
close-to-replace problem for itself.

### 4.3 Load-context correctness (the part that must not regress)

Today Revit `Assembly.LoadFrom`s `MCPBridge.AddIn.dll` directly, and the add-in's own
`AssemblyResolution` + `RoslynAssemblyIsolation` + `SqliteAssemblyIsolation` establish type identity
correctly (see `AssemblyResolution.cs`'s long comment). The shim must preserve exactly that:

- The shim `LoadFrom`s the real add-in from the versioned folder into the **same context Revit would
  have used** (the add-in's own per-add-in ALC on 2025+; Default on older hosts). `LoadFrom` of the
  real add-in is what happens today — the shim only changes *which directory* it comes from.
- **Exactly one** version of the real add-in loads per process. The pointer is read **once** at
  startup; the shim never reloads. Two versions in one AppDomain is the one thing that breaks Roslyn
  reference enumeration (`LoadableReferences()` walks the AppDomain) and `Eichler.Connectors.Revit`
  discovery — it must be structurally impossible, and single-read-at-startup makes it so.
- Ribbon `PushButtonData` needs the command classes' assembly *path*. Today that is
  `typeof(MCPBridgeApplication).Assembly.Location`, which after the shim's `LoadFrom` is the versioned
  path — already correct, no special-casing.

### 4.4 Update lifecycle (add-in)

```
download release zip → verify checksum → extract addin-<year>/ into addin\<newver>\<year>\
  → fsync the payload → write addin\current.json.tmp → atomic rename to current.json
```

No process is asked to close. A Revit already running keeps the files it mapped (untouched under its
own `addin\<oldver>\`); the new version loads on its **next** start. This is the exact shape the
server's staged swap already uses, now applied to the add-in.

### 4.5 Cleanup of old version folders

A version folder is removable once **no running Revit has it mapped**. Same shape as
`Remove-StaleBrokerImages`: on any installer run, for each `addin\<v>\` that is not `current.json`'s
version, try to delete it; a folder whose DLLs a running Revit holds open simply fails to delete and
is retried next run. Bound retained versions to, say, the current plus the previous (so a user who
hasn't restarted Revit can still be served their running version's files if ever re-read).

### 4.6 Trust prompt and signing

Revit's trust prompt ("unverified publisher — Load Once / Always Load / Do Not Load") is presented by
Revit's AddInManager for the assembly **named in a `.addin` manifest**. Assemblies loaded later by
add-in code via `Assembly.LoadFrom` are ordinary .NET loads that AddInManager does not scan and does
not prompt for. So the prompt keys on the **shim**, not on the versioned add-in the shim loads.

**Confirmed against the current setup (2026-09-04):** the project already self-signs both dev and
release builds (`MCPBridgeSignDevBuild` target in `MCPBridge.AddIn.csproj`, whose comment records that
signing gives Revit a *publisher*-level trust so a rebuild is not re-prompted, unlike an unsigned
build whose trust is content-keyed and re-prompts every time). On the dev VM there is no Revit
`…\Security` trust record for 2025 or 2027 and the signed add-in loads and connects with no prompt.
Consequences for the shim:

- Sign `MCPBridge.Shim.dll` with the same certificate the add-in already uses. The shim is then
  trusted at the publisher level exactly as the add-in is today — **no new prompt**, and because the
  shim's content changes only rarely, no re-prompt across releases either.
- The versioned `MCPBridge.AddIn.dll` is `LoadFrom`ed by the shim, so Revit's AddInManager never
  presents a prompt for it regardless; signing it (as today) keeps its own publisher trust intact for
  anything else that inspects it.

The empirical "does Revit prompt for the loaded add-in" test is **not observable on this machine**
because everything is signed and trusted — it would require deliberately shipping an *unsigned* shim
to force a prompt, which is not how the connector ships. The behaviour above is Revit's documented
AddInManager model plus the project's existing signing, so §9.1 no longer gates on it: **build the
shim signed with the existing certificate and the prompt is a non-issue**, the same as the add-in is
today.

### 4.7 Migration (today's flat layout → shim)

The first install carrying the shim replaces today's flat `Addins\<year>\MCPBridge.*` with the shim +
manifest, and lays down `addin\<version>\<year>\`. Because it replaces the currently-loaded
`MCPBridge.AddIn.dll`, **this one install still needs Revit closed** — handled by the *existing*
close/defer machinery, which then retires. After it, no add-in update ever closes Revit again. The
migration is detected by "the Addins folder holds `MCPBridge.AddIn.dll` directly rather than
`MCPBridge.Shim.dll`".

---

## 5. Part B — Server: hosted primary (#202)

### 5.1 The hosted primary

A single designated process runs continuously:

```
mcp-server --mode local --primary-only
```

`--primary-only` binds the port, writes `broker.json`, and **never** takes the lock-or-proxy branch —
it is always the primary. Client-spawned servers (Claude Desktop, Claude Code, Cowork) **always**
become secondaries and proxy their stdio to it. There is now exactly one process an installer, a
Status window, or a person can name and restart.

### 5.2 Hosting mechanism — recommendation

| Scope | Mechanism | Elevation | Notes |
|---|---|---|---|
| **User (default)** | `HKCU\…\Run` entry **plus** an immediate detached launch on install | none | Runs in the interactive session under the user's own token; `broker.json`/token are the user's. No admin, unlike registering a scheduled task (which hit "Access is denied" for a standard user in #210). |
| AllUsers | Windows service (`SERVICE_AUTO_START`) | admin (already required for an AllUsers install) | Runs as a dedicated account; §5.5 covers token/`broker.json` ownership. |

Recommendation: **HKCU Run key for per-user**, because it needs no elevation, runs in the interactive
session (correct `%LOCALAPPDATA%`, correct token owner for the auth token), and starts at every logon.
The installer also launches it immediately (detached, the same way `revit/dev-tooling/launcher-agent.ps1`
already starts the dev broker) so a fresh install works without a logout. A per-user **scheduled task**
is the fallback if a Run entry proves unreliable, but note the #210 registration-permission gotcha.

### 5.3 Clients always secondary — with the election retained as fallback

Client servers try to reach the hosted primary and proxy. **If the host is not running** (never
installed the task, disabled, crashed, or the brief window during its restart), a client falls back to
today's `singleton.Elect` — racing the lock, recording its pid, taking over a dead holder. So:

- The #212 election / lock-generation / dead-holder-takeover work is **not** wasted — it becomes the
  robustness floor beneath the hosted primary.
- A fresh install with no host yet still serves (first client becomes primary until the host starts).
- The hosted primary, on start, participates in the same election and wins/takes over the lock; a
  client that had become primary in the gap self-evicts to it on the next natural turn (stale-image
  logic generalises to "a hosted primary exists now").

### 5.4 Update lifecycle (server)

```
download → verify → stage mcp-server.exe.new → swap into place (existing staged-swap logic)
  → signal the hosted primary to restart  (service: Restart-Service; Run-key process: stop pid + relaunch)
```

Every client's proxied connection to the old primary drops; each client's secondary re-reaches the new
hosted primary (§5.6). Self-eviction (#205) stays as the backstop if the restart signal is missed: the
old primary notices its image changed and steps aside.

### 5.5 `broker.json` and token ownership

- Per-user (Run key): the primary runs as the user, writes `broker.json` under the user's
  `%LOCALAPPDATA%`, mints the token — identical to today, just in a known process.
- AllUsers (service): the service account writes `broker.json` to a location every client can read;
  the token file's ACL must allow the interactive users the service serves. This is the one genuinely
  new security surface and is called out as an open question (§10).

### 5.6 Session continuity across a primary restart — **the key open question**

When the hosted primary restarts, each client's secondary loses its upstream. A secondary is a
byte-level stdio proxy; the MCP `initialize` handshake was answered by the *old* primary.

**Measured 2026-09-04 (probe: two servers on one app-data dir, kill the primary mid-session).** The
promoted/reconnecting process starts a **fresh** MCP server that requires `initialize`. The client's
original `initialize` went to the dead primary, so the very next request is rejected:

```
B next request after A death (NO re-initialize): ERROR method "tools/list" is invalid during session initialization
B fresh initialize after death:                  OK
B tools/list after fresh initialize:             OK (14 tools)
```

So **transparent re-proxy is not free** — the new primary has no session state, and today the
promoted process keeps *running* (it does not exit), so the client, which never learns its upstream
changed, is wedged until a manual reconnect. Two viable designs, in preference order:

- **(a) Replay the cached `initialize`.** The secondary remembers the client's `initialize` request
  and, on connecting to any new primary, replays it and swallows the duplicate `initialize` response
  before resuming the proxy. Truly transparent: the user sees nothing across a host restart. Cost: the
  secondary must parse just enough of the stream to capture the first request and one response.
- **(b) Exit to force a client-driven reconnect.** On losing its upstream, the secondary **exits**
  (rather than promoting/serving a stale session), the MCP client respawns it, and the fresh child
  re-`initialize`s against the new primary. One brief reconnect — exactly the "reconnect the `revit`
  server / restart Claude" step the canonical UX (§6.2) already asks of the user for a server update,
  so it is consistent and acceptable.

Recommendation: **(a)** for a server auto-swap so a running client keeps working, with **(b)** as the
guaranteed fallback. Note the current code does neither — it promotes in place and serves a stale
session — so this is a real change Part B must make, not an existing property to preserve. The
`initialize`-replay is small and worth building; the probe confirms the exact failure it must fix.

---

## 6. Unified update lifecycle and UX

### 6.1 One lifecycle, both components

```
     check (automatic, unchanged)        apply (user-initiated, unchanged trigger)
     ─────────────────────────────       ────────────────────────────────────────
     server polls GitHub every 6 h  →    download → verify checksum →
     + update_connector checks now       stage each changed component beside the running copy →
                                         ATOMIC APPLY:
                                           add-in : write current.json (pointer flip)
                                           server : swap exe + restart hosted primary
                                         → takes effect at next natural start; nothing closes
```

A corpus-only release (server component only) restarts the hosted primary and is invisible to Revit —
already true today, now with no ownership ambiguity. An add-in-only release flips a pointer; running
Revits pick it up on next launch. A both-components release does both, independently.

### 6.2 What the user sees

**Canonical UX principle (drives everything below): an update never forces the user to exit Revit.
The tool installs the new files, then *tells the user* what to restart, and the user chooses when.**
"Apply" (write the files) and "load" (restart Revit / reconnect the client) are two separate,
user-controlled steps — the whole architecture exists to make that separation safe and atomic.

- **Install:** unchanged one-liner. It lays down the shim + first version folder, registers the hosted
  primary (Run key + immediate launch), and registers with the clients. (Fix #216 here: print
  `Downloading … (126 MB)` before the transfer so it never looks hung.)
- **Update from Revit (ribbon):** the ribbon carries a **visual "update available" indicator** (a
  distinct icon / badge on the MCP Bridge button, driven by the same `latest_available_version` the
  Status window already reads from `broker.json`). The user clicks **Update**; the script downloads,
  verifies, stages, and applies (add-in pointer flip + server swap/host restart). When the files are
  in place it reports success and tells the user plainly: **"Update installed. Restart Revit to load
  the new add-in"** (and, if the server component also changed, "and reconnect Claude"). Nothing is
  asked to close; Revit keeps running on its current version until the user restarts it. There is no
  "Revit will close" warning because it never does.
- **Update from Claude (`update_connector`):** the user runs `update_connector`; the tool checks
  GitHub, and on `apply` installs the new version with **nothing closed**. It then tells the user:
  **"Update installed. Reconnect the `revit` MCP server (or restart Claude Desktop), and the new add-in
  loads the next time you restart Revit."** The result reports, per Revit version, that the running
  add-in is older than the installed one (restart Revit to finish) and whether the hosted primary is on
  the new server yet.
- **Update from PowerShell:** re-run the one-liner; it stages and applies with nothing to close, and
  prints the same "restart Revit / reconnect Claude to load it" line.
- **Status window vocabulary (one shape for both):**
  ```
  MCP Bridge (add-in): v0.1.5 installed · running v0.1.4 — restart Revit to load it
  MCP Server:          v0.1.5 installed · primary running v0.1.5 (hosted)
  ```
  Both lines are `installed · running` with a one-line remedy only when they differ. This replaces the
  #209 special-casing (a Revit whose own add-in is deferred) entirely — there is no `deferred` state
  any more; there is only "installed vs what this process is running."
- **Uninstall:** remove the shim + manifest + the whole `addin\` tree, stop and deregister the hosted
  primary (service/Run key), remove the app dir and data, the Apps & features entry, and the client
  registrations. Fix #215 here: detect a running hosted primary or client servers and stop the ones we
  own (the hosted primary is ours to stop) / report the ones we don't, instead of the unconditional
  success line.

---

## 7. How this resolves every past challenge

| Past challenge (issue) | Resolution in this design |
|---|---|
| Add-in update forces Revit closed | §4: pointer flip; running Revit keeps its files, new version loads next start |
| Deferred watcher / logon task / pending manifest, three uncoordinated mechanisms (#210) | §4.4 deletes all of it; there is nothing to defer |
| Logon scheduled task "Access is denied" for a standard user (#210) | §5.2 uses an HKCU Run key (no elevation) for the hosted primary; no per-user scheduled task |
| Status window can't describe a deferred Revit (#209) | §6.2 one `installed · running` shape; no `deferred` state exists |
| Zombie primary holds lock+port until reboot (#212) | §5: a hosted primary is the designated broker; the election + dead-holder takeover remain as the fallback floor |
| "Restart your client" unreliable; primary owned by the wrong client (#202) | §5.1: one designated process; the installer restarts it directly |
| Stale image keeps serving after swap (#201/#205) | §5.4: explicit host restart; self-eviction stays as backstop |
| Schema-fingerprint skew between clients (#197/#198) | §5.1: one primary serves one schema to every secondary; skew window shrinks to a host restart |
| Piped-install stub self-copy (#192/#193) | Unchanged (the full-installer self-copy validator stays); the hosted primary registration reuses the same validated copy |
| Broker swap `pending` forever behind a locked `.old` (#193/#195/#196) | §5.4: the hosted primary is stopped before the swap by the restart step, so the image is not locked at swap time |
| UTF-8 BOM on marker/manifest JSON | Unchanged BOM-tolerant reads; `current.json` is written by the installer and read BOM-tolerantly by the shim |
| Silent 126 MB download looks hung (#216) | §6.2: print a "Downloading … (size)" line before the transfer |
| Uninstall leaves running server files (#215) | §6.2: uninstall stops the hosted primary it owns and reports client-owned servers |
| Trust prompt per release | §4.6: prompt keyed to the stable shim → once for the shim's life (verify live); signing collapses any residual to once per release |

Two challenges are **environmental, not design**, and are unchanged: the raw-CDN cache lag on
`install.ps1` (~5 min after a merge; verify via the release blob, not the raw URL) and the dev launcher
broker shadowing the installed server on the VM (kill it before install tests).

---

## 8. Verification plan (tier-2, live on 2025 and 2027)

Add-in (Part A):
1. Shim + one version folder on both Revit years: add-in loads, ribbon present, discovery + a script
   run unaffected (existing tier-2 sweep).
2. **Trust prompt:** first load of the shim, and first load of a *new* version folder, on each year —
   record whether the loaded add-in is prompted separately from the shim (§4.6).
3. Drop a second version folder + flip `current.json` **while Revit runs**: nothing happens; restart
   Revit → new version loads; old folder is cleaned on a later installer run once unmapped.
4. Fallback: a `current.json` version that lacks this year's payload → shim loads the newest that has it.

Server (Part B):
5. Hosted primary via Run key: install → primary running; log off/on → primary running; two clients →
   both secondaries proxying to it.
6. **Session continuity:** with a client mid-session, restart the hosted primary → confirm whether the
   client continues transparently (a) or reconnects once cleanly (b) (§5.6). This gate decides the
   Part B design.
7. Host-down fallback: stop the hosted primary → a client falls back to the election and serves; start
   the host → the client secondary re-reaches it (or self-evicts a client-primary to it).
8. Update with a running Revit **and** a running client: server swaps + host restarts (no Revit close),
   add-in pointer flips (no Revit close); Status shows `installed · running` correctly for both.

Migration:
9. A machine on today's flat layout takes the first shim install (one Revit close via the existing
   machinery), then a subsequent add-in update with Revit running (no close).

Zombie regression:
10. The #212 dead-holder takeover still fires when the host is absent and a client-primary is a corpse.

---

## 9. Phasing and sequencing

The two parts are independent; each is shippable alone. Recommended order, lowest-risk-highest-leverage
first:

1. **Part A (shim + versioned add-in).** Deletes the most machinery (close wait, deferred watcher,
   logon task, pending manifest, `-ApplyPendingUpdate`, the `deferred` state, most of #209/#210) and
   needs no new privilege surface. Ship it; #209/#210 largely evaporate. The trust-prompt question is
   settled (§4.6): sign the shim with the existing certificate and there is no new prompt. The one
   remaining Part A implementation risk worth a spike is the load chain itself — shim → `LoadFrom` the
   versioned add-in → ribbon + discovery + a script run unaffected — which the §8.1/§8.3 live sweep
   covers.
2. **Prototype Part B's §5.6 session continuity** before building it — one throwaway test of "restart
   the primary under a live client." Its answer (transparent vs one-reconnect) is the only thing that
   can change Part B's shape.
3. **Part B (hosted primary).** Build on the retained election as fallback. Per-user Run key first;
   AllUsers service second, behind the §10 token-ownership answer.

Do **not** invest further in #209/#210 as standalone fixes: Part A removes the code they patch. Land
only the trivial parts that must survive until Part A ships (e.g. #215/#216, which are install/uninstall
UX and independent of both parts).

## 10. Open questions (must be answered before the dependent phase)

- **(Part A — RESOLVED, §4.6)** Revit's trust prompt keys on the manifest-named shim, not on the
  `LoadFrom`ed add-in; the project already signs, so a shim signed with the same certificate adds no
  prompt. No longer gates phase 1.
- **(Part B — MEASURED, §5.6)** A secondary does **not** survive a primary restart transparently: the
  promoted/reconnected process starts a fresh MCP server that rejects the next request until
  re-`initialize`d. Part B must either replay the cached `initialize` (transparent) or exit to force a
  client reconnect (acceptable). This is a change to build, not a property to preserve.
- **(Part B, AllUsers only)** Under a service, where does `broker.json` live and how is the token file
  ACL'd so every interactive user the service serves can authenticate, without widening it to a local
  privilege leak? Blocks only the AllUsers variant, not per-user.

## 11. What gets deleted when this lands

- **Installer:** `Stop-RevitProcessGracefully` and the 30 s close wait; the deferred-update watcher, its
  logon task, the pending-update manifest and `-ApplyPendingUpdate`; the `deployed`/`deferred`/`skipped`
  per-version state machine (add-in becomes a pointer flip; server becomes swap + host restart).
- **Add-in:** the Status window's multi-state deferred wording (#209); the "will Revit close?"
  confirmation text.
- **Server:** nothing is deleted — the #212 election becomes the documented fallback beneath the hosted
  primary rather than the primary path.

The net is a smaller installer, one update lifecycle, one Status vocabulary, and no path in which a user
is left with a silently stuck update.
