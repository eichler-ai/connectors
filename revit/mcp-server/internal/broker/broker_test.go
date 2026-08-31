package broker

import (
	"bufio"
	"context"
	"encoding/json"
	"net"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/discovery"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/execution"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/mcpserver"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

const testToken = "test-token-0123456789"

func newTestBroker(t *testing.T) (*Broker, net.Listener) {
	t.Helper()
	server := mcp.NewServer(&mcp.Implementation{Name: "revit-mcp-server-test", Version: "0.0.0"}, nil)
	mgr := execution.NewManager()
	mcpserver.Register(server, mgr)
	reg := registry.New()

	b := &Broker{
		Token:     testToken,
		Registry:  reg,
		Execution: mgr,
		Discovery: discovery.NewRouter(reg),
		MCPServer: server,
	}
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("net.Listen: %v", err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	go b.Serve(ctx, ln)
	t.Cleanup(func() {
		cancel()
		ln.Close()
	})
	return b, ln
}

// dialAndAuth connects to the broker and sends the auth handshake, returning
// the raw connection (with the auth line already consumed) and its
// bufio.Reader for further reads, plus the auth response.
func dialAndAuth(t *testing.T, addr string, token string, role Role) (net.Conn, *bufio.Reader, *transport.Message) {
	t.Helper()
	conn, err := net.Dial("tcp", addr)
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	authReq, _ := transport.NewRequest(json.RawMessage(`"auth-1"`), "auth", authParams{Token: token, Role: role})
	b, _ := json.Marshal(authReq)
	if _, err := conn.Write(append(b, '\n')); err != nil {
		t.Fatalf("writing auth: %v", err)
	}
	br := bufio.NewReader(conn)
	line, err := br.ReadBytes('\n')
	if err != nil {
		t.Fatalf("reading auth response: %v", err)
	}
	var resp transport.Message
	if err := json.Unmarshal(line, &resp); err != nil {
		t.Fatalf("decoding auth response: %v", err)
	}
	return conn, br, &resp
}

func TestAuthRejectsBadToken(t *testing.T) {
	_, ln := newTestBroker(t)
	conn, _, resp := dialAndAuth(t, ln.Addr().String(), "wrong-token", RoleAddIn)
	defer conn.Close()

	if resp.Error == nil {
		t.Fatalf("expected an error response for a bad token, got %+v", resp)
	}
	if resp.Error.Code != transport.ErrCodeUnauthorized {
		t.Errorf("Error.Code = %d, want %d", resp.Error.Code, transport.ErrCodeUnauthorized)
	}
	if resp.Error.Data == nil || resp.Error.Data.Severity != "error" {
		t.Errorf("expected shared diagnostic-record shape in error.data, got %+v", resp.Error.Data)
	}

	// The connection should be closed by the broker after rejection.
	conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 1)
	if _, err := conn.Read(buf); err == nil {
		t.Errorf("expected connection to be closed after auth rejection")
	}
}

func TestAuthRejectsMissingAuthAsFirstMessage(t *testing.T) {
	_, ln := newTestBroker(t)
	conn, err := net.Dial("tcp", ln.Addr().String())
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	defer conn.Close()

	notReq, _ := transport.NewRequest(json.RawMessage(`"1"`), "register", map[string]any{"instance_id": "x"})
	b, _ := json.Marshal(notReq)
	conn.Write(append(b, '\n'))

	br := bufio.NewReader(conn)
	line, err := br.ReadBytes('\n')
	if err != nil {
		t.Fatalf("reading response: %v", err)
	}
	var resp transport.Message
	json.Unmarshal(line, &resp)
	if resp.Error == nil {
		t.Fatalf("expected rejection when first message isn't auth, got %+v", resp)
	}
}

func TestAuthAcceptsValidTokenAddIn(t *testing.T) {
	_, ln := newTestBroker(t)
	conn, _, resp := dialAndAuth(t, ln.Addr().String(), testToken, RoleAddIn)
	defer conn.Close()

	if resp.Error != nil {
		t.Fatalf("unexpected error: %+v", resp.Error)
	}
	var result map[string]any
	json.Unmarshal(resp.Result, &result)
	if result["ok"] != true {
		t.Errorf("result = %+v, want ok:true", result)
	}
}

func TestRegisterPopulatesRegistryAndAttachesExecution(t *testing.T) {
	b, ln := newTestBroker(t)
	conn, br, resp := dialAndAuth(t, ln.Addr().String(), testToken, RoleAddIn)
	defer conn.Close()
	if resp.Error != nil {
		t.Fatalf("auth failed: %+v", resp.Error)
	}

	rest := &tail{r: br, conn: conn}
	addinConn := transport.NewConn(rest)
	addinConn.SetRequestHandler(func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return map[string]any{"status": "success", "execution_id": p["execution_id"], "output": "ok"}, nil
	})
	go addinConn.Serve()

	if err := addinConn.Notify("register", registerParams{
		InstanceID:   "inst-xyz",
		PID:          999,
		RevitVersion: "2027",
		Documents:    []registry.Document{{ID: "doc-1", Title: "Test.rvt", Active: true}},
	}); err != nil {
		t.Fatalf("Notify register: %v", err)
	}

	// Registration is processed asynchronously by the broker's read loop;
	// poll briefly for it to land.
	deadline := time.Now().Add(2 * time.Second)
	var inst *registry.Instance
	for time.Now().Before(deadline) {
		if i, ok := b.Registry.Get("inst-xyz"); ok {
			inst = i
			break
		}
		time.Sleep(10 * time.Millisecond)
	}
	if inst == nil {
		t.Fatal("instance never appeared in registry after register")
	}
	if inst.PID != 999 || inst.RevitVersion != "2027" {
		t.Errorf("registry entry = %+v", inst)
	}
	if len(inst.Documents) != 1 || inst.Documents[0].ID != "doc-1" {
		t.Errorf("Documents = %+v", inst.Documents)
	}

	// Execution manager should now be able to route to this instance.
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	res, drec := b.Execution.ExecuteScript(ctx, "inst-xyz", "doc-1", "1+1", 2000, 60000, execution.ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}
	if res.Status != execution.StatusSuccess || res.Output != "ok" {
		t.Errorf("res = %+v", res)
	}
}

// TestPingNotificationReachesRegistry proves the broker's "ping" notification
// case actually calls Registry.RecordPing, not just that RecordPing itself
// works in isolation (already covered in registry_test.go) -- this is the
// exact class of silent wire-wiring bug this PR's own Workshared fix caught
// elsewhere, so the seam between the notification switch and the registry
// call needs its own coverage.
//
// No 31-second wait (test-quality pass; this test used to be the whole
// suite's wall-clock ceiling). An earlier comment here claimed the aging
// "can't be faked with a virtual/future query time" -- that stopped being
// true when IsResponsive gained its caller-supplied `now`: pick a QUERY time
// past the threshold relative to everything that happened before the ping
// (so the ConnectedSince fallback reads unresponsive) but within it relative
// to a ping sent a real ~50ms later (so a recorded ping flips the answer).
// Timestamps stay real; only the question's "as of when" is virtual.
func TestPingNotificationReachesRegistry(t *testing.T) {
	b, ln := newTestBroker(t)
	conn, br, resp := dialAndAuth(t, ln.Addr().String(), testToken, RoleAddIn)
	defer conn.Close()
	if resp.Error != nil {
		t.Fatalf("auth failed: %+v", resp.Error)
	}

	rest := &tail{r: br, conn: conn}
	addinConn := transport.NewConn(rest)
	addinConn.SetRequestHandler(func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return map[string]any{"status": "success"}, nil
	})
	go addinConn.Serve()

	if err := addinConn.Notify("register", registerParams{InstanceID: "inst-ping"}); err != nil {
		t.Fatalf("Notify register: %v", err)
	}
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if _, ok := b.Registry.Get("inst-ping"); ok {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}

	// Everything up to here -- including ConnectedSince -- happened at or
	// before this instant, so a query 25ms past the threshold from NOW reads
	// unresponsive through the ConnectedSince fallback...
	registeredBy := time.Now()
	query := registeredBy.Add(registry.UnresponsiveThreshold + 25*time.Millisecond)
	if b.Registry.IsResponsive("inst-ping", query) {
		t.Fatalf("pre-ping: the ConnectedSince fallback should read unresponsive at the virtual query time")
	}

	// ...while a ping recorded a real >=50ms AFTER registeredBy lands within
	// the threshold of that same query time. Only Registry.RecordPing being
	// actually wired to the "ping" notification can flip the answer.
	time.Sleep(50 * time.Millisecond)
	if err := addinConn.Notify("ping", struct{}{}); err != nil {
		t.Fatalf("Notify ping: %v", err)
	}

	deadline = time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if b.Registry.IsResponsive("inst-ping", query) {
			// Guard against the one vacuous-pass path (PR review): IsResponsive
			// answers true for an UNKNOWN instance, so a regression that made the
			// broker tear the connection down on "ping" would flip the answer
			// without RecordPing ever running. Responsive-because-recorded and
			// responsive-because-deregistered must not be conflated.
			if _, ok := b.Registry.Get("inst-ping"); !ok {
				t.Fatal("instance vanished from the registry after ping -- IsResponsive flipped for the wrong reason")
			}
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("ping notification never reached Registry.RecordPing (IsResponsive at the virtual query time still reflects pre-ping staleness)")
}

// waitFor polls cond every millisecond until it holds or the budget runs
// out, reporting ok. Callers decide whether a timeout is a failure (waiting
// for a state that must be reached) or merely informational (sampling for a
// state that must never be reached).
func waitFor(budget time.Duration, cond func() bool) bool {
	deadline := time.Now().Add(budget)
	for time.Now().Before(deadline) {
		if cond() {
			return true
		}
		time.Sleep(time.Millisecond)
	}
	return cond()
}

// registerAddIn dials, authenticates as an add-in, and sends one register
// notification, returning the raw connection and the transport.Conn over it.
// Neither is closed -- the caller decides when and how, which is the whole
// variable the two tests below differ on.
func registerAddIn(t *testing.T, addr, instanceID string) (net.Conn, *transport.Conn) {
	t.Helper()
	conn, br, resp := dialAndAuth(t, addr, testToken, RoleAddIn)
	if resp.Error != nil {
		t.Fatalf("auth failed: %+v", resp.Error)
	}
	addinConn := transport.NewConn(&tail{r: br, conn: conn})
	// So a t.Fatal between here and the caller's own close doesn't strand the
	// socket and its Serve goroutine for the rest of the process. Both closes
	// are idempotent, so a caller closing them itself (which both tests below
	// do, deliberately, as the thing under test) is fine.
	t.Cleanup(func() {
		addinConn.Close()
		conn.Close()
	})
	go addinConn.Serve()
	if err := addinConn.Notify("register", registerParams{
		InstanceID:   instanceID,
		PID:          1,
		RevitVersion: "2027",
	}); err != nil {
		t.Fatalf("Notify register: %v", err)
	}
	return conn, addinConn
}

// countRegistersVia mints an epoch from the registry itself, so a test can
// tell how many registers ran between two points without reaching into
// Registry's internals or racing a transient entry.
//
// Registry.Register returns a monotonically increasing epoch shared across
// every instance_id, so registering a throwaway sentinel before and after a
// window makes the difference of the two epochs one more than the number of
// registers that happened inside it. This exists because "the entry is
// absent" cannot distinguish torn-down from never-registered, and for the
// immediate-close race the entry's correct lifetime is too short to sample
// for directly -- the epoch is the durable evidence the transient isn't.
func mintEpoch(b *Broker, sentinel string) uint64 {
	return b.Registry.Register(&registry.Instance{InstanceID: sentinel}, time.Now())
}

// TestSecondRegisterOnOneConnectionDoesNotStrandTheFirst covers the orphan
// path a review of the issue-#111 fix found in serveAddIn itself: instanceID
// and registerEpoch are single locals, so a connection that registers A and
// then B used to overwrite them and leave A attached to this connection in
// all three stores, with nothing left holding the identity to detach it. The
// close path would then clean up only B, and A would sit in the registry --
// advertised by list_instances, routable to a dead connection -- until the
// 5-minute prune sweep.
//
// Defensive rather than observed: the real add-in never sends two
// instance_ids down one socket. Pinned because it is the same orphan class
// the test above is named for, and because "the client wouldn't do that" is
// the assumption issue #111 was made of.
func TestSecondRegisterOnOneConnectionDoesNotStrandTheFirst(t *testing.T) {
	b, ln := newTestBroker(t)
	// Both closed by registerAddIn's own t.Cleanup -- this test never closes
	// the connection itself, since the re-register, not the close, is the
	// event under test.
	_, addinConn := registerAddIn(t, ln.Addr().String(), "inst-first")

	if !waitFor(2*time.Second, func() bool {
		_, ok := b.Registry.Get("inst-first")
		return ok
	}) {
		t.Fatal("inst-first never appeared in the registry")
	}

	if err := addinConn.Notify("register", registerParams{
		InstanceID:   "inst-second",
		PID:          2,
		RevitVersion: "2027",
	}); err != nil {
		t.Fatalf("Notify second register: %v", err)
	}

	if !waitFor(2*time.Second, func() bool {
		_, ok := b.Registry.Get("inst-second")
		return ok
	}) {
		t.Fatal("inst-second never appeared in the registry")
	}

	// The first must be gone as soon as the second lands -- not left to age
	// out, which is the whole difference between a handled case and a leak.
	if !waitFor(2*time.Second, func() bool {
		_, ok := b.Registry.Get("inst-first")
		return !ok
	}) {
		t.Fatal("inst-first is still registered after the connection re-registered as inst-second: the first registration was stranded")
	}

	_, drec := b.Execution.ExecuteScript(context.Background(), "inst-first", "doc-1", "1+1", 500, 60000, execution.ScriptOptions{})
	if drec == nil || drec.Code != "instance-not-found" {
		t.Fatalf("inst-first should no longer be routable, got %+v", drec)
	}
}

// TestRegisterThenCloseRemovesTheInstance covers the ordinary lifecycle:
// a registration that is definitely established, then torn down by a close.
// A cleanly closed connection removes the instance from the registry
// immediately rather than leaving it to age out via the heartbeat prune
// sweep (PRD §05), and detaches it from the execution manager so
// execute_script against it reports instance-not-found.
//
// The close deliberately waits for the entry to APPEAR first. That is what
// makes the teardown assertion mean anything: absent-because-never-
// registered and absent-because-torn-down are the same observation, so a
// test that does not first establish presence cannot tell them apart. Its
// predecessor did not, and measurably never once observed a teardown --
// see TestRegisterThenImmediateCloseLeavesNoOrphan below.
func TestRegisterThenCloseRemovesTheInstance(t *testing.T) {
	b, ln := newTestBroker(t)
	conn, addinConn := registerAddIn(t, ln.Addr().String(), "inst-lifecycle")

	if !waitFor(2*time.Second, func() bool {
		_, ok := b.Registry.Get("inst-lifecycle")
		return ok
	}) {
		t.Fatal("register was never processed: the entry never appeared in the registry")
	}

	addinConn.Close()
	conn.Close()

	if !waitFor(2*time.Second, func() bool {
		_, ok := b.Registry.Get("inst-lifecycle")
		return !ok
	}) {
		t.Fatal("instance should have been removed from the registry after the connection closed")
	}

	// Also detached from the execution manager -- not left attached to the
	// now-closed connection forever.
	var drec *diag.Record
	if !waitFor(2*time.Second, func() bool {
		_, drec = b.Execution.ExecuteScript(context.Background(), "inst-lifecycle", "doc-1", "1+1", 500, 60000, execution.ScriptOptions{})
		return drec != nil && drec.Code == "instance-not-found"
	}) {
		t.Fatalf("expected instance-not-found after the connection closed (attach must have been detached), got %+v", drec)
	}
}

// TestRegisterThenImmediateCloseLeavesNoOrphan covers the race the previous
// version of this test was named for: a connection that drops in the same
// breath as its register notification. The hazard is an entry ADDED AFTER
// its own teardown ran -- a registration for a connection that is already
// gone, which nothing later cleans up and which list_instances would go on
// advertising.
//
// Issue #111. The original test asserted the entry was absent, polling in a
// loop that broke as soon as it saw absence. Absence is also the state
// BEFORE the server has processed the register, and instrumenting the loop
// showed that is what it saw essentially every time: across 200 runs the
// entry was observed present once. So the loop exited on its first iteration
// in 199 of 200 runs, the test almost never witnessed a teardown, and the
// ~1-in-400 "failure" was simply the server winning the race to register
// between the loop's exit and the final check -- the run where the code did
// MORE work, not less. The bug was in the test, and it was not merely flaky:
// it was vacuous in essentially every run that passed.
//
// Asserting on a transient cannot be fixed by polling harder, because the
// entry's correct lifetime here is microseconds -- appear, then vanish --
// and any sampling interval can miss it entirely. So this asserts the
// SETTLED state instead, which is the property that actually matters: once
// the dust settles the entry must be gone and must STAY gone. A transient
// appearance is fine. A permanent one is the orphan bug.
//
// The epoch check is what keeps THIS test from repeating the mistake it was
// written to fix. Settled-absence is still absence, so without evidence that
// a register ran at all, a run where the close discarded the register before
// the server saw it would pass while testing nothing. Review of this change
// caught exactly that -- the margin is enormous (the register is written
// with an unbuffered syscall before the FIN, and measures ~25-220us to land
// against a 250ms budget) but "improbable" is not the same as "checked", and
// an unchecked assumption of that shape is what issue #111 was.
func TestRegisterThenImmediateCloseLeavesNoOrphan(t *testing.T) {
	b, ln := newTestBroker(t)
	before := mintEpoch(b, "sentinel-before")
	conn, addinConn := registerAddIn(t, ln.Addr().String(), "inst-fastclose")

	// Close in the same breath as the register -- no wait, that is the point.
	addinConn.Close()
	conn.Close()

	// Long enough for a loopback register + EOF to have been processed many
	// times over, so "absent" below is settled rather than not-yet-arrived.
	time.Sleep(250 * time.Millisecond)

	// Exactly one register must have run in that window: the add-in's. A
	// delta of 1 means only this sentinel's own register happened, i.e. the
	// scenario never occurred and everything below is vacuous.
	if delta := mintEpoch(b, "sentinel-after") - before; delta != 2 {
		t.Fatalf("expected exactly one intervening register (epoch delta 2), got %d: the add-in's register never reached the server, so this test exercised nothing", delta)
	}

	// A single check, not a sampling loop: an orphan is by definition
	// permanent, so one look after the dust settles finds it exactly as
	// reliably as a hundred. Review measured the two as indistinguishable
	// (47/60 vs 46/60 under the async-dispatch mutation), so the loop was
	// costing 250ms of suite wall-clock for nothing.
	if _, ok := b.Registry.Get("inst-fastclose"); ok {
		t.Fatal("registry still holds inst-fastclose after everything settled: a register was applied after its own teardown, orphaning the entry")
	}

	// The same property for the execution manager: routable to a dead
	// connection forever is the equivalent leak on that side.
	_, drec := b.Execution.ExecuteScript(context.Background(), "inst-fastclose", "doc-1", "1+1", 500, 60000, execution.ScriptOptions{})
	if drec == nil || drec.Code != "instance-not-found" {
		t.Fatalf("expected instance-not-found once settled, got %+v", drec)
	}
}

func TestAgentClientRoleProxiesMCPSession(t *testing.T) {
	_, ln := newTestBroker(t)
	conn, br, resp := dialAndAuth(t, ln.Addr().String(), testToken, RoleAgentClient)
	defer conn.Close()
	if resp.Error != nil {
		t.Fatalf("auth failed: %+v", resp.Error)
	}

	rest := &tail{r: br, conn: conn}
	clientTransport := &mcp.IOTransport{Reader: rest, Writer: rest}
	client := mcp.NewClient(&mcp.Implementation{Name: "secondary-proxy-test-client", Version: "0.0.0"}, nil)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	cs, err := client.Connect(ctx, clientTransport, nil)
	if err != nil {
		t.Fatalf("client.Connect over proxied agent-client connection: %v", err)
	}
	defer cs.Close()

	list, err := cs.ListTools(ctx, nil)
	if err != nil {
		t.Fatalf("ListTools over proxied connection: %v", err)
	}
	found := false
	for _, tool := range list.Tools {
		if tool.Name == "execute_script" {
			found = true
		}
	}
	if !found {
		t.Errorf("execute_script tool not visible through the agent-client proxy path: %+v", list.Tools)
	}
}

// TestReconnectOverlapKeepsLiveInstanceRegistered is the end-to-end
// regression test for the v1 integrated review's teardown-race finding: a
// half-open connection's late teardown must not deregister the live
// replacement the add-in already re-registered on a new connection. It
// also pins the displacement half of the fix: the broker closes the old
// connection the moment the new register displaces it, so its teardown
// runs now (as a guarded no-op) instead of the socket leaking.
func TestReconnectOverlapKeepsLiveInstanceRegistered(t *testing.T) {
	b, ln := newTestBroker(t)

	// Connection A: the "original" connection, about to go half-open.
	connA, brA, respA := dialAndAuth(t, ln.Addr().String(), testToken, RoleAddIn)
	defer connA.Close()
	if respA.Error != nil {
		t.Fatalf("auth A failed: %+v", respA.Error)
	}
	addinA := transport.NewConn(&tail{r: brA, conn: connA})
	go addinA.Serve()
	if err := addinA.Notify("register", registerParams{InstanceID: "inst-overlap", PID: 1, RevitVersion: "2027"}); err != nil {
		t.Fatalf("Notify register (A): %v", err)
	}
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if inst, ok := b.Registry.Get("inst-overlap"); ok && inst.PID == 1 {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}

	// Connection B: the add-in redials (network blip) and re-registers the
	// same stable instance_id while A is still officially attached.
	connB, brB, respB := dialAndAuth(t, ln.Addr().String(), testToken, RoleAddIn)
	defer connB.Close()
	if respB.Error != nil {
		t.Fatalf("auth B failed: %+v", respB.Error)
	}
	addinB := transport.NewConn(&tail{r: brB, conn: connB})
	addinB.SetRequestHandler(func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return map[string]any{"status": "success", "execution_id": p["execution_id"], "output": "from-B"}, nil
	})
	go addinB.Serve()
	if err := addinB.Notify("register", registerParams{InstanceID: "inst-overlap", PID: 2, RevitVersion: "2027"}); err != nil {
		t.Fatalf("Notify register (B): %v", err)
	}
	deadline = time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if inst, ok := b.Registry.Get("inst-overlap"); ok && inst.PID == 2 {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}

	// The displacement should close connection A from the broker's side —
	// that is what forces A's teardown to run *now*, while B is live, which
	// is exactly the interleaving the guards exist for. Wait until A's
	// stream actually reports closed before asserting anything.
	_ = connA.SetReadDeadline(time.Now().Add(5 * time.Second))
	buf := make([]byte, 1)
	if _, err := brA.Read(buf); err == nil {
		t.Fatal("expected the broker to close the displaced connection A")
	}

	// Give A's teardown a moment to fully run, then confirm it did NOT
	// clobber B: the registry entry must still be B's, and execute_script
	// must still route to B.
	assertDeadline := time.Now().Add(2 * time.Second)
	for {
		inst, ok := b.Registry.Get("inst-overlap")
		if ok && inst.PID == 2 {
			ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
			res, drec := b.Execution.ExecuteScript(ctx, "inst-overlap", "doc-1", "1+1", 1000, 60000, execution.ScriptOptions{})
			cancel()
			if drec == nil && res.Status == execution.StatusSuccess && res.Output == "from-B" {
				break // registered AND routable through B — the race didn't clobber anything
			}
		}
		if time.Now().After(assertDeadline) {
			inst, ok := b.Registry.Get("inst-overlap")
			t.Fatalf("after A's teardown, instance should remain registered (got ok=%v inst=%+v) and routable to B", ok, inst)
		}
		time.Sleep(20 * time.Millisecond)
	}

	// And B's own clean close must still deregister normally — the guards
	// must not have broken the ordinary teardown path.
	addinB.Close()
	connB.Close()
	deadline = time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if _, ok := b.Registry.Get("inst-overlap"); !ok {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("instance should deregister when its live connection closes cleanly")
}
