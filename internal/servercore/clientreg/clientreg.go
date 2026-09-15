// Package clientreg registers a connector's MCP server with the user's AI clients — Claude Code, Claude
// Desktop, and Codex — from one place. It is the single source of truth a connector's registration drives:
// the caller (a connector's installer, or the Rhino plug-in's MCPBridgeRegister command) shells
// `mcp-server register`, so "every client, equally" is implemented once, cross-platform, rather than
// duplicated in C# and PowerShell.
//
// It is connector-agnostic: the caller supplies a Config with the registration slug ("revit", "rhino")
// and any extra server arguments (Revit registers its broker with "--mode local"; Rhino registers with
// none). The package lives in the shared servercore module so every connector's server reuses it.
//
// Claude Code keeps user-scope MCP servers in ~/.claude.json and has its own `claude mcp add`, so that is
// what we drive for it. Claude Desktop (and Cowork, which reads the same file) has no CLI, so we merge the
// server into its claude_desktop_config.json directly — taking care with the Microsoft Store / MSIX build,
// whose real config is virtualized under the package's LocalCache (a write to %APPDATA%\Claude is silently
// ignored there — confirmed live on this project's VM). Codex keeps its MCP servers in ~/.codex/config.toml
// — shared by its CLI, desktop app, and IDE extension, so one `codex mcp add` registers all three at once —
// and we drive its `codex mcp add/remove/get` CLI (Codex owns the TOML merge, so other servers and
// hand-written keys are preserved and we need no TOML writer).
package clientreg

import (
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
)

// Config is the connector-specific registration identity. ServerName is the MCP registration slug
// (CONVENTIONS.md) — "revit", "rhino" — and ServerArgs are the arguments appended after the server path
// on both clients (the Claude Code `claude mcp add … -- <path> <args…>` line and the Claude Desktop
// entry's "args"). Revit passes {"--mode", "local"}; Rhino passes none.
type Config struct {
	ServerName string
	ServerArgs []string
}

// Client identifies a Claude client we can register with.
type Client string

const (
	ClaudeCode    Client = "claude-code"
	ClaudeDesktop Client = "claude-desktop"
	Codex         Client = "codex"
)

// Outcome is what happened for one client.
type Outcome struct {
	Client  Client `json:"client"`
	Action  string `json:"action"`            // registered | updated | unchanged | removed | not-installed | skipped | error
	Path    string `json:"path,omitempty"`    // the config file touched, when known
	Command string `json:"command,omitempty"` // the registered command (status/register)
	Detail  string `json:"detail,omitempty"`  // human note (why skipped, error text)
}

// Result is the outcome across every client, plus whether anything actually points at us.
type Result struct {
	Outcomes []Outcome `json:"outcomes"`
}

// Registered reports whether at least one client now points at serverPath (used by `register`).
func (r Result) Registered() bool {
	for _, o := range r.Outcomes {
		if o.Action == "registered" || o.Action == "updated" || o.Action == "unchanged" {
			return true
		}
	}
	return false
}

// CheckOK reports whether `register --check` should exit success: at least one client points at exactly
// this server path (unchanged) and none is stale (updated) or errored. A stale or broken registration is
// "needs attention", so the installer and the plug-in's on-load notice re-register rather than reading it
// as fine — the case a plain "is it present?" check would miss.
func (r Result) CheckOK() bool {
	anyCurrent := false
	for _, o := range r.Outcomes {
		switch o.Action {
		case "unchanged":
			anyCurrent = true
		case "updated", "error":
			return false
		}
	}
	return anyCurrent
}

// Env is the filesystem/OS context, injectable so tests can point at temp dirs and exercise every
// platform's path rules without running on that platform.
type Env struct {
	GOOS         string // "windows" | "darwin" | "linux"
	Home         string // user home
	AppData      string // %APPDATA% (Windows)
	LocalAppData string // %LOCALAPPDATA% (Windows)
	// lookClaude finds the `claude` CLI, or "" if absent. Overridable in tests.
	lookClaude func() string
	// runClaude runs `claude` with args, returning combined output and success. Overridable in tests.
	runClaude func(args ...string) (string, bool)
	// lookCodex finds the `codex` CLI, or "" if absent. Overridable in tests.
	lookCodex func() string
	// runCodex runs `codex` with args, returning combined output and success. Overridable in tests.
	runCodex func(args ...string) (string, bool)
}

// CurrentEnv is the real environment.
func CurrentEnv() Env {
	e := Env{
		GOOS:         runtime.GOOS,
		Home:         homeDir(),
		AppData:      os.Getenv("APPDATA"),
		LocalAppData: os.Getenv("LOCALAPPDATA"),
	}
	e.lookClaude = e.findClaude
	e.runClaude = e.execClaude
	e.lookCodex = e.findCodex
	e.runCodex = e.execCodex
	return e
}

func homeDir() string {
	if h, err := os.UserHomeDir(); err == nil {
		return h
	}
	return ""
}

// CodeConfigPath is Claude Code's user-scope config (~/.claude.json).
func (e Env) CodeConfigPath() string { return filepath.Join(e.Home, ".claude.json") }

// DesktopConfigPath is Claude Desktop's config for this platform. On Windows the Store/MSIX build
// virtualizes %APPDATA%\Claude, so prefer the package's LocalCache when a Claude package is present.
func (e Env) DesktopConfigPath() string {
	switch e.GOOS {
	case "windows":
		if e.LocalAppData != "" {
			pkgs := filepath.Join(e.LocalAppData, "Packages")
			if entries, err := os.ReadDir(pkgs); err == nil {
				var names []string
				for _, en := range entries {
					if en.IsDir() && strings.HasPrefix(en.Name(), "Claude") {
						names = append(names, en.Name())
					}
				}
				sort.Strings(names) // deterministic when several match
				for _, n := range names {
					cand := filepath.Join(pkgs, n, "LocalCache", "Roaming", "Claude")
					if isDir(cand) {
						return filepath.Join(cand, "claude_desktop_config.json")
					}
				}
			}
		}
		base := e.AppData
		if base == "" {
			base = filepath.Join(e.Home, "AppData", "Roaming")
		}
		return filepath.Join(base, "Claude", "claude_desktop_config.json")
	case "darwin":
		return filepath.Join(e.Home, "Library", "Application Support", "Claude", "claude_desktop_config.json")
	default:
		return filepath.Join(e.Home, ".config", "Claude", "claude_desktop_config.json")
	}
}

func isDir(p string) bool {
	fi, err := os.Stat(p)
	return err == nil && fi.IsDir()
}

// client is one registration target. Claude Code, Claude Desktop, and Codex are the three today; a new
// client is added by writing one adapter and appending it to clients() below — nothing else here, in
// main.go, or in the installer changes.
type client interface {
	Register(e Env, cfg Config, serverPath string) Outcome
	Unregister(e Env, cfg Config) Outcome
	Status(e Env, cfg Config, serverPath string) Outcome
}

// clients is the ordered set of known registration targets. Extend it to support another client.
func clients() []client {
	return []client{claudeCodeClient{}, claudeDesktopClient{}, codexClient{}}
}

// Register points every installed client at serverPath (with cfg.ServerArgs). Idempotent.
func Register(e Env, cfg Config, serverPath string) Result {
	return each(func(c client) Outcome { return c.Register(e, cfg, serverPath) })
}

// Unregister removes cfg.ServerName from every client.
func Unregister(e Env, cfg Config) Result {
	return each(func(c client) Outcome { return c.Unregister(e, cfg) })
}

// Status reports each client's state relative to serverPath (unchanged=current, updated=stale path,
// not-registered / not-installed / skipped otherwise).
func Status(e Env, cfg Config, serverPath string) Result {
	return each(func(c client) Outcome { return c.Status(e, cfg, serverPath) })
}

func each(fn func(client) Outcome) Result {
	var outs []Outcome
	for _, c := range clients() {
		outs = append(outs, fn(c))
	}
	return Result{Outcomes: outs}
}

// ---- Claude Code (via the claude CLI) ----

type claudeCodeClient struct{}

func (claudeCodeClient) Register(e Env, cfg Config, serverPath string) Outcome {
	return e.registerCode(cfg, serverPath)
}
func (claudeCodeClient) Unregister(e Env, cfg Config) Outcome { return e.unregisterCode(cfg) }
func (claudeCodeClient) Status(e Env, cfg Config, serverPath string) Outcome {
	return e.statusCode(cfg, serverPath)
}

func (e Env) registerCode(cfg Config, serverPath string) Outcome {
	o := Outcome{Client: ClaudeCode, Path: e.CodeConfigPath()}
	claude := e.lookClaude()
	if claude == "" {
		o.Action, o.Detail = "skipped", "the `claude` CLI was not found on PATH or in ~/.local/bin"
		return o
	}
	prev := readServerCommand(e.CodeConfigPath(), cfg.ServerName)
	// remove-then-add so a moved path replaces cleanly rather than clashing on the name.
	e.runClaude("mcp", "remove", cfg.ServerName, "--scope", "user")
	// `claude mcp add <name> --scope user -- <path> <serverArgs…>`
	addArgs := append([]string{"mcp", "add", cfg.ServerName, "--scope", "user", "--", serverPath}, cfg.ServerArgs...)
	if out, ok := e.runClaude(addArgs...); !ok {
		o.Action, o.Detail = "error", strings.TrimSpace(out)
		return o
	}
	o.Command = serverPath
	switch prev {
	case "":
		o.Action = "registered"
	case serverPath:
		o.Action = "unchanged"
	default:
		o.Action = "updated"
	}
	return o
}

func (e Env) unregisterCode(cfg Config) Outcome {
	o := Outcome{Client: ClaudeCode, Path: e.CodeConfigPath()}
	claude := e.lookClaude()
	if claude == "" {
		o.Action, o.Detail = "skipped", "the `claude` CLI was not found"
		return o
	}
	if readServerCommand(e.CodeConfigPath(), cfg.ServerName) == "" {
		o.Action = "unchanged"
		return o
	}
	e.runClaude("mcp", "remove", cfg.ServerName, "--scope", "user")
	o.Action = "removed"
	return o
}

func (e Env) statusCode(cfg Config, serverPath string) Outcome {
	o := Outcome{Client: ClaudeCode, Path: e.CodeConfigPath()}
	cmd := readServerCommand(e.CodeConfigPath(), cfg.ServerName)
	o.Command = cmd
	o.Action = classify(cmd, serverPath)
	return o
}

func (e Env) findClaude() string {
	name := "claude"
	if e.GOOS == "windows" {
		name = "claude.exe"
	}
	if p, err := exec.LookPath(name); err == nil {
		return p
	}
	// A GUI-launched Rhino misses the shell PATH; the CLI installs to ~/.local/bin by default.
	cand := filepath.Join(e.Home, ".local", "bin", name)
	if _, err := os.Stat(cand); err == nil {
		return cand
	}
	return ""
}

func (e Env) execClaude(args ...string) (string, bool) {
	claude := e.findClaude()
	if claude == "" {
		return "", false
	}
	out, err := exec.Command(claude, args...).CombinedOutput()
	return string(out), err == nil
}

// ---- Claude Desktop (direct config write) ----

type claudeDesktopClient struct{}

func (claudeDesktopClient) Register(e Env, cfg Config, serverPath string) Outcome {
	return e.registerDesktop(cfg, serverPath)
}
func (claudeDesktopClient) Unregister(e Env, cfg Config) Outcome { return e.unregisterDesktop(cfg) }
func (claudeDesktopClient) Status(e Env, cfg Config, serverPath string) Outcome {
	return e.statusDesktop(cfg, serverPath)
}

func (e Env) registerDesktop(cfg Config, serverPath string) Outcome {
	path := e.DesktopConfigPath()
	o := Outcome{Client: ClaudeDesktop, Path: path}
	if !isDir(filepath.Dir(path)) {
		o.Action, o.Detail = "not-installed", "no Claude Desktop config directory for this user"
		return o
	}
	obj, err := readJSONObject(path)
	if err != nil {
		o.Action, o.Detail = "error", err.Error()
		return o
	}
	prev := serverCommandIn(obj, cfg.ServerName)
	if err := setMCPServer(obj, cfg.ServerName, serverPath, cfg.ServerArgs); err != nil {
		o.Action, o.Detail = "error", err.Error()
		return o
	}
	backupOnce(path)
	if err := writeJSON(path, obj); err != nil {
		o.Action, o.Detail = "error", err.Error()
		return o
	}
	o.Command = serverPath
	switch prev {
	case "":
		o.Action = "registered"
	case serverPath:
		o.Action = "unchanged"
	default:
		o.Action = "updated"
	}
	return o
}

func (e Env) unregisterDesktop(cfg Config) Outcome {
	path := e.DesktopConfigPath()
	o := Outcome{Client: ClaudeDesktop, Path: path}
	if _, err := os.Stat(path); err != nil {
		o.Action = "unchanged" // no file → nothing to remove
		return o
	}
	obj, err := readJSONObject(path)
	if err != nil {
		o.Action, o.Detail = "error", err.Error()
		return o
	}
	if serverCommandIn(obj, cfg.ServerName) == "" {
		o.Action = "unchanged"
		return o
	}
	removeMCPServer(obj, cfg.ServerName)
	if err := writeJSON(path, obj); err != nil {
		o.Action, o.Detail = "error", err.Error()
		return o
	}
	o.Action = "removed"
	return o
}

func (e Env) statusDesktop(cfg Config, serverPath string) Outcome {
	path := e.DesktopConfigPath()
	o := Outcome{Client: ClaudeDesktop, Path: path}
	if !isDir(filepath.Dir(path)) {
		o.Action, o.Detail = "not-installed", "no Claude Desktop config directory for this user"
		return o
	}
	obj, err := readJSONObject(path)
	if err != nil {
		o.Action, o.Detail = "error", err.Error()
		return o
	}
	cmd := serverCommandIn(obj, cfg.ServerName)
	o.Command = cmd
	o.Action = classify(cmd, serverPath)
	return o
}

// ---- Codex (via the codex CLI, ~/.codex/config.toml) ----

// codexClient registers with Codex. Codex's CLI, desktop app, and IDE extension all read one shared
// ~/.codex/config.toml, so a single `codex mcp add` covers all three surfaces at once (unlike Claude,
// whose Code and Desktop are two separate configs and two adapters). We drive the `codex` CLI rather than
// editing the TOML: Codex owns the merge, so every other server and hand-written key is preserved and we
// need no TOML writer. When the `codex` CLI is not on PATH the adapter skips — a Codex desktop app with no
// CLI installed is not reached by this path (a documented v1 limitation, symmetric with Claude Code, which
// we likewise drive only through its CLI).
type codexClient struct{}

func (codexClient) Register(e Env, cfg Config, serverPath string) Outcome {
	return e.registerCodex(cfg, serverPath)
}
func (codexClient) Unregister(e Env, cfg Config) Outcome { return e.unregisterCodex(cfg) }
func (codexClient) Status(e Env, cfg Config, serverPath string) Outcome {
	return e.statusCodex(cfg, serverPath)
}

// CodexConfigPath is the config every Codex surface (CLI, app, IDE) shares.
func (e Env) CodexConfigPath() string { return filepath.Join(e.Home, ".codex", "config.toml") }

func (e Env) registerCodex(cfg Config, serverPath string) Outcome {
	o := Outcome{Client: Codex, Path: e.CodexConfigPath()}
	if e.lookCodexCLI() == "" {
		o.Action, o.Detail = "skipped", "the `codex` CLI was not found on PATH or in ~/.local/bin"
		return o
	}
	prev := e.codexServerCommand(cfg.ServerName)
	// remove-then-add so a moved path replaces cleanly rather than clashing on the name.
	e.runCodexCLI("mcp", "remove", cfg.ServerName)
	// `codex mcp add <name> -- <path> <serverArgs…>` — the stdio launcher form.
	addArgs := append([]string{"mcp", "add", cfg.ServerName, "--", serverPath}, cfg.ServerArgs...)
	if out, ok := e.runCodexCLI(addArgs...); !ok {
		o.Action, o.Detail = "error", strings.TrimSpace(out)
		return o
	}
	o.Command = serverPath
	switch prev {
	case "":
		o.Action = "registered"
	case serverPath:
		o.Action = "unchanged"
	default:
		o.Action = "updated"
	}
	return o
}

func (e Env) unregisterCodex(cfg Config) Outcome {
	o := Outcome{Client: Codex, Path: e.CodexConfigPath()}
	if e.lookCodexCLI() == "" {
		o.Action, o.Detail = "skipped", "the `codex` CLI was not found"
		return o
	}
	if e.codexServerCommand(cfg.ServerName) == "" {
		o.Action = "unchanged"
		return o
	}
	e.runCodexCLI("mcp", "remove", cfg.ServerName)
	o.Action = "removed"
	return o
}

func (e Env) statusCodex(cfg Config, serverPath string) Outcome {
	o := Outcome{Client: Codex, Path: e.CodexConfigPath()}
	if e.lookCodexCLI() == "" {
		o.Action, o.Detail = "skipped", "the `codex` CLI was not found"
		return o
	}
	cmd := e.codexServerCommand(cfg.ServerName)
	o.Command = cmd
	o.Action = classify(cmd, serverPath)
	return o
}

// codexServerCommand returns the stdio command Codex currently has registered for name, or "" if name is
// not registered (or Codex cannot be queried). `codex mcp get <name> --json` exits non-zero for an unknown
// server and otherwise prints the server config, whose transport.command is the launcher we set.
func (e Env) codexServerCommand(name string) string {
	out, ok := e.runCodexCLI("mcp", "get", name, "--json")
	if !ok {
		return "" // unknown server → non-zero exit → not registered
	}
	var got struct {
		Transport struct {
			Command string `json:"command"`
		} `json:"transport"`
	}
	if err := json.Unmarshal([]byte(out), &got); err != nil {
		return ""
	}
	return got.Transport.Command
}

// lookCodexCLI / runCodexCLI are nil-safe wrappers so an Env built by hand (without CurrentEnv wiring the
// codex funcs) skips Codex rather than panicking.
func (e Env) lookCodexCLI() string {
	if e.lookCodex == nil {
		return ""
	}
	return e.lookCodex()
}

func (e Env) runCodexCLI(args ...string) (string, bool) {
	if e.runCodex == nil {
		return "", false
	}
	return e.runCodex(args...)
}

func (e Env) findCodex() string {
	name := "codex"
	if e.GOOS == "windows" {
		name = "codex.exe"
	}
	if p, err := exec.LookPath(name); err == nil {
		return p
	}
	// Like the claude CLI, codex installs to ~/.local/bin, which a GUI-launched process may miss on PATH.
	cand := filepath.Join(e.Home, ".local", "bin", name)
	if _, err := os.Stat(cand); err == nil {
		return cand
	}
	return ""
}

func (e Env) execCodex(args ...string) (string, bool) {
	codex := e.findCodex()
	if codex == "" {
		return "", false
	}
	out, err := exec.Command(codex, args...).CombinedOutput()
	return string(out), err == nil
}

// ---- shared config helpers ----

// classify maps a registered command against the wanted path to a status action.
func classify(registered, want string) string {
	switch {
	case registered == "":
		return "not-registered"
	case pathsEqual(registered, want):
		return "unchanged"
	default:
		return "updated" // registered, but at a different (stale) path
	}
}

func pathsEqual(a, b string) bool {
	a, b = strings.TrimSpace(a), strings.TrimSpace(b)
	if runtime.GOOS == "windows" {
		return strings.EqualFold(a, b)
	}
	return a == b
}

// readServerCommand reads mcpServers.<name>.command from a config file, or "" (never errors — a missing
// or malformed file, or a non-string command, reads as "not registered"). For Claude Code's
// ~/.claude.json this is the TOP-LEVEL mcpServers, where `--scope user` servers live; project-scoped
// servers (under projects/<path>/mcpServers) are intentionally not consulted, since we register at user
// scope.
func readServerCommand(path, name string) string {
	cfg, err := readJSONObject(path)
	if err != nil {
		return ""
	}
	return serverCommandIn(cfg, name)
}

func serverCommandIn(cfg map[string]any, name string) string {
	servers, ok := cfg["mcpServers"].(map[string]any)
	if !ok {
		return ""
	}
	entry, ok := servers[name].(map[string]any)
	if !ok {
		return ""
	}
	cmd, _ := entry["command"].(string)
	return cmd
}

// setMCPServer merges a stdio server for name→command (with args) into cfg, creating mcpServers if
// absent/null and leaving every other server and top-level key intact. It refuses (errors) if mcpServers
// is present but is not a JSON object — a hand-corrupted config — rather than silently replacing whatever
// was there.
func setMCPServer(cfg map[string]any, name, command string, args []string) error {
	if existing, present := cfg["mcpServers"]; present && existing != nil {
		if _, ok := existing.(map[string]any); !ok {
			return fmt.Errorf("mcpServers is present but not a JSON object; refusing to overwrite it")
		}
	}
	servers, ok := cfg["mcpServers"].(map[string]any)
	if !ok || servers == nil {
		servers = map[string]any{}
		cfg["mcpServers"] = servers
	}
	// A []any (not []string) so the value marshals to a JSON array; always non-nil so it serializes as
	// [] rather than null when there are no args.
	jsonArgs := make([]any, 0, len(args))
	for _, a := range args {
		jsonArgs = append(jsonArgs, a)
	}
	servers[name] = map[string]any{"type": "stdio", "command": command, "args": jsonArgs}
	return nil
}

func removeMCPServer(cfg map[string]any, name string) {
	if servers, ok := cfg["mcpServers"].(map[string]any); ok {
		delete(servers, name)
	}
}

// readJSONObject reads a JSON object file. A missing file is an empty object (not an error); a present
// but unparseable file is an error, so we never silently clobber a config we couldn't understand.
func readJSONObject(path string) (map[string]any, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return map[string]any{}, nil
		}
		return nil, err
	}
	if len(strings.TrimSpace(string(b))) == 0 {
		return map[string]any{}, nil
	}
	var m map[string]any
	if err := json.Unmarshal(b, &m); err != nil {
		return nil, fmt.Errorf("%s is not valid JSON: %w", path, err)
	}
	if m == nil {
		m = map[string]any{}
	}
	return m, nil
}

// writeJSON writes indented JSON (no BOM, 2-space, the style these clients use), atomically: a temp file
// in the same directory then a rename, so a crash or a full disk can never leave a half-written config.
// A symlinked config is resolved to its target so the rename replaces the target, not the link.
func writeJSON(path string, cfg map[string]any) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	b, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return err
	}
	b = append(b, '\n')

	target := path
	if resolved, rerr := filepath.EvalSymlinks(path); rerr == nil {
		target = resolved // existing symlink → write through to its target
	}
	tmp, err := os.CreateTemp(filepath.Dir(target), ".mcpbridge-*.json")
	if err != nil {
		return err
	}
	tmpName := tmp.Name()
	defer os.Remove(tmpName) // harmless no-op once the rename has moved it
	if _, err := tmp.Write(b); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}
	return os.Rename(tmpName, target)
}

// backupOnce copies path to path.mcpbridge.bak the first time only, so a re-register never overwrites the
// pristine pre-install backup. Best effort.
func backupOnce(path string) {
	bak := path + ".mcpbridge.bak"
	if _, err := os.Stat(bak); err == nil {
		return
	}
	if b, err := os.ReadFile(path); err == nil {
		_ = os.WriteFile(bak, b, 0o644)
	}
}
