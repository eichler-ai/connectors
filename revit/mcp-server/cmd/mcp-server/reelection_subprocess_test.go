package main

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"net"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/broker"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/singleton"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

// Re-election coverage, in the same real-process style as
// continuity_subprocess_test.go (whose harness this file reuses). The tests
// there kill or freeze the primary; these close the remaining holes:
//
//   - a primary that exits CLEANLY (its client closes stdin, the normal stdio
//     shutdown) and releases the lock properly -- the common path, which must
//     hand over without any dead-holder takeover or unresponsive-primary strike;
//   - two independent client sessions surviving ONE re-election at the same
//     time, one promoting and the other re-homing as its secondary;
//   - the add-in's side of a re-election: broker.json is rewritten with a new
//     port and token, and an add-in that re-reads it must be able to re-auth
//     and re-register on the new primary, visible to a surviving client.
//
// On the add-in test's scope: the real add-in is C# (BridgeHost.RunConnectionLoop),
// whose whole loop is "read broker.json -> dial -> auth -> register -> read
// until the socket drops -> back off -> repeat". fakeAddIn below is that shape
// in Go, and what the test verifies is the SERVER-SIDE contract that loop
// depends on -- not BridgeHost's own backoff, timeouts or diagnostics, which
// only a live Revit + broker run exercises (see revit/CONTRIBUTING.md's tiers).

// waitExit waits for the process to exit on its own and returns cmd.Wait's
// result; it fails the test if the process is still running at the deadline.
func (p *mcpProc) waitExit(timeout time.Duration) error {
	p.t.Helper()
	done := make(chan error, 1)
	go func() { done <- p.cmd.Wait() }()
	select {
	case err := <-done:
		return err
	case <-time.After(timeout):
		p.t.Fatalf("%s did not exit within %v after its stdin closed:\n%s", p.name, timeout, p.transitionLog())
		return nil
	}
}

// closeStdin is the MCP client's normal stdio shutdown signal.
func (p *mcpProc) closeStdin() {
	p.t.Helper()
	if err := p.stdin.Close(); err != nil {
		p.t.Fatalf("%s: closing stdin: %v", p.name, err)
	}
}

// waitForPromotion blocks until the process's log says it is listening as primary.
func waitForPromotion(t *testing.T, p *mcpProc) {
	t.Helper()
	deadline := time.Now().Add(60 * time.Second)
	for time.Now().Before(deadline) {
		if strings.Contains(p.transitionLog(), "primary: listening") {
			return
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatalf("%s never promoted to primary:\n%s", p.name, p.transitionLog())
}

// initSession drives initialize + initialized + one tools/list through p and
// fails unless the session is live.
func initSession(t *testing.T, p *mcpProc) {
	t.Helper()
	p.send(initLine)
	m, _ := p.recv(30 * time.Second)
	if m["result"] == nil {
		t.Fatalf("%s initialize: %v\n%s", p.name, m, p.transitionLog())
	}
	p.send(initedLine)
	p.send(toolsListLine(1))
	m, _ = p.recv(30 * time.Second)
	if m["id"] != float64(1) || m["result"] == nil {
		t.Fatalf("%s tools/list: %v\n%s", p.name, m, p.transitionLog())
	}
}

// expectResult reads p's next line and requires it to be a successful
// response to request id.
func expectResult(t *testing.T, p *mcpProc, id int, what string) map[string]any {
	t.Helper()
	m, ok := p.recv(60 * time.Second)
	if !ok {
		t.Fatalf("%s exited (%s):\n%s", p.name, what, p.transitionLog())
	}
	if m["id"] != float64(id) || m["result"] == nil {
		t.Fatalf("%s %s: expected the result for request %d, got %v\n%s", p.name, what, id, m, p.transitionLog())
	}
	return m
}

func toolsCallLine(id int, name string) string {
	return fmt.Sprintf(`{"jsonrpc":"2.0","id":%d,"method":"tools/call","params":{"name":%q,"arguments":{}}}`+"\n", id, name)
}

// listedInstanceIDs pulls the instance ids out of a list_instances tools/call
// result (structuredContent first, the text content as a fallback).
func listedInstanceIDs(t *testing.T, m map[string]any) []string {
	t.Helper()
	res, _ := m["result"].(map[string]any)
	var out struct {
		Instances []struct {
			InstanceID string `json:"instance_id"`
		} `json:"instances"`
	}
	if sc, ok := res["structuredContent"]; ok {
		b, _ := json.Marshal(sc)
		_ = json.Unmarshal(b, &out)
	} else if content, ok := res["content"].([]any); ok && len(content) > 0 {
		if c0, ok := content[0].(map[string]any); ok {
			_ = json.Unmarshal([]byte(fmt.Sprint(c0["text"])), &out)
		}
	}
	ids := make([]string, 0, len(out.Instances))
	for _, i := range out.Instances {
		ids = append(ids, i.InstanceID)
	}
	return ids
}

func containsString(ss []string, s string) bool {
	for _, x := range ss {
		if x == s {
			return true
		}
	}
	return false
}

// TestCleanPrimaryExitPromotesSecondaryWithoutTakeover: A is primary and B
// proxies through it, with a live session on B. A's client closes stdin -- the
// normal stdio shutdown -- so A returns from run() and RELEASES the lock (no
// corpse, no stale holder record). B must notice the upstream drop, win the
// election on the very next turn, promote, and keep B's session live via the
// #220 replay -- with none of the dead-primary machinery involved.
func TestCleanPrimaryExitPromotesSecondaryWithoutTakeover(t *testing.T) {
	if testing.Short() {
		t.Skip("spawns real mcp-server processes; skipped with -short")
	}
	bin := serverBinary(t)
	dataDir := t.TempDir()

	a := spawnServer(t, bin, dataDir, "A")
	initSession(t, a)
	aPID := a.cmd.Process.Pid

	b := spawnServer(t, bin, dataDir, "B")
	waitForSecondary(t, b)
	initSession(t, b)

	t.Logf("--- closing primary A's stdin (pid %d): clean stdio shutdown ---", aPID)
	a.closeStdin()
	if err := a.waitExit(30 * time.Second); err != nil {
		t.Fatalf("A did not exit cleanly on stdin close: %v\n%s", err, a.transitionLog())
	}
	t.Logf("A exited cleanly")

	// The client on B knows nothing of this; it just sends its next request.
	b.send(toolsListLine(2))
	m := expectResult(t, b, 2, "next request after A's clean exit (NO re-initialize)")
	t.Logf("B next request after A exit: %s", summarize(m))
	b.send(toolsListLine(3))
	expectResult(t, b, 3, "follow-up request")

	tl := b.transitionLog()
	t.Logf("--- B.log (transition) ---\n%s", tl)
	if !strings.Contains(tl, "primary: listening") {
		t.Fatalf("B did not promote to primary:\n%s", tl)
	}
	if !strings.Contains(tl, "session-continuity: replaying") {
		t.Fatalf("B did not replay the cached initialize:\n%s", tl)
	}
	// The handoff was clean: one drop, one re-run of the election, one win.
	if n := strings.Count(tl, "re-attempting lock acquisition"); n != 1 {
		t.Fatalf("expected exactly one election re-run after a clean primary exit, got %d:\n%s", n, tl)
	}
	for _, forbidden := range []string{"dead-primary-takeover", "primary-unresponsive", "election-stalled", "stale lock generation"} {
		if strings.Contains(tl, forbidden) {
			t.Fatalf("a clean exit must hand over without %q:\n%s", forbidden, tl)
		}
	}

	// broker.json now names B, on generation 0 (a released lock is free again).
	info, err := singleton.ReadBrokerJSON(dataDir)
	if err != nil {
		t.Fatal(err)
	}
	if info.PID != b.cmd.Process.Pid {
		t.Fatalf("broker.json names pid %d, want B's %d (A was %d)", info.PID, b.cmd.Process.Pid, aPID)
	}
	if _, err := os.Stat(singleton.GenerationLockPath(dataDir, 1)); !os.IsNotExist(err) {
		t.Fatalf("a clean handoff must not need a fallback lock generation, but broker.lock.1 exists (stat err %v)", err)
	}
}

// TestTwoClientSessionsSurviveOneReelection: A is primary; B and C each proxy
// an independent, initialized client session through it. A is killed. Both
// sessions must survive that single re-election at the same time: whichever
// of B/C wins promotes, the other re-homes as a secondary to the winner, and
// both answer their client's next request with no re-initialize.
func TestTwoClientSessionsSurviveOneReelection(t *testing.T) {
	if testing.Short() {
		t.Skip("spawns real mcp-server processes; skipped with -short")
	}
	bin := serverBinary(t)
	dataDir := t.TempDir()

	a := spawnServer(t, bin, dataDir, "A")
	initSession(t, a)

	b := spawnServer(t, bin, dataDir, "B")
	waitForSecondary(t, b)
	c := spawnServer(t, bin, dataDir, "C")
	waitForSecondary(t, c)
	initSession(t, b)
	initSession(t, c)

	t.Logf("--- killing primary A (pid %d) with two live proxied sessions ---", a.cmd.Process.Pid)
	a.kill()

	// Both clients send their next request before either has been answered,
	// so the two re-elections are genuinely concurrent.
	b.send(toolsListLine(2))
	c.send(toolsListLine(2))
	for _, p := range []*mcpProc{b, c} {
		m := expectResult(t, p, 2, "next request after A's death (NO re-initialize)")
		t.Logf("%s next request after A death: %s", p.name, summarize(m))
	}
	// And again, once the dust has settled.
	b.send(toolsListLine(3))
	c.send(toolsListLine(3))
	expectResult(t, b, 3, "follow-up")
	expectResult(t, c, 3, "follow-up")

	var promoted, rehomed *mcpProc
	for _, p := range []*mcpProc{b, c} {
		tl := p.transitionLog()
		if !strings.Contains(tl, "session-continuity: replaying") {
			t.Fatalf("%s did not replay its client's initialize:\n%s", p.name, tl)
		}
		isPrimary := strings.Contains(tl, "primary: listening")
		reconnected := strings.Count(tl, "secondary: proxying stdio through primary") > 1
		switch {
		case isPrimary && !reconnected:
			if promoted != nil {
				t.Fatalf("both %s and %s promoted to primary:\n%s\n---\n%s", promoted.name, p.name, promoted.transitionLog(), tl)
			}
			promoted = p
		case reconnected && !isPrimary:
			if rehomed != nil {
				t.Fatalf("both %s and %s re-homed as secondaries; nobody promoted:\n%s\n---\n%s", rehomed.name, p.name, rehomed.transitionLog(), tl)
			}
			rehomed = p
		default:
			t.Fatalf("%s took an unexpected path (primary=%v reconnected=%v):\n%s", p.name, isPrimary, reconnected, tl)
		}
	}
	if promoted == nil || rehomed == nil {
		t.Fatalf("expected one promotion and one re-home; promoted=%v rehomed=%v", promoted, rehomed)
	}
	t.Logf("%s promoted to primary; %s re-homed as its secondary", promoted.name, rehomed.name)

	// The re-homed one is proxying through the promoted one, not through a ghost.
	info, err := singleton.ReadBrokerJSON(dataDir)
	if err != nil {
		t.Fatal(err)
	}
	if info.PID != promoted.cmd.Process.Pid {
		t.Fatalf("broker.json names pid %d, want the promoted %s (pid %d)", info.PID, promoted.name, promoted.cmd.Process.Pid)
	}
	if !strings.Contains(rehomed.transitionLog(), fmt.Sprintf("through primary at %s:%d", info.Host, info.Port)) {
		t.Fatalf("%s did not reconnect to the new primary's %s:%d:\n%s", rehomed.name, info.Host, info.Port, rehomed.transitionLog())
	}
}

// fakeAddIn is the add-in's connection loop in the small (see the file
// comment): it reads broker.json, dials, auths with role add-in, registers
// one instance, and reports where it landed.
type fakeAddIn struct {
	instanceID string
	conn       net.Conn
	rpc        *transport.Conn
	served     chan struct{} // closed when the read loop ends (the broker went away)
	port       int
	token      string
}

// bufferedConn continues reading from the bufio.Reader that consumed the auth
// response (so no bytes buffered past it are lost) while writes and Close go
// to the socket -- the same shape as the broker's own post-auth tail.
type bufferedConn struct {
	r *bufio.Reader
	net.Conn
}

func (c *bufferedConn) Read(p []byte) (int, error) { return c.r.Read(p) }

// connectFakeAddIn performs one discover -> dial -> auth -> register attempt.
func connectFakeAddIn(dataDir, instanceID string) (*fakeAddIn, error) {
	info, err := singleton.ReadBrokerJSON(dataDir)
	if err != nil {
		return nil, fmt.Errorf("discover: %w", err)
	}
	conn, err := net.DialTimeout("tcp", net.JoinHostPort(info.Host, strconv.Itoa(info.Port)), 2*time.Second)
	if err != nil {
		return nil, fmt.Errorf("dial %s:%d (pid %d): %w", info.Host, info.Port, info.PID, err)
	}
	authReq, _ := transport.NewRequest(json.RawMessage(`"auth"`), "auth", map[string]any{
		"token": info.Token,
		"role":  string(broker.RoleAddIn),
	})
	ab, _ := json.Marshal(authReq)
	if _, err := conn.Write(append(ab, '\n')); err != nil {
		conn.Close()
		return nil, fmt.Errorf("send auth: %w", err)
	}
	br := bufio.NewReader(conn)
	_ = conn.SetReadDeadline(time.Now().Add(10 * time.Second))
	line, err := br.ReadBytes('\n')
	if err != nil {
		conn.Close()
		return nil, fmt.Errorf("read auth response: %w", err)
	}
	_ = conn.SetReadDeadline(time.Time{})
	var resp transport.Message
	if err := json.Unmarshal(line, &resp); err != nil {
		conn.Close()
		return nil, fmt.Errorf("decode auth response: %w", err)
	}
	if resp.Error != nil {
		conn.Close()
		return nil, fmt.Errorf("auth rejected: %s", resp.Error.Message)
	}

	rpc := transport.NewConn(&bufferedConn{r: br, Conn: conn})
	rpc.SetRequestHandler(func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return nil, &transport.RPCError{Code: -32601, Message: "fake add-in: " + method + " not supported"}
	})
	f := &fakeAddIn{instanceID: instanceID, conn: conn, rpc: rpc, served: make(chan struct{}), port: info.Port, token: info.Token}
	go func() {
		_ = rpc.Serve()
		close(f.served)
	}()
	if err := rpc.Notify("register", map[string]any{
		"instance_id":   instanceID,
		"pid":           os.Getpid(),
		"revit_version": "2026",
		"documents":     []map[string]any{{"id": "doc-1", "title": "Fake.rvt", "active": true}},
	}); err != nil {
		rpc.Close()
		return nil, fmt.Errorf("register: %w", err)
	}
	return f, nil
}

// reconnectFakeAddIn is the loop's retry half: discover -> connect until it
// succeeds or the deadline passes, backing off between failures the way the
// add-in does (a fixed short delay here; the real policy is BridgeHost's).
// A broker.json still naming the dead primary fails at dial and is retried.
func reconnectFakeAddIn(t *testing.T, dataDir, instanceID string, timeout time.Duration) *fakeAddIn {
	t.Helper()
	deadline := time.Now().Add(timeout)
	var lastErr error
	attempts := 0
	for time.Now().Before(deadline) {
		attempts++
		f, err := connectFakeAddIn(dataDir, instanceID)
		if err == nil {
			t.Logf("fake add-in reconnected on attempt %d (port %d)", attempts, f.port)
			return f
		}
		lastErr = err
		time.Sleep(100 * time.Millisecond)
	}
	t.Fatalf("fake add-in could not reconnect within %v (%d attempts); last: %v", timeout, attempts, lastErr)
	return nil
}

// waitListed polls list_instances through the client on p until instanceID
// is (or is not) listed; a register is processed asynchronously by the broker.
func waitListed(t *testing.T, p *mcpProc, id *int, instanceID string, want bool) {
	t.Helper()
	deadline := time.Now().Add(20 * time.Second)
	var ids []string
	for time.Now().Before(deadline) {
		*id++
		p.send(toolsCallLine(*id, "list_instances"))
		m := expectResult(t, p, *id, "list_instances")
		ids = listedInstanceIDs(t, m)
		if containsString(ids, instanceID) == want {
			return
		}
		time.Sleep(100 * time.Millisecond)
	}
	t.Fatalf("%s: list_instances listed %v; wanted %q listed=%v", p.name, ids, instanceID, want)
}

// TestAddInReconnectsToNewPrimaryAfterReelection: A is primary with a client
// session on secondary B, and a fake add-in is registered on A (visible via
// B's list_instances). A is killed. B promotes and rewrites broker.json with a
// NEW port and NEW token. The add-in's read loop ends; re-running its
// connection loop against the rewritten broker.json must land it on B --
// authenticated with the new token, re-registered -- so B's client sees the
// instance again through the same, never re-initialized session.
func TestAddInReconnectsToNewPrimaryAfterReelection(t *testing.T) {
	if testing.Short() {
		t.Skip("spawns real mcp-server processes; skipped with -short")
	}
	bin := serverBinary(t)
	dataDir := t.TempDir()
	const instanceID = "fake-revit-1"

	a := spawnServer(t, bin, dataDir, "A")
	initSession(t, a)
	b := spawnServer(t, bin, dataDir, "B")
	waitForSecondary(t, b)
	initSession(t, b)
	reqID := 1

	addin, err := connectFakeAddIn(dataDir, instanceID)
	if err != nil {
		t.Fatalf("fake add-in first connect: %v", err)
	}
	t.Cleanup(func() { addin.rpc.Close() })
	waitListed(t, b, &reqID, instanceID, true)
	t.Logf("fake add-in registered on A (port %d); B's client sees it", addin.port)
	oldPort, oldToken := addin.port, addin.token

	t.Logf("--- killing primary A (pid %d) with the add-in attached ---", a.cmd.Process.Pid)
	a.kill()

	// The add-in's read loop ends when its broker goes away.
	select {
	case <-addin.served:
	case <-time.After(30 * time.Second):
		t.Fatalf("the fake add-in's connection to the dead primary never dropped")
	}
	// B promotes on its own (the upstream drop drives the re-election; no
	// client request is needed for it).
	waitForPromotion(t, b)

	// The add-in re-runs its loop against the rewritten broker.json.
	reconnected := reconnectFakeAddIn(t, dataDir, instanceID, 60*time.Second)
	t.Cleanup(func() { reconnected.rpc.Close() })
	if reconnected.port == oldPort {
		t.Fatalf("the add-in reconnected to the old port %d; broker.json was not rewritten by the new primary", oldPort)
	}
	if reconnected.token == oldToken {
		t.Fatalf("the new primary reused the old token; each primary must mint its own")
	}
	info, err := singleton.ReadBrokerJSON(dataDir)
	if err != nil {
		t.Fatal(err)
	}
	if info.PID != b.cmd.Process.Pid || info.Port != reconnected.port {
		t.Fatalf("broker.json names pid %d port %d; want B (pid %d) on the add-in's port %d", info.PID, info.Port, b.cmd.Process.Pid, reconnected.port)
	}

	// And the surviving client session, now served by B's own server, sees
	// the re-registered instance.
	waitListed(t, b, &reqID, instanceID, true)
	t.Logf("fake add-in re-registered on B (port %d, new token); B's client (never re-initialized) lists it", reconnected.port)

	// Sanity on the other direction: dropping the add-in removes it promptly.
	reconnected.rpc.Close()
	waitListed(t, b, &reqID, instanceID, false)
}
