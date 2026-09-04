package main

import (
	"os"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/singleton"
)

// Hosted-primary harness (revit/docs/self-update-architecture.md §5, Part B
// step 2): real mcp-server processes on one app-data dir, each case asserting
// on broker.json's pid and hosted flag rather than on log lines alone. Slow
// (it builds the binary and waits out real watcher ticks); -short skips it.

// waitForBroker polls broker.json until pred holds, or fails after timeout.
func waitForBroker(t *testing.T, dataDir string, timeout time.Duration, what string, pred func(singleton.BrokerInfo) bool) singleton.BrokerInfo {
	t.Helper()
	deadline := time.Now().Add(timeout)
	var last singleton.BrokerInfo
	for time.Now().Before(deadline) {
		if info, err := singleton.ReadBrokerJSON(dataDir); err == nil {
			last = info
			if pred(info) {
				return info
			}
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatalf("broker.json never reached %q within %v; last: pid=%d hosted=%v", what, timeout, last.PID, last.Hosted)
	return last
}

func isPrimary(p *mcpProc) func(singleton.BrokerInfo) bool {
	return func(i singleton.BrokerInfo) bool { return i.PID == p.cmd.Process.Pid }
}

// TestHostedServerStartedFirstIsPrimary (a): a -hosted server with nothing
// else running wins the lock and advertises hosted=true.
func TestHostedServerStartedFirstIsPrimary(t *testing.T) {
	if testing.Short() {
		t.Skip("spawns real mcp-server processes; skipped with -short")
	}
	bin := serverBinary(t)
	dataDir := t.TempDir()

	h := spawnServer(t, bin, dataDir, "H", "-hosted")
	info := waitForBroker(t, dataDir, 30*time.Second, "hosted primary", isPrimary(h))
	if !info.Hosted {
		t.Fatalf("hosted server is primary but broker.json hosted=%v", info.Hosted)
	}
	if _, err := os.Stat(singleton.HostedRequestPath(dataDir)); err == nil {
		t.Fatal("a hosted server that won outright left a yield request behind")
	}
	t.Logf("(a) broker.json: pid=%d hosted=%v", info.PID, info.Hosted)
	t.Logf("--- H.log ---\n%s", h.transitionLog())
}

// TestClientProxiesThroughHostedPrimary (b): a non-hosted client starting
// while the hosted primary runs becomes a secondary and its session works
// through it.
func TestClientProxiesThroughHostedPrimary(t *testing.T) {
	if testing.Short() {
		t.Skip("spawns real mcp-server processes; skipped with -short")
	}
	bin := serverBinary(t)
	dataDir := t.TempDir()

	h := spawnServer(t, bin, dataDir, "H", "-hosted")
	waitForBroker(t, dataDir, 30*time.Second, "hosted primary", isPrimary(h))

	c := spawnServer(t, bin, dataDir, "C")
	waitForSecondary(t, c)
	c.send(initLine)
	m, _ := c.recv(30 * time.Second)
	if m["result"] == nil {
		t.Fatalf("C initialize via hosted primary: %v", m)
	}
	c.send(initedLine)
	c.send(toolsListLine(1))
	m, _ = c.recv(30 * time.Second)
	t.Logf("(b) C tools/list via hosted primary: %s", summarize(m))
	if m["id"] != float64(1) || m["result"] == nil {
		t.Fatalf("C tools/list via hosted primary: %v", m)
	}
	info, _ := singleton.ReadBrokerJSON(dataDir)
	if info.PID != h.cmd.Process.Pid || !info.Hosted {
		t.Fatalf("broker.json changed hands: pid=%d hosted=%v, want hosted pid %d", info.PID, info.Hosted, h.cmd.Process.Pid)
	}
	if strings.Contains(c.transitionLog(), "primary: listening") {
		t.Fatalf("C became primary while a hosted primary was running:\n%s", c.transitionLog())
	}
}

// TestClientPrimaryYieldsToHostedServerAndSessionSurvives (c): the client
// starts first and is primary; a hosted server started second makes it step
// down; the hosted server becomes primary; and the client's MCP session --
// initialized against the client's own in-process server -- carries on through
// the hosted primary with no re-initialize.
func TestClientPrimaryYieldsToHostedServerAndSessionSurvives(t *testing.T) {
	if testing.Short() {
		t.Skip("spawns real mcp-server processes; skipped with -short")
	}
	bin := serverBinary(t)
	dataDir := t.TempDir()

	c := spawnServer(t, bin, dataDir, "C")
	c.send(initLine)
	m, _ := c.recv(30 * time.Second)
	t.Logf("C initialize (own server): %s", summarize(m))
	c.send(initedLine)
	c.send(toolsListLine(1))
	m, _ = c.recv(30 * time.Second)
	if m["id"] != float64(1) || m["result"] == nil {
		t.Fatalf("C tools/list as primary: %v", m)
	}
	before := waitForBroker(t, dataDir, 10*time.Second, "client primary", isPrimary(c))
	if before.Hosted {
		t.Fatalf("client primary advertised hosted=true")
	}
	t.Logf("(c) stage 1 broker.json: pid=%d (client C) hosted=%v", before.PID, before.Hosted)

	h := spawnServer(t, bin, dataDir, "H", "-hosted")
	// The yield watcher ticks every hostedYieldCheckInterval; allow several.
	after := waitForBroker(t, dataDir, 4*hostedYieldCheckInterval, "hosted primary", isPrimary(h))
	if !after.Hosted {
		t.Fatalf("hosted server took the lock but broker.json hosted=%v", after.Hosted)
	}
	t.Logf("(c) stage 2 broker.json: pid=%d (hosted H) hosted=%v", after.PID, after.Hosted)
	if _, err := os.Stat(singleton.HostedRequestPath(dataDir)); err == nil {
		t.Fatal("yield request left behind after the hosted server won")
	}

	// The client knows nothing of the hand-off: its next request has no
	// re-initialize and must be answered through the hosted primary.
	c.send(toolsListLine(2))
	m, ok := c.recv(60 * time.Second)
	if !ok {
		t.Fatalf("C exited during the yield:\n%s", c.transitionLog())
	}
	t.Logf("(c) C next request after yield (NO re-initialize): %s", summarize(m))
	if m["id"] != float64(2) || m["result"] == nil {
		t.Fatalf("C's session did not survive the yield: %v\n%s", m, c.transitionLog())
	}
	c.send(toolsListLine(3))
	m, _ = c.recv(30 * time.Second)
	if m["id"] != float64(3) || m["result"] == nil {
		t.Fatalf("follow-up request failed: %v", m)
	}

	tl := c.transitionLog()
	for _, want := range []string{"hosted-yield: hosted server pid", "session-continuity: replaying", "secondary: proxying stdio through primary"} {
		if !strings.Contains(tl, want) {
			t.Fatalf("C's log lacks %q:\n%s", want, tl)
		}
	}
	// The hosted server still holds it: the client did not re-win the lock.
	info, _ := singleton.ReadBrokerJSON(dataDir)
	if info.PID != h.cmd.Process.Pid {
		t.Fatalf("broker.json pid=%d after the session check, want hosted pid %d", info.PID, h.cmd.Process.Pid)
	}
	if p := h.cmd.ProcessState; p != nil {
		t.Fatalf("hosted server exited: %v", p)
	}
	t.Logf("--- C.log (transition) ---\n%s", tl)
	t.Logf("--- H.log (transition) ---\n%s", h.transitionLog())
}

// TestLoneClientFallsBackToPrimary (d): with no hosted server anywhere, a
// non-hosted client still becomes primary and serves -- the fallback that keeps
// a client working when the host was never installed or is down.
func TestLoneClientFallsBackToPrimary(t *testing.T) {
	if testing.Short() {
		t.Skip("spawns real mcp-server processes; skipped with -short")
	}
	bin := serverBinary(t)
	dataDir := t.TempDir()

	c := spawnServer(t, bin, dataDir, "C")
	c.send(initLine)
	m, _ := c.recv(30 * time.Second)
	if m["result"] == nil {
		t.Fatalf("C initialize: %v", m)
	}
	c.send(initedLine)
	c.send(toolsListLine(1))
	m, _ = c.recv(30 * time.Second)
	t.Logf("(d) lone client tools/list: %s", summarize(m))
	if m["id"] != float64(1) || m["result"] == nil {
		t.Fatalf("lone client did not serve: %v", m)
	}
	info := waitForBroker(t, dataDir, 10*time.Second, "client primary", isPrimary(c))
	if info.Hosted {
		t.Fatalf("lone client advertised hosted=true")
	}
}

// TestStaleHostedRequestDoesNotMakeAClientPrimaryYield (e): a request from a
// dead pid, and one that has aged out, are both ignored across more than one
// watcher tick -- the client stays primary and serves.
func TestStaleHostedRequestDoesNotMakeAClientPrimaryYield(t *testing.T) {
	if testing.Short() {
		t.Skip("spawns real mcp-server processes; skipped with -short")
	}
	bin := serverBinary(t)

	cases := []struct {
		name string
		req  func() (pid int, at time.Time)
	}{
		{"dead pid, fresh timestamp", func() (int, time.Time) { return deadPID(t), time.Now() }},
		{"live pid, aged out", func() (int, time.Time) {
			return os.Getpid(), time.Now().Add(-singleton.HostedRequestMaxAge - time.Minute)
		}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			dataDir := t.TempDir()
			pid, at := tc.req()
			if err := singleton.WriteHostedRequest(dataDir, pid, at); err != nil {
				t.Fatal(err)
			}

			c := spawnServer(t, bin, dataDir, "C")
			c.send(initLine)
			if m, _ := c.recv(30 * time.Second); m["result"] == nil {
				t.Fatalf("C initialize: %v", m)
			}
			c.send(initedLine)
			waitForBroker(t, dataDir, 10*time.Second, "client primary", isPrimary(c))

			// Past the first watcher tick, with margin for a second.
			time.Sleep(hostedYieldCheckInterval + 2*time.Second)

			c.send(toolsListLine(1))
			m, ok := c.recv(30 * time.Second)
			if !ok {
				t.Fatalf("C exited:\n%s", c.transitionLog())
			}
			if m["id"] != float64(1) || m["result"] == nil {
				t.Fatalf("C stopped serving: %v", m)
			}
			info, _ := singleton.ReadBrokerJSON(dataDir)
			if info.PID != c.cmd.Process.Pid || info.Hosted {
				t.Fatalf("broker.json pid=%d hosted=%v, want client pid %d", info.PID, info.Hosted, c.cmd.Process.Pid)
			}
			if tl := c.transitionLog(); strings.Contains(tl, "hosted-yield") {
				t.Fatalf("client acted on a stale request:\n%s", tl)
			}
			t.Logf("(e) %s: client pid %d still primary after %v", tc.name, info.PID, hostedYieldCheckInterval+2*time.Second)
		})
	}
}

// deadPID returns a pid that certainly names no running process: a child that
// has already been reaped.
func deadPID(t *testing.T) int {
	t.Helper()
	p := spawnServer(t, serverBinary(t), t.TempDir(), "dead", "-version")
	_ = p.cmd.Wait()
	pid := p.cmd.Process.Pid
	if singleton.ProcessAlive(pid) {
		t.Fatalf("pid %d of a reaped child still reads as alive", pid)
	}
	return pid
}
