# Phase 7 spike — Windows distribution (yak package + companion binary + Claude registration)

Live findings against **Rhino 8.35.26251.13001 for Windows**, on a Parallels **Windows 11 ARM64** VM
(Rhino x64 under emulation), 2026-09-13. Everything below was observed, not read: the steps ran on the
real VM over SSH as the interactive user `nicholas`, driving the bundled `yak.exe`, the real
`net8.0` plug-in built on the VM from `main` (09a6aeb), and the Windows `mcp-server.exe`
cross-compiled from the Mac (`GOOS=windows GOARCH=amd64 CGO_ENABLED=0` — the semsearch/ONNX stack is
pure-Go). The PoC package deliberately carried the **fat** 82 MB server binary (models embedded), i.e.
the worst case, not the slimmed one §15 proposes.

The question this spike answers: **is the §15 distribution plan (plan A — one yak package carries the
plug-in and the companion server binary; a Rhino command registers it with Claude) viable on Windows, or
does it need rework?** Verdict up front: **plan A works end to end on Windows, unmodified.** All five
checks passed.

## Access — how the VM is driven (see the dev skill's dev-environment.md)

`prlctl exec` runs as `NT AUTHORITY\SYSTEM` (wrong profile: yak/claude would target `systemprofile`), so
it is only good for admin bootstrap. The real driver is **`ssh nicholas@10.211.55.3`** (alias `rhino-vm`),
which runs as the logged-in user with the correct profile. OpenSSH Server was installed for this spike;
the two gotchas (firewall rule Private-scoped vs. a Public network; an admin's key must live in
`administrators_authorized_keys`) are written up in the dev skill.

## 1. yak accepts a large non-plug-in payload (§17 item 9 → §15)

**Resolved: yes, even un-slimmed.** A staging dir of the plug-in's product DLLs + the 82 MB
`mcp-server.exe` + a hand-written `manifest.yml` built with `yak build` into
`rhino-mcp-bridge-0.0.1-rh8_25-any.yak` (**62 MB**, the payload compresses well). `yak install` reported
`Successfully installed rhino-mcp-bridge (0.0.1)` and unpacked to
`C:\Users\nicholas\AppData\Roaming\McNeel\Rhinoceros\packages\8.0\rhino-mcp-bridge\0.0.1\` — the `.rhp`,
the product DLLs, **and `mcp-server.exe` intact at 82.4 MB** (17 files). yak's two "Content
version/name doesn't match manifest" lines are cosmetic (the assembly's informational version vs. the
manifest version), as phase 2 already noted. The distribution tag was `rh8_25-any` (Rhino 8.25 baseline,
any OS) from building against the current RhinoCommon.

Slimming the models out (§15) is therefore **not required for yak to accept the package** — it stays worth
doing for download speed (62 MB → ~a third), but it is no longer a correctness gate.

## 2. The plug-in loads cleanly from the combined package (§14 / §15)

Launching Rhino with the combined package installed wrote `instances\<pid>.json`
(`platform: windows`, `bridge_version` = the source rev, a live loopback port), `connection.log` showed
`listening on 127.0.0.1:<port>`, discovery synced, and `roslyn warm-up done`. **`startup-errors.log` was
absent (clean `OnLoad`).** The extra 82 MB executable sitting beside the `.rhp` in the package folder does
**not** interfere with plug-in loading — Rhino loads the `.rhp` and ignores the rest.

## 3. The unsigned Go binary runs — no Defender quarantine (§15 signing)

With Defender **real-time protection on**, the yak-installed `mcp-server.exe --version` ran (exit 0,
correct build string), the file was not quarantined before or after, and `Get-MpThreatDetection` recorded
**no detections**. `Get-AuthenticodeSignature` = `NotSigned`, as expected. A yak-installed file carries no
Mark-of-the-Web, so SmartScreen does not gate it either. So an unsigned Go server binary installs and
launches without friction on a default Windows 11 — signing (§15) stays a deferred nicety for trust
hygiene, not a launch blocker. *(Caveat: one dev VM; a locked-down corporate Defender policy or a
downloaded-with-MOTW copy could still differ.)*

## 4. `claude mcp add` registers a native exe and Claude connects (§15 MCPBridgeRegister)

The Claude CLI on the VM is a **native `claude.exe`** on PATH (`C:\Users\nicholas\.local\bin\claude.exe`),
not a `.cmd`/`.ps1` shim — so the Windows stdio-spawn fragility that plagues `npx`/`uvx`-wrapped MCP
servers does not apply. `claude mcp add rhino-poc -s user -- <installed exe>` wrote a clean
`{"type":"stdio","command":"…mcp-server.exe","args":[],"env":{}}` to `~/.claude.json`, and `claude mcp
list` reported **`rhino-poc: … - Connected`** — Claude launched the exe and completed the MCP handshake.
This is the mechanism `MCPBridgeRegister` will use; pointing `command` straight at the native exe is the
key simplification over the incumbents.

## 5. Full end to end — server dials into the plug-in, agent sees the instance

In one session (so the SSH-launched Rhino stays alive), Rhino was launched, and once it was listening the
yak-installed `mcp-server.exe` was driven over stdio (`initialize` → `notifications/initialized` →
`tools/call list_instances`). It returned the live Windows instance:

```
instance_id: ab295cb8-1abc-4eb1-bb88-91ac704ffbe3
platform: windows   rhino_version: 8.35.26251.13001   status: idle
```

The complete chain — **yak package (plug-in + server) → `yak install` → plug-in loads → server from the
same package dials in → agent lists the instance over MCP** — works on Windows.

## Notes, limits, and what still needs doing before the phase ships

- **SSH-launched Rhino is non-console** and does not survive its launching session, so the end-to-end must
  run inside one session (or Rhino must be launched in the console session). Fine for CI/spike driving;
  the release-gate harness already handles a real session (phase 2).
- **`documents: []`** in the instance — the #289 "programmatic launch may open no document" behaviour; a
  doc-open step (per the deploy script) populates it. Not a distribution concern.
- **Not yet tested:** the actual `MCPBridgeRegister` Rhino command (this spike ran `claude mcp add` by
  hand, which is what the command will shell out to); model-slimming (fetch-on-first-run); publishing to
  the public yak server (`yak push`, needs a Rhino Accounts login); auto-update-on-restart from a real
  source; the Mac half of plan A (notarization).
- **VM state:** the PoC package was uninstalled and scratch removed. The VM's repo checkout was moved from
  `rhino/phase-3-two-instance` (6f8f744, its prior state) to `main` (09a6aeb) to build a current artifact.
