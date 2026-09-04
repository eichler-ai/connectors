package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"
)

// This file is the in-repo form of the §5.6 probe
// (revit/docs/self-update-architecture.md): real mcp-server processes on one
// app-data dir, a real MCP session driven through a secondary's stdio pipes,
// the primary killed mid-session, and the client's NEXT request sent with no
// re-initialize. Before this change the promoted/reconnected process answered
// `method "tools/list" is invalid during session initialization`; with the
// replay it answers the tools list.
//
// It builds the binary from this package (or uses $MCP_SERVER_CONTINUITY_BIN,
// which is how the "before" was measured against a main-built binary), so it
// is slow; -short skips it.

var (
	builtServerOnce sync.Once
	builtServerPath string
	builtServerErr  error
)

func serverBinary(t *testing.T) string {
	t.Helper()
	if p := os.Getenv("MCP_SERVER_CONTINUITY_BIN"); p != "" {
		return p
	}
	builtServerOnce.Do(func() {
		dir, err := os.MkdirTemp("", "mcp-server-continuity-bin")
		if err != nil {
			builtServerErr = err
			return
		}
		builtServerPath = filepath.Join(dir, "mcp-server")
		if runtime.GOOS == "windows" {
			builtServerPath += ".exe"
		}
		out, err := exec.Command("go", "build", "-o", builtServerPath, ".").CombinedOutput()
		if err != nil {
			builtServerErr = fmt.Errorf("go build: %v\n%s", err, out)
		}
	})
	if builtServerErr != nil {
		t.Fatal(builtServerErr)
	}
	return builtServerPath
}

// mcpProc is one spawned mcp-server with a line-reading client on its stdout.
type mcpProc struct {
	t     *testing.T
	name  string
	cmd   *exec.Cmd
	stdin io.WriteCloser
	lines chan string
	log   string
}

func spawnServer(t *testing.T, bin, dataDir, name string) *mcpProc {
	t.Helper()
	logPath := filepath.Join(dataDir, name+".log")
	logFile, err := os.Create(logPath)
	if err != nil {
		t.Fatal(err)
	}
	cmd := exec.Command(bin, "-mode", "local", "-app-data-dir", dataDir)
	cmd.Stderr = logFile
	stdin, err := cmd.StdinPipe()
	if err != nil {
		t.Fatal(err)
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		t.Fatal(err)
	}
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	p := &mcpProc{t: t, name: name, cmd: cmd, stdin: stdin, lines: make(chan string, 64), log: logPath}
	go func() {
		sc := bufio.NewScanner(stdout)
		sc.Buffer(make([]byte, 1<<20), 16<<20)
		for sc.Scan() {
			p.lines <- sc.Text()
		}
		close(p.lines)
	}()
	t.Cleanup(func() {
		_ = cmd.Process.Kill()
		_ = cmd.Wait()
		logFile.Close()
	})
	return p
}

func (p *mcpProc) send(line string) {
	p.t.Helper()
	if _, err := io.WriteString(p.stdin, line); err != nil {
		p.t.Fatalf("%s: write stdin: %v", p.name, err)
	}
}

func (p *mcpProc) recv(timeout time.Duration) (map[string]any, bool) {
	p.t.Helper()
	select {
	case l, ok := <-p.lines:
		if !ok {
			return nil, false
		}
		var m map[string]any
		if err := json.Unmarshal([]byte(l), &m); err != nil {
			p.t.Fatalf("%s: non-JSON line on stdout: %q", p.name, l)
		}
		return m, true
	case <-time.After(timeout):
		p.t.Fatalf("%s: no response within %v", p.name, timeout)
		return nil, false
	}
}

func (p *mcpProc) kill() {
	_ = p.cmd.Process.Kill()
	_ = p.cmd.Wait()
}

// transitionLog is the election/role lines from a process's stderr log, for
// the failure message and for asserting which role it took.
func (p *mcpProc) transitionLog() string {
	b, _ := os.ReadFile(p.log)
	var keep []string
	for _, ln := range strings.Split(string(b), "\n") {
		for _, k := range []string{"primary:", "secondary:", "session-continuity", "takeover", "unresponsive", "election"} {
			if strings.Contains(ln, k) {
				keep = append(keep, ln)
				break
			}
		}
	}
	return strings.Join(keep, "\n")
}

func summarize(m map[string]any) string {
	if r, ok := m["result"].(map[string]any); ok {
		if tools, ok := r["tools"].([]any); ok {
			return fmt.Sprintf("OK tools/list (%d tools)", len(tools))
		}
		if si, ok := r["serverInfo"].(map[string]any); ok {
			return fmt.Sprintf("OK initialize (%v)", si["version"])
		}
		return "OK"
	}
	if e, ok := m["error"].(map[string]any); ok {
		return fmt.Sprintf("ERROR %v", e["message"])
	}
	b, _ := json.Marshal(m)
	return string(b)
}

// waitForSecondary blocks until the process's log says it is proxying through a primary.
func waitForSecondary(t *testing.T, p *mcpProc) {
	t.Helper()
	deadline := time.Now().Add(20 * time.Second)
	for time.Now().Before(deadline) {
		if strings.Contains(p.transitionLog(), "secondary: proxying stdio through primary") {
			return
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatalf("%s never became a secondary:\n%s", p.name, p.transitionLog())
}

// TestSessionSurvivesPrimaryDeathAcrossProcesses: A is primary, B proxies
// through A; the client initialized through B; A is killed; B's client sends
// its next request with no re-initialize and must get a real answer. With two
// processes B promotes itself, so this exercises the promoted-primary replay.
func TestSessionSurvivesPrimaryDeathAcrossProcesses(t *testing.T) {
	if testing.Short() {
		t.Skip("spawns real mcp-server processes; skipped with -short")
	}
	bin := serverBinary(t)
	dataDir := t.TempDir()

	a := spawnServer(t, bin, dataDir, "A")
	a.send(initLine)
	m, _ := a.recv(30 * time.Second)
	t.Logf("A initialize   : %s", summarize(m))
	a.send(initedLine)
	a.send(toolsListLine(1))
	m, _ = a.recv(30 * time.Second)
	t.Logf("A tools/list   : %s", summarize(m))

	b := spawnServer(t, bin, dataDir, "B")
	waitForSecondary(t, b)
	b.send(initLine)
	m, _ = b.recv(30 * time.Second)
	t.Logf("B initialize   : %s", summarize(m))
	if m["result"] == nil {
		t.Fatalf("B initialize via A failed: %v", m)
	}
	b.send(initedLine)
	b.send(toolsListLine(1))
	m, _ = b.recv(30 * time.Second)
	t.Logf("B tools/list   : %s (proxied via A)", summarize(m))

	t.Logf("--- killing primary A (pid %d) ---", a.cmd.Process.Pid)
	a.kill()

	// The client (B's owner) knows nothing of A's death; it just sends its
	// next request. B has to notice the drop, re-elect (promote, here), bring
	// up its own MCP server -- model loading included -- and serve it.
	b.send(toolsListLine(2))
	m, ok := b.recv(60 * time.Second)
	if !ok {
		t.Fatalf("B exited after A's death:\n%s", b.transitionLog())
	}
	t.Logf("B next request after A death (NO re-initialize): %s", summarize(m))
	if m["id"] != float64(2) {
		t.Fatalf("expected the answer to request 2 and nothing else first (a leaked duplicate initialize response?), got %v\n%s", m, b.transitionLog())
	}
	if m["result"] == nil {
		t.Fatalf("B's session did not survive A's death: %v\n%s", m, b.transitionLog())
	}

	// Still a live session, and no stray line arrived in between.
	b.send(toolsListLine(3))
	m, _ = b.recv(30 * time.Second)
	t.Logf("B tools/list #3 : %s", summarize(m))
	if m["id"] != float64(3) || m["result"] == nil {
		t.Fatalf("follow-up request failed: %v", m)
	}
	tl := b.transitionLog()
	if !strings.Contains(tl, "session-continuity: replaying") {
		t.Fatalf("B did not log a replay:\n%s", tl)
	}
	t.Logf("--- B.log (transition) ---\n%s", tl)
}

// TestSessionSurvivesPrimaryDeathWithAThirdProcess adds a second idle
// secondary C, so that when A dies B either promotes or reconnects as a
// secondary to C -- whichever wins the re-election. B's session must survive
// in both roles; the log says which one this run exercised.
func TestSessionSurvivesPrimaryDeathWithAThirdProcess(t *testing.T) {
	if testing.Short() {
		t.Skip("spawns real mcp-server processes; skipped with -short")
	}
	bin := serverBinary(t)
	dataDir := t.TempDir()

	a := spawnServer(t, bin, dataDir, "A")
	a.send(initLine)
	a.recv(30 * time.Second)
	a.send(initedLine)

	b := spawnServer(t, bin, dataDir, "B")
	waitForSecondary(t, b)
	c := spawnServer(t, bin, dataDir, "C")
	waitForSecondary(t, c)
	// C's client initializes too, so C is a live session that must also
	// survive, whichever role it lands in.
	c.send(initLine)
	if m, _ := c.recv(30 * time.Second); m["result"] == nil {
		t.Fatalf("C initialize: %v", m)
	}
	c.send(initedLine)

	b.send(initLine)
	if m, _ := b.recv(30 * time.Second); m["result"] == nil {
		t.Fatalf("B initialize: %v", m)
	}
	b.send(initedLine)
	b.send(toolsListLine(1))
	if m, _ := b.recv(30 * time.Second); m["result"] == nil {
		t.Fatalf("B tools/list via A: %v", m)
	}

	a.kill()

	for _, p := range []*mcpProc{b, c} {
		p.send(toolsListLine(2))
		m, ok := p.recv(60 * time.Second)
		if !ok {
			t.Fatalf("%s exited after A's death:\n%s", p.name, p.transitionLog())
		}
		t.Logf("%s next request after A death (NO re-initialize): %s", p.name, summarize(m))
		if m["id"] != float64(2) || m["result"] == nil {
			t.Fatalf("%s's session did not survive: %v\n%s", p.name, m, p.transitionLog())
		}
		role := "promoted to primary"
		if strings.Count(p.transitionLog(), "secondary: proxying stdio through primary") > 1 {
			role = "reconnected as secondary to the new primary"
		}
		t.Logf("%s: %s", p.name, role)
	}
}
