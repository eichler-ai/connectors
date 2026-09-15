package clientreg

import (
	"encoding/json"
	"os"
	"path/filepath"
	"slices"
	"testing"
)

// testCfg is the connector config the tests register with (the Rhino slug, no extra args). Tests that
// exercise extra server args (Revit-style) build their own Config.
var testCfg = Config{ServerName: "rhino"}

// mockCodeEnv returns an Env whose "claude" CLI is faked by maintaining ~/.claude.json directly, so
// register/status round-trips are exercised without a real claude binary. It captures both the server
// path and any args passed after "--", so a Config with ServerArgs can be asserted.
func mockCodeEnv(t *testing.T, home string) (Env, *[]string) {
	t.Helper()
	var lastAddArgs []string // the args recorded after "--" on the most recent `mcp add`
	e := Env{GOOS: "linux", Home: home}
	e.lookClaude = func() string { return "/fake/claude" }
	e.runClaude = func(args ...string) (string, bool) {
		if len(args) >= 2 && args[0] == "mcp" && args[1] == "add" {
			after := argsAfterDoubleDash(args)
			lastAddArgs = after
			var cmd string
			var rest []string
			if len(after) > 0 {
				cmd, rest = after[0], after[1:] // <path> <serverArgs…>
			}
			cfg, _ := readJSONObject(e.CodeConfigPath())
			// mcp add stores the name from the positional after "mcp add".
			_ = setMCPServer(cfg, args[2], cmd, rest)
			_ = writeJSON(e.CodeConfigPath(), cfg)
		}
		if len(args) >= 3 && args[0] == "mcp" && args[1] == "remove" {
			cfg, _ := readJSONObject(e.CodeConfigPath())
			removeMCPServer(cfg, args[2])
			_ = writeJSON(e.CodeConfigPath(), cfg)
		}
		return "", true
	}
	return e, &lastAddArgs
}

// mockCodexEnv returns an Env whose "codex" CLI is faked by an in-memory name→command map, exercising the
// `codex mcp add/remove/get --json` round-trip the adapter drives without a real codex binary. The returned
// pointer captures the args after "--" on the most recent `mcp add`, so a Config with ServerArgs can be
// asserted (as the Claude mock does).
func mockCodexEnv(t *testing.T) (Env, *[]string) {
	t.Helper()
	servers := map[string]string{} // name → registered stdio command
	var lastAddArgs []string
	e := Env{GOOS: "linux", Home: t.TempDir()}
	e.lookCodex = func() string { return "/fake/codex" }
	e.runCodex = func(args ...string) (string, bool) {
		switch {
		case len(args) >= 3 && args[0] == "mcp" && args[1] == "add":
			after := argsAfterDoubleDash(args)
			lastAddArgs = after
			if len(after) > 0 {
				servers[args[2]] = after[0] // <path> is the command; further args are launcher args
			}
			return "", true
		case len(args) >= 3 && args[0] == "mcp" && args[1] == "remove":
			if _, ok := servers[args[2]]; !ok {
				return "Error: No MCP server named '" + args[2] + "' found.", false
			}
			delete(servers, args[2])
			return "", true
		case len(args) >= 4 && args[0] == "mcp" && args[1] == "get" && args[3] == "--json":
			cmd, ok := servers[args[2]]
			if !ok {
				return "Error: No MCP server named '" + args[2] + "' found.", false
			}
			js, _ := json.Marshal(map[string]any{
				"name":      args[2],
				"transport": map[string]any{"type": "stdio", "command": cmd},
			})
			return string(js), true
		}
		return "", false
	}
	return e, &lastAddArgs
}

// argsAfterDoubleDash returns the elements following the first "--" in args (nil if none).
func argsAfterDoubleDash(args []string) []string {
	for i, a := range args {
		if a == "--" {
			return args[i+1:]
		}
	}
	return nil
}

func TestDesktopConfigPath_platformSelection(t *testing.T) {
	// Windows without a Claude MSIX package → the plain %APPDATA% path.
	winHome := t.TempDir()
	win := Env{GOOS: "windows", Home: winHome, AppData: filepath.Join(winHome, "AppData", "Roaming"), LocalAppData: filepath.Join(winHome, "AppData", "Local")}
	wantPlain := filepath.Join(win.AppData, "Claude", "claude_desktop_config.json")
	if got := win.DesktopConfigPath(); got != wantPlain {
		t.Errorf("windows (no MSIX) = %q, want %q", got, wantPlain)
	}

	// Windows WITH a Store/MSIX Claude package present → its virtualized LocalCache path wins.
	pkg := filepath.Join(win.LocalAppData, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude")
	if err := os.MkdirAll(pkg, 0o755); err != nil {
		t.Fatal(err)
	}
	wantMSIX := filepath.Join(pkg, "claude_desktop_config.json")
	if got := win.DesktopConfigPath(); got != wantMSIX {
		t.Errorf("windows (MSIX present) = %q, want %q", got, wantMSIX)
	}

	// macOS and Linux.
	macHome := "/Users/x"
	if got := (Env{GOOS: "darwin", Home: macHome}).DesktopConfigPath(); got != macHome+"/Library/Application Support/Claude/claude_desktop_config.json" {
		t.Errorf("darwin = %q", got)
	}
	if got := (Env{GOOS: "linux", Home: "/home/x"}).DesktopConfigPath(); got != "/home/x/.config/Claude/claude_desktop_config.json" {
		t.Errorf("linux = %q", got)
	}
}

func TestDesktop_registerMergesAndIsIdempotent(t *testing.T) {
	home := t.TempDir()
	e := Env{GOOS: "linux", Home: home}
	cfgPath := e.DesktopConfigPath()
	// Claude Desktop is "installed": its config dir exists, with a pre-existing unrelated server + key.
	if err := os.MkdirAll(filepath.Dir(cfgPath), 0o755); err != nil {
		t.Fatal(err)
	}
	seed := map[string]any{
		"theme":      "dark",
		"mcpServers": map[string]any{"other": map[string]any{"type": "stdio", "command": "/keep/me"}},
	}
	if err := writeJSON(cfgPath, seed); err != nil {
		t.Fatal(err)
	}

	if o := e.registerDesktop(testCfg, "/pkg/mcp-server"); o.Action != "registered" {
		t.Fatalf("first register action = %q (%s)", o.Action, o.Detail)
	}
	cfg, _ := readJSONObject(cfgPath)
	// rhino added...
	if serverCommandIn(cfg, testCfg.ServerName) != "/pkg/mcp-server" {
		t.Errorf("rhino command = %q", serverCommandIn(cfg, testCfg.ServerName))
	}
	// ...without disturbing the other server or the top-level key.
	if serverCommandIn(cfg, "other") != "/keep/me" {
		t.Error("the pre-existing 'other' server was clobbered")
	}
	if cfg["theme"] != "dark" {
		t.Error("a top-level key was clobbered")
	}
	// Backup was taken once, capturing the pristine pre-install file (no rhino in it).
	bak, err := readJSONObject(cfgPath + ".mcpbridge.bak")
	if err != nil || serverCommandIn(bak, testCfg.ServerName) != "" || serverCommandIn(bak, "other") != "/keep/me" {
		t.Errorf("backup should be the pristine pre-install config: %v (%v)", bak, err)
	}

	// Re-register at the SAME path → unchanged; DIFFERENT path → updated.
	if o := e.registerDesktop(testCfg, "/pkg/mcp-server"); o.Action != "unchanged" {
		t.Errorf("re-register same path = %q, want unchanged", o.Action)
	}
	if o := e.registerDesktop(testCfg, "/new/mcp-server"); o.Action != "updated" {
		t.Errorf("re-register new path = %q, want updated", o.Action)
	}
}

func TestDesktop_notInstalledWhenNoConfigDir(t *testing.T) {
	e := Env{GOOS: "linux", Home: t.TempDir()} // config dir does NOT exist
	if o := e.registerDesktop(testCfg, "/pkg/mcp-server"); o.Action != "not-installed" {
		t.Errorf("register with no Desktop dir = %q, want not-installed", o.Action)
	}
	if o := e.statusDesktop(testCfg, "/pkg/mcp-server"); o.Action != "not-installed" {
		t.Errorf("status with no Desktop dir = %q, want not-installed", o.Action)
	}
}

func TestDesktop_unregisterLeavesOthersIntact(t *testing.T) {
	home := t.TempDir()
	e := Env{GOOS: "linux", Home: home}
	cfgPath := e.DesktopConfigPath()
	_ = os.MkdirAll(filepath.Dir(cfgPath), 0o755)
	_ = writeJSON(cfgPath, map[string]any{"mcpServers": map[string]any{"other": map[string]any{"command": "/keep"}}})
	_ = e.registerDesktop(testCfg, "/pkg/mcp-server")

	if o := e.unregisterDesktop(testCfg); o.Action != "removed" {
		t.Fatalf("unregister = %q, want removed", o.Action)
	}
	cfg, _ := readJSONObject(cfgPath)
	if serverCommandIn(cfg, testCfg.ServerName) != "" {
		t.Error("rhino should be gone")
	}
	if serverCommandIn(cfg, "other") != "/keep" {
		t.Error("the 'other' server should remain")
	}
	// Second unregister is a no-op.
	if o := e.unregisterDesktop(testCfg); o.Action != "unchanged" {
		t.Errorf("second unregister = %q, want unchanged", o.Action)
	}
}

func TestCode_registerStatusRoundTrip(t *testing.T) {
	home := t.TempDir()
	e, _ := mockCodeEnv(t, home)

	if o := e.registerCode(testCfg, "/pkg/mcp-server"); o.Action != "registered" {
		t.Fatalf("register = %q (%s)", o.Action, o.Detail)
	}
	if o := e.statusCode(testCfg, "/pkg/mcp-server"); o.Action != "unchanged" {
		t.Errorf("status after register = %q, want unchanged (current)", o.Action)
	}
	if o := e.statusCode(testCfg, "/moved/mcp-server"); o.Action != "updated" {
		t.Errorf("status at a different path = %q, want updated (stale)", o.Action)
	}
	if o := e.unregisterCode(testCfg); o.Action != "removed" {
		t.Errorf("unregister = %q, want removed", o.Action)
	}
	if o := e.statusCode(testCfg, "/pkg/mcp-server"); o.Action != "not-registered" {
		t.Errorf("status after unregister = %q, want not-registered", o.Action)
	}
}

// TestConfig_serverArgsFlowToBothClients proves the connector-agnostic extraction: a Config with extra
// args (Revit registers its broker with "--mode local") lands after "--" on the Claude Code `mcp add`
// line AND in the Claude Desktop entry's "args" array — while a Config with no args still writes "args": [].
func TestCodex_registerStatusRoundTrip(t *testing.T) {
	e, _ := mockCodexEnv(t)

	if o := e.registerCodex(testCfg, "/pkg/mcp-server"); o.Action != "registered" {
		t.Fatalf("register = %q (%s)", o.Action, o.Detail)
	}
	if o := e.statusCodex(testCfg, "/pkg/mcp-server"); o.Action != "unchanged" {
		t.Errorf("status after register = %q, want unchanged (current)", o.Action)
	}
	if o := e.statusCodex(testCfg, "/moved/mcp-server"); o.Action != "updated" {
		t.Errorf("status at a different path = %q, want updated (stale)", o.Action)
	}
	// Re-register at the same path → unchanged; at a different path → updated (remove-then-add replaces).
	if o := e.registerCodex(testCfg, "/pkg/mcp-server"); o.Action != "unchanged" {
		t.Errorf("re-register same path = %q, want unchanged", o.Action)
	}
	if o := e.registerCodex(testCfg, "/new/mcp-server"); o.Action != "updated" {
		t.Errorf("re-register new path = %q, want updated", o.Action)
	}
	if o := e.unregisterCodex(testCfg); o.Action != "removed" {
		t.Errorf("unregister = %q, want removed", o.Action)
	}
	if o := e.statusCodex(testCfg, "/new/mcp-server"); o.Action != "not-registered" {
		t.Errorf("status after unregister = %q, want not-registered", o.Action)
	}
	// Second unregister is a no-op.
	if o := e.unregisterCodex(testCfg); o.Action != "unchanged" {
		t.Errorf("second unregister = %q, want unchanged", o.Action)
	}
}

func TestCodex_skippedWhenCodexAbsent(t *testing.T) {
	e := Env{GOOS: "linux", Home: t.TempDir(), lookCodex: func() string { return "" }}
	if o := e.registerCodex(testCfg, "/pkg/mcp-server"); o.Action != "skipped" {
		t.Errorf("register with no codex CLI = %q, want skipped", o.Action)
	}
	if o := e.statusCodex(testCfg, "/pkg/mcp-server"); o.Action != "skipped" {
		t.Errorf("status with no codex CLI = %q, want skipped", o.Action)
	}
	if o := e.unregisterCodex(testCfg); o.Action != "skipped" {
		t.Errorf("unregister with no codex CLI = %q, want skipped", o.Action)
	}
}

// TestCodex_serverArgsFlowToAdd proves a Config's extra args (Revit's "--mode local") land after "--" on
// the `codex mcp add` line, exactly as they do for the Claude clients.
func TestCodex_serverArgsFlowToAdd(t *testing.T) {
	revit := Config{ServerName: "revit", ServerArgs: []string{"--mode", "local"}}
	e, lastAdd := mockCodexEnv(t)
	if o := e.registerCodex(revit, "/pkg/mcp-server.exe"); o.Action != "registered" {
		t.Fatalf("register = %q (%s)", o.Action, o.Detail)
	}
	wantAdd := []string{"/pkg/mcp-server.exe", "--mode", "local"}
	if got := *lastAdd; !slices.Equal(got, wantAdd) {
		t.Errorf("codex mcp add args after -- = %v, want %v", got, wantAdd)
	}
}

func TestConfig_serverArgsFlowToBothClients(t *testing.T) {
	revit := Config{ServerName: "revit", ServerArgs: []string{"--mode", "local"}}

	// Claude Code: the args after "--" are exactly <path> then the server args.
	home := t.TempDir()
	e, lastAdd := mockCodeEnv(t, home)
	if o := e.registerCode(revit, "/pkg/mcp-server.exe"); o.Action != "registered" {
		t.Fatalf("register = %q (%s)", o.Action, o.Detail)
	}
	wantAdd := []string{"/pkg/mcp-server.exe", "--mode", "local"}
	if got := *lastAdd; !slices.Equal(got, wantAdd) {
		t.Errorf("claude mcp add args after -- = %v, want %v", got, wantAdd)
	}

	// Claude Desktop: the entry's args array carries the server args verbatim.
	de := Env{GOOS: "linux", Home: t.TempDir()}
	cfgPath := de.DesktopConfigPath()
	if err := os.MkdirAll(filepath.Dir(cfgPath), 0o755); err != nil {
		t.Fatal(err)
	}
	if o := de.registerDesktop(revit, "/pkg/mcp-server.exe"); o.Action != "registered" {
		t.Fatalf("desktop register = %q (%s)", o.Action, o.Detail)
	}
	obj, _ := readJSONObject(cfgPath)
	entry := obj["mcpServers"].(map[string]any)["revit"].(map[string]any)
	gotArgs, _ := json.Marshal(entry["args"])
	if string(gotArgs) != `["--mode","local"]` {
		t.Errorf("desktop args = %s, want [\"--mode\",\"local\"]", gotArgs)
	}

	// A no-args Config still serializes args as [] (never null).
	de2 := Env{GOOS: "linux", Home: t.TempDir()}
	_ = os.MkdirAll(filepath.Dir(de2.DesktopConfigPath()), 0o755)
	_ = de2.registerDesktop(testCfg, "/pkg/rhino")
	obj2, _ := readJSONObject(de2.DesktopConfigPath())
	entry2 := obj2["mcpServers"].(map[string]any)["rhino"].(map[string]any)
	if a, _ := json.Marshal(entry2["args"]); string(a) != `[]` {
		t.Errorf("no-args desktop args = %s, want []", a)
	}
}

func TestCode_skippedWhenClaudeAbsent(t *testing.T) {
	e := Env{GOOS: "linux", Home: t.TempDir(), lookClaude: func() string { return "" }}
	if o := e.registerCode(testCfg, "/pkg/mcp-server"); o.Action != "skipped" {
		t.Errorf("register with no claude CLI = %q, want skipped", o.Action)
	}
}

func TestRegister_coversEveryClient(t *testing.T) {
	// Register returns one outcome per known client, so a new client can't be silently forgotten.
	e := Env{GOOS: "linux", Home: t.TempDir(), lookClaude: func() string { return "" }}
	got := map[Client]bool{}
	for _, o := range Register(e, testCfg, "/pkg/mcp-server").Outcomes {
		got[o.Client] = true
	}
	for _, want := range []Client{ClaudeCode, ClaudeDesktop, Codex} {
		if !got[want] {
			t.Errorf("Register produced no outcome for %s", want)
		}
	}
}

func TestServerCommandIn_toleratesMalformed(t *testing.T) {
	cases := []string{
		`{}`,
		`{"mcpServers": null}`,
		`{"mcpServers": {"rhino": {"command": 123}}}`, // non-string command
		`{"mcpServers": {"rhino": {"command": ["x"]}}}`,
		`{"mcpServers": {"rhino": {}}}`, // no command key
		`{"mcpServers": {"other": {"command": "/x"}}}`,
	}
	for _, c := range cases {
		var m map[string]any
		if err := json.Unmarshal([]byte(c), &m); err != nil {
			t.Fatalf("test data not JSON: %s", c)
		}
		if cmd := serverCommandIn(m, testCfg.ServerName); cmd != "" {
			t.Errorf("serverCommandIn(%s) = %q, want empty", c, cmd)
		}
	}
}

func TestDesktop_refusesNonObjectMcpServers(t *testing.T) {
	home := t.TempDir()
	e := Env{GOOS: "linux", Home: home}
	cfgPath := e.DesktopConfigPath()
	_ = os.MkdirAll(filepath.Dir(cfgPath), 0o755)
	// A corrupted config where mcpServers is an array, not an object. Valid JSON, so it parses — the
	// engine must refuse rather than clobber the array.
	original := `{"mcpServers": ["oops"]}`
	_ = os.WriteFile(cfgPath, []byte(original), 0o644)

	if o := e.registerDesktop(testCfg, "/pkg/mcp-server"); o.Action != "error" {
		t.Fatalf("register onto non-object mcpServers = %q, want error", o.Action)
	}
	// The file is untouched — not overwritten with a fresh rhino-only config.
	got, _ := os.ReadFile(cfgPath)
	if string(got) != original {
		t.Errorf("config was modified despite the refusal:\n%s", got)
	}
}

func TestDesktop_statusSurfacesMalformed(t *testing.T) {
	home := t.TempDir()
	e := Env{GOOS: "linux", Home: home}
	cfgPath := e.DesktopConfigPath()
	_ = os.MkdirAll(filepath.Dir(cfgPath), 0o755)
	_ = os.WriteFile(cfgPath, []byte("{not json"), 0o644)
	if o := e.statusDesktop(testCfg, "/pkg/mcp-server"); o.Action != "error" {
		t.Errorf("status on malformed config = %q, want error (not a misleading not-registered)", o.Action)
	}
}

func TestCheckOK(t *testing.T) {
	mk := func(actions ...string) Result {
		var r Result
		for _, a := range actions {
			r.Outcomes = append(r.Outcomes, Outcome{Action: a})
		}
		return r
	}
	cases := []struct {
		name string
		r    Result
		want bool
	}{
		{"current on one, other absent", mk("unchanged", "not-installed"), true},
		{"stale path fails", mk("unchanged", "updated"), false},
		{"none registered fails", mk("not-registered", "not-installed"), false},
		{"error fails", mk("unchanged", "error"), false},
		{"both current", mk("unchanged", "unchanged"), true},
	}
	for _, c := range cases {
		if got := c.r.CheckOK(); got != c.want {
			t.Errorf("%s: CheckOK = %v, want %v", c.name, got, c.want)
		}
	}
}

func TestWriteJSON_replacesSymlinkTarget(t *testing.T) {
	if os.Getenv("GOOS") == "windows" {
		t.Skip("symlink semantics differ on Windows")
	}
	dir := t.TempDir()
	target := filepath.Join(dir, "real.json")
	link := filepath.Join(dir, "link.json")
	if err := os.WriteFile(target, []byte(`{"old":true}`), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(target, link); err != nil {
		t.Skipf("symlink unsupported: %v", err)
	}
	if err := writeJSON(link, map[string]any{"new": true}); err != nil {
		t.Fatal(err)
	}
	// The link is still a symlink (not replaced by a regular file), and its target now has the new content.
	fi, _ := os.Lstat(link)
	if fi.Mode()&os.ModeSymlink == 0 {
		t.Error("the symlink was replaced by a regular file; the target should have been written through")
	}
	m, _ := readJSONObject(target)
	if m["new"] != true {
		t.Errorf("target content = %v, want the new object", m)
	}
}

func TestReadJSONObject_missingAndMalformed(t *testing.T) {
	dir := t.TempDir()
	// Missing file → empty object, no error.
	if m, err := readJSONObject(filepath.Join(dir, "nope.json")); err != nil || len(m) != 0 {
		t.Errorf("missing file = %v, %v; want empty, nil", m, err)
	}
	// Present but malformed → error (never silently clobbered).
	bad := filepath.Join(dir, "bad.json")
	_ = os.WriteFile(bad, []byte("{not json"), 0o644)
	if _, err := readJSONObject(bad); err == nil {
		t.Error("malformed JSON should error, not read as empty")
	}
}
