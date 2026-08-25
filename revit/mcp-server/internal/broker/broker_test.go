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

	b := &Broker{
		Token:     testToken,
		Registry:  registry.New(),
		Execution: mgr,
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
	res, drec := b.Execution.ExecuteScript(ctx, "inst-xyz", "doc-1", "1+1", 2000, 60000)
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}
	if res.Status != execution.StatusSuccess || res.Output != "ok" {
		t.Errorf("res = %+v", res)
	}
}

// TestRegisterThenImmediateCloseDetachesCleanly is a regression test for a
// race between the register notification handler and the connection
// closing right after: if AttachInstance hadn't reliably completed before
// Serve() observed the close and ran DetachInstance, the instance would be
// left permanently attached to a dead connection (a leaked, unroutable
// registration) instead of cleanly detached.
func TestRegisterThenImmediateCloseDetachesCleanly(t *testing.T) {
	b, ln := newTestBroker(t)
	conn, br, resp := dialAndAuth(t, ln.Addr().String(), testToken, RoleAddIn)
	if resp.Error != nil {
		t.Fatalf("auth failed: %+v", resp.Error)
	}

	rest := &tail{r: br, conn: conn}
	addinConn := transport.NewConn(rest)
	go addinConn.Serve()

	if err := addinConn.Notify("register", registerParams{
		InstanceID:   "inst-fastclose",
		PID:          1,
		RevitVersion: "2027",
	}); err != nil {
		t.Fatalf("Notify register: %v", err)
	}

	// Close immediately after sending register.
	addinConn.Close()
	conn.Close()

	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if _, ok := b.Registry.Get("inst-fastclose"); ok {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}
	if _, ok := b.Registry.Get("inst-fastclose"); !ok {
		t.Fatal("instance never appeared in registry")
	}

	// Confirm it was also detached from the execution manager — not left
	// attached to the now-closed connection forever.
	deadline = time.Now().Add(2 * time.Second)
	var drec *diag.Record
	for time.Now().Before(deadline) {
		_, drec = b.Execution.ExecuteScript(context.Background(), "inst-fastclose", "doc-1", "1+1", 500, 60000)
		if drec != nil && drec.Code == "instance_not_found" {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("expected instance_not_found after the connection closed (attach must have been detached), got %+v", drec)
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
