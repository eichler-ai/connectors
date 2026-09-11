package dialer

import (
	"bufio"
	"context"
	"encoding/json"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/internal/servercore/transport"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/registry"
)

// fakeBridge is a minimal in-process Rhino MCP Bridge: a loopback listener that
// expects auth first, answers ok, sends register, then pings and echoes
// requests. It also writes its own instances/<pid>.json like the real plug-in.
type fakeBridge struct {
	t          *testing.T
	ln         net.Listener
	token      string
	instanceID string
	pid        int
	dir        string
	docs       []registry.Document

	// registerAs, when set, is the instance_id the process CLAIMS in register (vs. the file's).
	registerAs string
	// silentAfterAuth: answer auth ok and then send nothing, ever.
	silentAfterAuth bool

	mu    sync.Mutex
	conns []net.Conn
	auths []string // tokens presented
}

func newFakeBridge(t *testing.T, dir string, pid int) *fakeBridge {
	return newFakeBridgeWith(t, dir, pid, func(*fakeBridge) {})
}

// newFakeBridgeWith applies configure BEFORE the accept loop starts, so tests never write a
// field the serving goroutine reads (review of #281).
func newFakeBridgeWith(t *testing.T, dir string, pid int, configure func(*fakeBridge)) *fakeBridge {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	b := &fakeBridge{t: t, ln: ln, token: "tok-" + strconv.Itoa(pid), instanceID: "inst-" + strconv.Itoa(pid), pid: pid, dir: dir,
		docs: []registry.Document{{ID: "tmp-abc", Title: "Untitled", Active: true}}}
	b.writeFile()
	configure(b)
	go b.accept()
	t.Cleanup(func() { ln.Close() })
	return b
}

func (b *fakeBridge) port() int { return b.ln.Addr().(*net.TCPAddr).Port }

func (b *fakeBridge) writeFile() {
	body := map[string]any{"schema": 1, "instance_id": b.instanceID, "pid": b.pid, "port": b.port(), "token": b.token,
		"rhino_version": "8.35", "platform": "macos", "bridge_version": "dev", "schema_fingerprint": "", "started_at": time.Now().UTC()}
	raw, _ := json.Marshal(body)
	os.MkdirAll(b.dir, 0o755)
	if err := os.WriteFile(filepath.Join(b.dir, strconv.Itoa(b.pid)+".json"), raw, 0o600); err != nil {
		b.t.Fatal(err)
	}
}

func (b *fakeBridge) accept() {
	for {
		c, err := b.ln.Accept()
		if err != nil {
			return
		}
		b.mu.Lock()
		b.conns = append(b.conns, c)
		b.mu.Unlock()
		go b.serve(c)
	}
}

func (b *fakeBridge) serve(c net.Conn) {
	defer c.Close()
	r := bufio.NewReader(c)
	line, err := r.ReadString('\n')
	if err != nil {
		return
	}
	var req struct {
		ID     json.RawMessage `json:"id"`
		Method string          `json:"method"`
		Params struct {
			Token string `json:"token"`
			Role  string `json:"role"`
		} `json:"params"`
	}
	if err := json.Unmarshal([]byte(line), &req); err != nil {
		return
	}
	b.mu.Lock()
	b.auths = append(b.auths, req.Params.Token)
	b.mu.Unlock()
	if req.Method != "auth" || req.Params.Token != b.token || req.Params.Role != "agent-client" {
		c.Write([]byte(`{"jsonrpc":"2.0","id":` + string(req.ID) + `,"error":{"code":-32600,"message":"auth failed","data":{"severity":"error","code":"auth-invalid-token","source":"mcp-bridge.core.connection","message":"bad token"}}}` + "\n"))
		return
	}
	c.Write([]byte(`{"jsonrpc":"2.0","id":` + string(req.ID) + `,"result":{"ok":true}}` + "\n"))
	if b.silentAfterAuth {
		select {} // hold the socket open forever; the dialer must give up on its own
	}
	claimed := b.instanceID
	if b.registerAs != "" {
		claimed = b.registerAs
	}
	reg, _ := json.Marshal(map[string]any{"jsonrpc": "2.0", "method": "register", "params": map[string]any{
		"instance_id": claimed, "pid": b.pid, "rhino_version": "8.35", "platform": "macos", "bridge_version": "dev", "documents": b.docs}})
	c.Write(append(reg, '\n'))
	c.Write([]byte(`{"jsonrpc":"2.0","method":"ping","params":{"memory":{"private_mb":1,"working_set_mb":2,"managed_mb":3}}}` + "\n"))
	for {
		line, err := r.ReadString('\n')
		if err != nil {
			return
		}
		var any struct {
			ID     json.RawMessage `json:"id"`
			Method string          `json:"method"`
		}
		json.Unmarshal([]byte(line), &any)
		c.Write([]byte(`{"jsonrpc":"2.0","id":` + string(any.ID) + `,"result":{"echo":"` + any.Method + `"}}` + "\n"))
	}
}

func (b *fakeBridge) closeConns() {
	b.mu.Lock()
	defer b.mu.Unlock()
	for _, c := range b.conns {
		c.Close()
	}
	b.conns = nil
}

func waitFor(t *testing.T, what string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for %s", what)
}

func newManager(t *testing.T, dir string, reg *registry.Registry) (*Manager, context.CancelFunc) {
	t.Helper()
	m := New(Options{InstancesDir: dir, ServerID: "srv-test", ServerVersion: "test", Registry: reg,
		Alive: func(int) bool { return true }, ScanInterval: 50 * time.Millisecond, Logf: t.Logf})
	ctx, cancel := context.WithCancel(context.Background())
	go m.Run(ctx)
	t.Cleanup(cancel)
	return m, cancel
}

func TestDialsAuthsAndRegisters(t *testing.T) {
	dir := t.TempDir()
	b := newFakeBridge(t, dir, 101)
	reg := registry.New()
	m, _ := newManager(t, dir, reg)

	waitFor(t, "registration", func() bool { _, ok := reg.Get(b.instanceID); return ok })
	inst, _ := reg.Get(b.instanceID)
	if inst.PID != 101 || inst.RhinoVersion != "8.35" || inst.Platform != "macos" || len(inst.Documents) != 1 || inst.Documents[0].ID != "tmp-abc" {
		t.Fatalf("instance = %+v", inst)
	}
	waitFor(t, "ping recorded", func() bool { i, _ := reg.Get(b.instanceID); return i.Memory != nil && i.Memory.ManagedMB == 3 })
	if got := m.Connected(); len(got) != 1 || got[0] != b.instanceID {
		t.Fatalf("connected = %v", got)
	}
	// One dial, not one per scan.
	time.Sleep(200 * time.Millisecond)
	b.mu.Lock()
	n := len(b.auths)
	b.mu.Unlock()
	if n != 1 {
		t.Fatalf("expected exactly one auth, got %d", n)
	}
	// And the connection routes requests.
	conn, ok := m.Conn(b.instanceID)
	if !ok {
		t.Fatal("no conn")
	}
	res, rpcErr, err := conn.Call(context.Background(), "list_functions", map[string]any{})
	if err != nil || rpcErr != nil || !strings.Contains(string(res), "list_functions") {
		t.Fatalf("call: %s %v %v", res, rpcErr, err)
	}
}

func TestWrongTokenIsRefusedAndBackedOff(t *testing.T) {
	dir := t.TempDir()
	b := newFakeBridge(t, dir, 102)
	// Corrupt the file's token so the server presents the wrong one.
	p := filepath.Join(dir, "102.json")
	raw, _ := os.ReadFile(p)
	os.WriteFile(p, []byte(strings.Replace(string(raw), b.token, "wrong", 1)), 0o600)
	reg := registry.New()
	newManager(t, dir, reg)

	waitFor(t, "an auth attempt", func() bool { b.mu.Lock(); defer b.mu.Unlock(); return len(b.auths) >= 1 })
	time.Sleep(300 * time.Millisecond) // six scans' worth
	b.mu.Lock()
	n := len(b.auths)
	b.mu.Unlock()
	if n != 1 {
		t.Fatalf("a refused pid must be backed off, not redialled every scan; got %d attempts", n)
	}
	if _, ok := reg.Get(b.instanceID); ok {
		t.Fatal("a refused connection must not register")
	}
}

func TestDroppedConnectionIsRemovedAndRedialled(t *testing.T) {
	dir := t.TempDir()
	b := newFakeBridge(t, dir, 103)
	reg := registry.New()
	m, _ := newManager(t, dir, reg)
	waitFor(t, "first registration", func() bool { _, ok := reg.Get(b.instanceID); return ok })
	first, _ := reg.Get(b.instanceID)

	b.closeConns()
	waitFor(t, "removal", func() bool { return len(m.Connected()) == 0 })
	waitFor(t, "re-registration", func() bool { i, ok := reg.Get(b.instanceID); return ok && i.ConnectedSince.After(first.ConnectedSince) })
}

func TestStaleFileIsDeletedAndNeverDialled(t *testing.T) {
	dir := t.TempDir()
	b := newFakeBridge(t, dir, 104)
	reg := registry.New()
	m := New(Options{InstancesDir: dir, Registry: reg, Alive: func(pid int) bool { return pid != 104 }, Logf: t.Logf})
	m.ScanOnce(context.Background())
	if _, err := os.Stat(filepath.Join(dir, "104.json")); !os.IsNotExist(err) {
		t.Fatal("file for a dead pid should be deleted")
	}
	time.Sleep(100 * time.Millisecond)
	b.mu.Lock()
	defer b.mu.Unlock()
	if len(b.auths) != 0 {
		t.Fatal("a dead pid must not be dialled")
	}
}

func TestTwoManagersOneBridge(t *testing.T) {
	// PRD §05: two servers (two Claude sessions) are two connections into one plug-in.
	dir := t.TempDir()
	b := newFakeBridge(t, dir, 105)
	regA, regB := registry.New(), registry.New()
	newManager(t, dir, regA)
	newManager(t, dir, regB)
	waitFor(t, "A registered", func() bool { _, ok := regA.Get(b.instanceID); return ok })
	waitFor(t, "B registered", func() bool { _, ok := regB.Get(b.instanceID); return ok })
	b.mu.Lock()
	defer b.mu.Unlock()
	if len(b.conns) != 2 {
		t.Fatalf("expected 2 connections, got %d", len(b.conns))
	}
}

func TestInstanceIdMismatchIsClosedAndBackedOff(t *testing.T) {
	dir := t.TempDir()
	b := newFakeBridgeWith(t, dir, 106, func(b *fakeBridge) { b.registerAs = "someone-else" })
	reg := registry.New()
	m, _ := newManager(t, dir, reg)
	waitFor(t, "an auth attempt", func() bool { b.mu.Lock(); defer b.mu.Unlock(); return len(b.auths) >= 1 })
	time.Sleep(300 * time.Millisecond) // six scans
	if len(m.Connected()) != 0 {
		t.Fatal("a mismatched register must not attach")
	}
	if _, ok := reg.Get("someone-else"); ok {
		t.Fatal("must not register under the process's claimed id either")
	}
	b.mu.Lock()
	n := len(b.auths)
	b.mu.Unlock()
	if n != 1 {
		t.Fatalf("a mismatched pid must be backed off, not redialled every scan; got %d dials", n)
	}
}

func TestNoRegisterAfterAuthIsGivenUpOn(t *testing.T) {
	dir := t.TempDir()
	b := newFakeBridgeWith(t, dir, 107, func(b *fakeBridge) { b.silentAfterAuth = true })
	reg := registry.New()
	m := New(Options{InstancesDir: dir, ServerID: "srv", Registry: reg, Alive: func(int) bool { return true },
		ScanInterval: 50 * time.Millisecond, RegisterTimeout: 200 * time.Millisecond, Logf: t.Logf})
	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(cancel)
	m.ScanOnce(ctx)
	waitFor(t, "auth", func() bool { b.mu.Lock(); defer b.mu.Unlock(); return len(b.auths) == 1 })
	waitFor(t, "the dial to be abandoned", func() bool {
		m.mu.Lock()
		defer m.mu.Unlock()
		_, inFlight := m.dialing[107]
		_, failed := m.failedAt[107]
		return !inFlight && failed
	})
	if len(m.Connected()) != 0 {
		t.Fatal("a silent bridge must not count as connected")
	}
}

func TestPruneClosesTheSocketAndTheDialerRedials(t *testing.T) {
	// The prune sweep (main.go) removes a silent instance and calls CloseInstance with its epoch;
	// the dialer must close that socket, notice the end, and redial on the next scan -- the
	// epoch-guard path the PR exists to carry over (review of #281).
	dir := t.TempDir()
	b := newFakeBridge(t, dir, 108)
	reg := registry.New()
	var mu sync.Mutex
	now := time.Now()
	clock := func() time.Time { mu.Lock(); defer mu.Unlock(); return now }
	m := New(Options{InstancesDir: dir, ServerID: "srv", Registry: reg, Alive: func(int) bool { return true },
		ScanInterval: 50 * time.Millisecond, Logf: t.Logf, Now: clock})
	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(cancel)
	go m.Run(ctx)
	waitFor(t, "registration", func() bool { _, ok := reg.Get(b.instanceID); return ok })
	first, _ := reg.Get(b.instanceID)

	mu.Lock()
	now = now.Add(registry.PruneAfterSilence + time.Second)
	mu.Unlock()
	pruned := reg.PruneStale(clock())
	if len(pruned) != 1 {
		t.Fatalf("pruned = %v", pruned)
	}
	for id, epoch := range pruned {
		m.CloseInstance(id, epoch)
	}
	waitFor(t, "the link to go", func() bool { return len(m.Connected()) == 0 })
	waitFor(t, "a redial", func() bool { i, ok := reg.Get(b.instanceID); return ok && i.ConnectedSince.After(first.ConnectedSince) })
	b.mu.Lock()
	defer b.mu.Unlock()
	if len(b.auths) != 2 {
		t.Fatalf("expected exactly 2 dials (initial + redial), got %d", len(b.auths))
	}
}

func TestCloseInstanceWithStaleEpochIsANoOp(t *testing.T) {
	dir := t.TempDir()
	b := newFakeBridge(t, dir, 109)
	reg := registry.New()
	m, _ := newManager(t, dir, reg)
	waitFor(t, "registration", func() bool { _, ok := reg.Get(b.instanceID); return ok })
	m.CloseInstance(b.instanceID, 999999)
	time.Sleep(300 * time.Millisecond)
	// Connected() alone cannot fail this test: a wrongly-closed link is redialled within a scan.
	// The dial count can -- a second auth means the live connection was closed.
	b.mu.Lock()
	n := len(b.auths)
	b.mu.Unlock()
	if n != 1 || len(m.Connected()) != 1 {
		t.Fatalf("a stale epoch must not close the live connection (dials %d, connected %d)", n, len(m.Connected()))
	}
}

// TestAttachHookCompletesBeforeDetachHook pins the #296 fix: for one connection,
// every attach hook runs to completion before any detach hook, even though the
// two are delivered on separate goroutines. The bug is a race, so the test
// FORCES it -- a deliberately slow attach hook plus an immediate disconnect, so
// the detach goroutine is spawned while the attach hook is still running. Before
// the fix the detach could be recorded first (the manager would then delete
// nothing and later insert a stale index for a dead instance); the fix makes the
// detach goroutine wait on the attach goroutine's completion.
func TestAttachHookCompletesBeforeDetachHook(t *testing.T) {
	dir := t.TempDir()
	b := newFakeBridge(t, dir, 130)
	reg := registry.New()

	// Build the manager and register the hook BEFORE Run starts dialing, so the
	// hook is present when the first connection registers (no registration race).
	m := New(Options{InstancesDir: dir, ServerID: "srv-test", ServerVersion: "test", Registry: reg,
		Alive: func(int) bool { return true }, ScanInterval: 50 * time.Millisecond, Logf: t.Logf})

	var mu sync.Mutex
	var events []string
	attachStarted := make(chan struct{})
	var once sync.Once
	m.OnAttach(func(_ string, _ *transport.Conn, attached bool) {
		if attached {
			once.Do(func() { close(attachStarted) }) // tell the test the attach hook is running
			time.Sleep(200 * time.Millisecond)       // stand in for a slow index build
			mu.Lock()
			events = append(events, "attach")
			mu.Unlock()
			return
		}
		mu.Lock()
		events = append(events, "detach")
		mu.Unlock()
	})

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go m.Run(ctx)

	// Once the attach hook has STARTED (but is still sleeping), drop the connection
	// so the detach goroutine races the still-running attach hook. Close the
	// listener first so the manager cannot redial and produce a second cycle (which
	// would pollute the single connect->disconnect this test is asserting about).
	<-attachStarted
	b.ln.Close()
	b.closeConns()

	waitFor(t, "both attach and detach hooks", func() bool {
		mu.Lock()
		defer mu.Unlock()
		return len(events) == 2
	})
	mu.Lock()
	defer mu.Unlock()
	if events[0] != "attach" || events[1] != "detach" {
		t.Fatalf("attach must complete before detach runs; got %v", events)
	}
}
