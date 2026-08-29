# dev-tooling — this project's own dev-environment scripts

**Not product tooling.** Everything in this directory automates *this project's own
development environment* — a Mac host driving a Parallels Windows VM that runs Revit, over
`prlctl` and a Parallels shared folder — so an agent (or a human) can build, redeploy, and
live-verify the add-in without touching the VM by hand. None of it ships to users, none of
it is needed to build or install the connector (see
[`../docs/quickstart.md`](../docs/quickstart.md) for that), and most of it makes
assumptions (Parallels shared-network IPs, UNC share aliases, `C:\dev\` paths, a VM-side
launcher agent) that only hold in a topology like this repo's own.

If you are reproducing a similar Mac + Parallels setup, the full operational lore — gotchas
included, and there are many — lives in the `revit-connector-development` skill
(`.claude/skills/revit-connector-development/SKILL.md`).

| Script | Runs on | Purpose |
|---|---|---|
| `launcher-agent.ps1` | VM (deployed to `C:\dev\`, runs at logon) | watches a signal directory to start/stop Revit and the broker inside the interactive user's session — `prlctl exec` runs as SYSTEM and can't |
| `register-launcher-agent.ps1` | VM (run once, as the interactive user) | registers the launcher agent's AtLogOn scheduled task |
| `redeploy-and-verify.sh` | Mac (entry point) | one-command build → close → redeploy → relaunch → verify cycle; reacts to stale-registration markers by restarting the Mac-side broker |
| `redeploy-and-verify.ps1` | VM (invoked by the `.sh` via `prlctl exec`) | the VM-side half of that cycle |
| `launch-revit-discovery.bat` | VM | minimal manual launcher: sets remote-mode env vars and starts Revit |
