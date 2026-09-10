---
name: rhino-connector-development
description: Development process, tooling, testing strategy, and PR review checklist for building the Rhino connector (Rhino MCP Bridge plug-in + Rhino MCP Server) in this repo. Use whenever implementing, testing, reviewing, or deploying any part of the Rhino connector, or when the process itself needs updating based on what's been learned.
---

# Rhino Connector Development

How to build, test, and review this connector. Patterned after `revit-connector-development`, which
remains the reference for the engineering rules; this file repeats only what Rhino changes and adds
what Rhino needs. Update it when the process changes — see "Keeping these files current".

Two companion files sit beside this one. **`dev-environment.md`** is the machine-level detail, split
explicitly into **developing on the Mac** (the dev platform) and **verifying on Windows** (a release
target, not a dev platform). **`caveats.md`** is indexed by symptom — read it when something behaves
strangely, before theorising.

## Orientation

- **Design source of truth:** `rhino/docs/PRD.md`. **Plan:** `rhino/docs/implementation-plan.md`
  (phases, tiers, PR breakdown). **What was verified live before design was committed:**
  `rhino/docs/spikes/phase-1a-findings.md` (conclusions) and `phase-1a-method.md` (run log).
- **Naming:** `CONVENTIONS.md`. MCP Bridge (the plug-in) vs. MCP Server (the Go process); assemblies
  are `Rhino.MCPBridge.*`; the script-facing assembly is `Eichler.Connectors.Rhino`; the MCP client
  slug is `rhino`.
- **Layout:** `rhino/mcp-bridge/` (plug-in, C#, .NET 8), `rhino/mcp-server/` (server, Go),
  `rhino/test-harness/` (live tier-2 suite), `rhino/dev-tooling/` (Mac deploy script),
  `internal/servercore/` (the Go core shared with the Revit server: transport, diag, buildinfo,
  selfcheck, semsearch).
- **Diagnostics:** the one record shape from Revit PRD §01 — `severity`, `code` (kebab-case),
  `source` (a module tag: `mcp-bridge.core.connection`, …), `message`, `detail`, `remedy`.

## Engineering rules

Every rule in `revit-connector-development/SKILL.md` "Engineering rules" applies unchanged — a green
result is not evidence; a test that cannot fail is not coverage; `public` means script-reachable in
the bridge's assemblies; the acting connection's identity travels with the action; every retained
buffer states its bound; a specification is not behaviour; verify a claimed impossibility live. Read
that section. What Rhino adds:

### Verification

- **Rhino reports by file, never by stdout.** `rhinocode script` relays nothing; a probe writes JSON
  to a path and the caller reads it *after the last step*. Both spike attempts that read too early
  or read a stale file produced wrong conclusions (method §10–§11).
- **Assert on names and ids, never on counts alone.** Two spheres added twice and one undo look
  identical to one add by count. Name objects per run (`run3-a`) or read `document_id`s; the E
  probe was ambiguous until it did.
- **A locked screen turns every live step into a silent no-op.** Rhino stops running scripts, GUI
  automation stops working, and nothing errors. The harness's `preflight` checks
  `CGSSessionScreenIsLocked`; a manual live session should too.
- **An `&&` chain that includes the restart helper hides a failed restart** and then runs the tests
  against the old build (or nothing). The deploy script fails loudly; keep it that way and do not
  inline its steps into ad-hoc chains.
- **Confirm the plug-in Rhino loaded is the one you built.** Rhino loads from the yak package folder,
  which is only rescanned at startup, and a hand-copied `.rhp` without yak's `manifest.txt` is
  ignored. The Status command prints the bridge version (which carries the source revision); check it
  after every deploy.

### Boundaries and guards

- **The CPython host does not marshal.** `RhinoCode.RunScript` from a background thread runs the
  script *on that thread* and lets it touch the document. Every run goes through the plug-in's
  command-per-run executor on the main thread; nothing else may call the host.
- **Never call `RhinoDoc.Undo()` inside a run.** Undo is deferred to the end of the current command
  and a mid-command `Undo()` discards the entry without reverting (spikes §3). Rollback is
  `RhinoApp.ExecuteCommand(doc, "_Undo")` issued *after* the run's command has returned.
- **`RhinoApp.RunScript` from outside a command context does nothing** and `SendKeystrokes` repeats
  the last command. `ExecuteCommand` is the one way to run a command from a plug-in thread.
- **Language registries load lazily.** A fresh Rhino has only the built-in languages;
  `Languages.WaitStatusComplete(LanguageSpec.Python3)` (~1.75 s) must precede the first run or every
  run fails with a `CodeLanguageNotFoundException` that reads like a shebang problem.

### Correctness

- **Both sides must agree on the app-data path.** .NET maps `LocalApplicationData` to `~/.local/share`
  on macOS and Go's `os.UserConfigDir` returns the roaming profile on Windows; both are wrong for us.
  `AppDataPaths` (C#) and `internal/appdata` (Go) spell the rule out per OS. Change both or the
  server never finds a Rhino.
- **The document model differs by OS.** Mac Rhino holds many documents per process; Windows Rhino
  holds one (PRD §05). Addressing is designed for the Mac case; a Windows-only assumption in a test
  is a bug.
- **`Process.PrivateMemorySize64` is 0 on macOS.** Assert the working set, not the private bytes,
  in anything platform-neutral.

### Working in agent sessions

- **Use absolute paths and `cd <dir> && <cmd>` in one invocation** — the cwd resets between calls
  and lands in whichever directory the last command ended in.
- **The dotnet SDK is Homebrew's `dotnet@8`, keg-only.** Every command needs
  `DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec` and `/opt/homebrew/opt/dotnet@8/bin` on `PATH`;
  the deploy script sets both. Without them `dotnet` is not found.
- **PRs that touch `.github/workflows/` need the keyring `gh` login** (`env -u GITHUB_TOKEN gh pr
  merge …`); the environment token lacks the `workflow` scope.
- **Never `git add -A` at the repo root**: the fetched search models under
  `internal/servercore/semsearch/models/assets/` show as untracked whenever the branch you are on
  predates the module. Add paths explicitly.

## Testing strategy

Two tiers, no mocked middle tier — `CONVENTIONS.md` "Testing philosophy", with one improvement over
Revit and one simplification:

### Tier 1 — unit tests, in CI on every push

**MCP Server (Go):** everything, TDD-first, table-driven. `go test -race ./...` in `rhino/mcp-server`.
The dialer is tested against an in-process fake bridge (`internal/dialer/dialer_test.go`'s
`fakeBridge`) that speaks the real wire protocol: auth, register, ping, request echo.

**MCP Bridge (C#):** everything behind the `Core`/`RhinoAdapter` seam, xUnit,
`dotnet test rhino/mcp-bridge`. **RhinoCommon is a managed NuGet package, so the test assembly
references it and still loads** — Revit's "the whole assembly is silently skipped" trap does not
exist here, and the suite runs in CI on macOS and Windows. The constraint that remains: most
RhinoCommon *calls* P/Invoke into Rhino's native core and throw without a live Rhino, so tests use
RhinoCommon for types only and fakes for behaviour. The connection state machine runs over an
in-memory duplex pipe (`DuplexPipeStream`), no socket.

CI asserts on the executed test *count* (trx), never the exit code.

### Tier 2 — live harness, on the dev Mac per PR

`rhino/test-harness/` (Go, `-tags harness`): spawns the real server binary, speaks MCP over its stdio,
against the running Rhino. Assumes a Rhino with the bridge loaded and a model open; **skips** cleanly
otherwise. **Runs natively on the Mac with no VM**, so harness-gated PRs paste its output rather than
deferring it to release time. Windows gets a live pass before each release (dev-environment.md).

```sh
cd rhino/mcp-server && go build -o mcp-server-mac ./cmd/mcp-server
cd ../test-harness && go test -tags harness ./... -v -broker-exe ../mcp-server/mcp-server-mac
```

Driving Rhino from a test: the `rhinocode` CLI (`script <file>`, `command <name>`) runs on Rhino's
main thread; results come back by file. `TestDocumentEventsRefreshTheRegistry` is the pattern.

## Per-stage workflow

As the Revit skill's, with the environment differences:

1. Questions up front, once.
2. Implement TDD-first. Server work and bridge work touch disjoint directories and can run in
   parallel worktrees — **both** sides, because nothing here is bound to a VM share. The deploy
   script builds from whatever checkout it is run from.
3. Classify the work (groundbreaking / additive) in the PR description.
4. Groundbreaking only: `/simplify` before opening the PR.
5. Open the PR; normal git hygiene.
6. Independent code-review agent, findings posted to the PR. Docs-only PRs too.
7. Merging needs this session's user's authorization, per PR unless pre-granted. Never inferred.

## Deploying and iterating (Mac)

```sh
rhino/dev-tooling/deploy-plugin.sh            # build Release, yak build+install, restart Rhino
rhino/dev-tooling/deploy-plugin.sh --no-restart
```

The restart discards any unsaved document (it clicks the keep/delete sheet's Delete) and opens a new
model from the template chooser. After it returns, `MCPBridgeStatus` in Rhino's command line shows the
port, connection count and bridge version; `~/Library/Application Support/Connectors/Rhino/` holds
`instances/<pid>.json`, `connection.log` and `startup-errors.log`.

## PR review checklist

- [ ] Unit tests for new `Core`/`RhinoAdapter` (bridge) or `internal/*` (server) logic, written first.
- [ ] If the change touches execution, undo, the Python host, the listener/dialer, Grasshopper, or
      file exchange — was the live harness run, and is its output in the PR?
- [ ] Naming matches `CONVENTIONS.md` (Bridge vs. Server; `Rhino.MCPBridge.*`).
- [ ] New automatic-resolution behaviour follows observability-over-silence.
- [ ] Every new error/notice/log record uses the §01 shape with a `remedy` where there is a next step.
- [ ] Anything platform-specific has both OS branches (or an explicit, tested skip on the other).
- [ ] If the change refines a PRD decision or contradicts a spike finding, `rhino/docs/PRD.md` (and
      the findings file) are updated in the same PR — the Revit drift cleanup (#273) is the cost of not.
- [ ] Temporary scaffolding stripped; throwaway probe code lives under `docs/spikes/`, not in `src/`.

## Keeping these files current

Add a caveat the first time a symptom costs more than ten minutes. Move a rule from `caveats.md` into
"Engineering rules" once it has bitten twice. When a phase lands, update "Testing strategy" with the
harness cases it added and `dev-environment.md` with any new tooling. Keep the Mac/Windows split
explicit in every section that has one.
