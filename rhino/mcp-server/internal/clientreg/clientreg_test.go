package clientreg

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// mockCodeEnv returns an Env whose "claude" CLI is faked by maintaining ~/.claude.json directly, so
// register/status round-trips are exercised without a real claude binary.
func mockCodeEnv(t *testing.T, home string) Env {
	t.Helper()
	e := Env{GOOS: "linux", Home: home}
	e.lookClaude = func() string { return "/fake/claude" }
	e.runClaude = func(args ...string) (string, bool) {
		if len(args) >= 2 && args[0] == "mcp" && args[1] == "add" {
			cmd := args[len(args)-1] // the path after "--"
			cfg, _ := readJSONObject(e.CodeConfigPath())
			_ = setMCPServer(cfg, ServerName, cmd)
			_ = writeJSON(e.CodeConfigPath(), cfg)
		}
		if len(args) >= 2 && args[0] == "mcp" && args[1] == "remove" {
			cfg, _ := readJSONObject(e.CodeConfigPath())
			removeMCPServer(cfg, ServerName)
			_ = writeJSON(e.CodeConfigPath(), cfg)
		}
		return "", true
	}
	return e
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

	if o := e.registerDesktop("/pkg/mcp-server"); o.Action != "registered" {
		t.Fatalf("first register action = %q (%s)", o.Action, o.Detail)
	}
	cfg, _ := readJSONObject(cfgPath)
	// rhino added...
	if serverCommandIn(cfg, ServerName) != "/pkg/mcp-server" {
		t.Errorf("rhino command = %q", serverCommandIn(cfg, ServerName))
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
	if err != nil || serverCommandIn(bak, ServerName) != "" || serverCommandIn(bak, "other") != "/keep/me" {
		t.Errorf("backup should be the pristine pre-install config: %v (%v)", bak, err)
	}

	// Re-register at the SAME path → unchanged; DIFFERENT path → updated.
	if o := e.registerDesktop("/pkg/mcp-server"); o.Action != "unchanged" {
		t.Errorf("re-register same path = %q, want unchanged", o.Action)
	}
	if o := e.registerDesktop("/new/mcp-server"); o.Action != "updated" {
		t.Errorf("re-register new path = %q, want updated", o.Action)
	}
}

func TestDesktop_notInstalledWhenNoConfigDir(t *testing.T) {
	e := Env{GOOS: "linux", Home: t.TempDir()} // config dir does NOT exist
	if o := e.registerDesktop("/pkg/mcp-server"); o.Action != "not-installed" {
		t.Errorf("register with no Desktop dir = %q, want not-installed", o.Action)
	}
	if o := e.statusDesktop("/pkg/mcp-server"); o.Action != "not-installed" {
		t.Errorf("status with no Desktop dir = %q, want not-installed", o.Action)
	}
}

func TestDesktop_unregisterLeavesOthersIntact(t *testing.T) {
	home := t.TempDir()
	e := Env{GOOS: "linux", Home: home}
	cfgPath := e.DesktopConfigPath()
	_ = os.MkdirAll(filepath.Dir(cfgPath), 0o755)
	_ = writeJSON(cfgPath, map[string]any{"mcpServers": map[string]any{"other": map[string]any{"command": "/keep"}}})
	_ = e.registerDesktop("/pkg/mcp-server")

	if o := e.unregisterDesktop(); o.Action != "removed" {
		t.Fatalf("unregister = %q, want removed", o.Action)
	}
	cfg, _ := readJSONObject(cfgPath)
	if serverCommandIn(cfg, ServerName) != "" {
		t.Error("rhino should be gone")
	}
	if serverCommandIn(cfg, "other") != "/keep" {
		t.Error("the 'other' server should remain")
	}
	// Second unregister is a no-op.
	if o := e.unregisterDesktop(); o.Action != "unchanged" {
		t.Errorf("second unregister = %q, want unchanged", o.Action)
	}
}

func TestCode_registerStatusRoundTrip(t *testing.T) {
	home := t.TempDir()
	e := mockCodeEnv(t, home)

	if o := e.registerCode("/pkg/mcp-server"); o.Action != "registered" {
		t.Fatalf("register = %q (%s)", o.Action, o.Detail)
	}
	if o := e.statusCode("/pkg/mcp-server"); o.Action != "unchanged" {
		t.Errorf("status after register = %q, want unchanged (current)", o.Action)
	}
	if o := e.statusCode("/moved/mcp-server"); o.Action != "updated" {
		t.Errorf("status at a different path = %q, want updated (stale)", o.Action)
	}
	if o := e.unregisterCode(); o.Action != "removed" {
		t.Errorf("unregister = %q, want removed", o.Action)
	}
	if o := e.statusCode("/pkg/mcp-server"); o.Action != "not-registered" {
		t.Errorf("status after unregister = %q, want not-registered", o.Action)
	}
}

func TestCode_skippedWhenClaudeAbsent(t *testing.T) {
	e := Env{GOOS: "linux", Home: t.TempDir(), lookClaude: func() string { return "" }}
	if o := e.registerCode("/pkg/mcp-server"); o.Action != "skipped" {
		t.Errorf("register with no claude CLI = %q, want skipped", o.Action)
	}
}

func TestRegister_coversEveryClient(t *testing.T) {
	// Register returns one outcome per known client, so a new client can't be silently forgotten.
	e := Env{GOOS: "linux", Home: t.TempDir(), lookClaude: func() string { return "" }}
	got := map[Client]bool{}
	for _, o := range Register(e, "/pkg/mcp-server").Outcomes {
		got[o.Client] = true
	}
	for _, want := range []Client{ClaudeCode, ClaudeDesktop} {
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
		if cmd := serverCommandIn(m, ServerName); cmd != "" {
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

	if o := e.registerDesktop("/pkg/mcp-server"); o.Action != "error" {
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
	if o := e.statusDesktop("/pkg/mcp-server"); o.Action != "error" {
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
