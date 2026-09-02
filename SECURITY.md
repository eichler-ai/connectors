# Security

## Reporting a vulnerability

Use GitHub's private vulnerability reporting on this repository (**Security → Report a
vulnerability**). Please do not open a public issue for anything you believe is exploitable
before it has been triaged.

## Trust model — read this before filing

The Revit connector's headline capability is **arbitrary C# execution inside a live Revit
process**, on purpose. It is a local, user-initiated development tool with a deliberately
unsandboxed script trust model (PRD §02/§10): the person who installs and runs it is handing
the connected agent full Revit API access. Reports of the form "a script can do X to the
document/process" are usually describing the product working as designed, not a vulnerability.

What the connector *does* promise, and what a report should therefore target:

- **Transport exposure.** In local mode the broker binds `127.0.0.1` only — never `0.0.0.0`.
  Anything that causes a non-loopback bind in local mode, or lets a remote peer reach the
  port without the token, is a real vulnerability.
- **Token authentication.** The broker mints a random token when it becomes primary and
  writes it to `broker.json`; every connecting party (Revit add-ins, secondary broker
  proxies) must present it before any other command is accepted. In local mode this only
  filters accidental cross-talk between unrelated same-user software — a malicious same-user
  process can read `broker.json` too, which is inside the accepted trust boundary. In
  **remote mode** the token is the only protection the port has, bounded by who can access
  the shared drive it rides on. Bypasses of the token check itself are real vulnerabilities.
- **The script-tier boundaries.** `ScriptApiDenylist` is a **guard against plausible
  mistakes, not a sandbox** — reflection can route around it, and that is an accepted,
  documented position (PRD §02), not a gap to report. What is enforced, and worth reporting
  if bypassable *without reflection*: a script may not construct its own
  `Transaction`/`TransactionGroup` (unconditional; a `SubTransaction` is allowed only inside a
  `using`), and the
  document-lifecycle members (`Document.Close`/`Save`/`SynchronizeWithCentral`/`Print`, …)
  require the request-level `confirm_lifecycle_actions` flag. Three independent review
  rounds have already closed non-reflection bypass routes through the connector's own public
  types (see PRD §14); a fourth instance of that class — plausible-looking, one-line,
  no-reflection reach to transaction or confirmation authority — is exactly the kind of
  report we want.

When in doubt, report it privately anyway — a triaged non-issue costs minutes; the full
security-model write-up is in [`revit/docs/PRD.md`](revit/docs/PRD.md) §10 and §14.

## Supported versions

Pre-1.0: only the current `main` branch receives fixes.
