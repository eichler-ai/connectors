package bridge_test

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/coder/websocket"

	"github.com/eichler-ai/connectors/hub/bridgetest"
	"github.com/eichler-ai/connectors/hub/diag"
	"github.com/eichler-ai/connectors/hub/internal/bridge"
	"github.com/eichler-ai/connectors/hub/internal/registry"
	"github.com/eichler-ai/connectors/hub/protocol"
	"github.com/eichler-ai/connectors/internal/auth"
)

const token = "test-token-0123456789"

type fixture struct {
	reg *registry.Registry
	svc *bridge.Service
	srv *httptest.Server
	url string // ws://…/excel/bridge
	uid string
}

func newFixture(t *testing.T, opts bridge.Options) *fixture {
	t.Helper()
	a, err := auth.NewDevToken(token)
	if err != nil {
		t.Fatal(err)
	}
	reg := registry.New()
	opts.Logger = slog.New(slog.NewTextHandler(io.Discard, nil))
	svc := bridge.New(reg, a, opts)
	mux := http.NewServeMux()
	mux.Handle("GET /excel/bridge", svc.Handler("excel"))
	srv := httptest.NewServer(mux)
	t.Cleanup(srv.Close)
	p, _ := a.VerifyBridgeToken(context.Background(), token)
	return &fixture{reg: reg, svc: svc, srv: srv, url: bridgetest.WSURL(srv.URL, "/excel/bridge"), uid: p.UserID}
}

// waitRegistered polls until the instance is in the registry: hello is
// processed asynchronously to the dial returning.
func (f *fixture) waitRegistered(t *testing.T, instance string) *registry.Bridge {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if b, ok := f.reg.Get(f.uid, "excel", instance); ok {
			return b
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatalf("instance %q never registered", instance)
	return nil
}

func (f *fixture) waitGone(t *testing.T, b *registry.Bridge) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if cur, ok := f.reg.Get(f.uid, "excel", b.InstanceID); !ok || cur != b {
			return
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatalf("instance %q still registered", b.InstanceID)
}

func TestExecRoundTrip(t *testing.T) {
	f := newFixture(t, bridge.Options{})
	fake := bridgetest.Dial(t, f.url, bridgetest.Options{Token: token, InstanceID: "a",
		Documents: []protocol.Document{{ID: "wb1", Title: "Book1", Active: true}}})
	fake.Handle = func(ex protocol.Exec) protocol.Result {
		if ex.Language != "officejs" || ex.TimeoutMs != 2000 || ex.Limits.ResultBytes != 16<<20 {
			t.Errorf("exec fields: %+v", ex)
		}
		return protocol.Result{ID: ex.ID, OK: true, Result: json.RawMessage(`{"n":42}`), DurationMs: 3,
			Notices: []diag.Record{{Severity: "info", Code: "x", Source: "fake", Message: "hi"}}}
	}
	b := f.waitRegistered(t, "a")
	if docs := b.Documents(); len(docs) != 1 || docs[0].Title != "Book1" {
		t.Fatalf("documents from hello: %+v", docs)
	}
	res, err := f.svc.Exec(context.Background(), b, bridge.ExecRequest{Script: "return 42", Language: "officejs", Timeout: 2 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if !res.OK || string(res.Result) != `{"n":42}` || len(res.Notices) != 1 {
		t.Fatalf("result: %+v", res)
	}
}

func TestScriptErrorPropagates(t *testing.T) {
	f := newFixture(t, bridge.Options{})
	fake := bridgetest.Dial(t, f.url, bridgetest.Options{Token: token, InstanceID: "a"})
	fake.Handle = func(ex protocol.Exec) protocol.Result {
		return protocol.Result{ID: ex.ID, OK: false, Error: &protocol.ScriptError{Name: "RichApi.Error", Code: "InvalidArgument", Message: "The argument is invalid", DebugInfo: json.RawMessage(`{"statement":"x"}`)}}
	}
	b := f.waitRegistered(t, "a")
	res, err := f.svc.Exec(context.Background(), b, bridge.ExecRequest{Script: "boom", Timeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if res.OK || res.Error == nil || res.Error.Code != "InvalidArgument" || string(res.Error.DebugInfo) != `{"statement":"x"}` {
		t.Fatalf("error not propagated verbatim: %+v", res)
	}
}

func TestTimeoutSendsCancelAndDropsLateReply(t *testing.T) {
	f := newFixture(t, bridge.Options{})
	fake := bridgetest.Dial(t, f.url, bridgetest.Options{Token: token, InstanceID: "a"})
	var firstID string
	fake.Handle = func(ex protocol.Exec) protocol.Result {
		if firstID == "" {
			firstID = ex.ID
			return bridgetest.NoReply(ex)
		}
		return bridgetest.Echo(ex)
	}
	b := f.waitRegistered(t, "a")
	_, err := f.svc.Exec(context.Background(), b, bridge.ExecRequest{Script: "hang", Timeout: 100 * time.Millisecond})
	if !errors.Is(err, bridge.ErrTimeout) {
		t.Fatalf("want ErrTimeout, got %v", err)
	}
	cancels := fake.WaitFor(protocol.MethodCancel, 1, 2*time.Second)
	var c protocol.Cancel
	if err := cancels[0].Decode(&c); err != nil || c.ID != firstID {
		t.Fatalf("cancel for %q, want %q (%v)", c.ID, firstID, err)
	}
	// The late reply must not be delivered to the next exec.
	fake.Send(protocol.New(protocol.MethodResult, protocol.Result{ID: firstID, OK: true, Result: json.RawMessage(`"late"`)}))
	res, err := f.svc.Exec(context.Background(), b, bridge.ExecRequest{Script: "second", Timeout: 2 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if string(res.Result) != `"second"` {
		t.Fatalf("second exec got %s; the late reply leaked", res.Result)
	}
}

func TestCallerCancelForwardsCancel(t *testing.T) {
	f := newFixture(t, bridge.Options{})
	fake := bridgetest.Dial(t, f.url, bridgetest.Options{Token: token, InstanceID: "a"})
	fake.Handle = bridgetest.NoReply
	b := f.waitRegistered(t, "a")
	ctx, cancel := context.WithCancel(context.Background())
	go func() { time.Sleep(50 * time.Millisecond); cancel() }()
	_, err := f.svc.Exec(ctx, b, bridge.ExecRequest{Script: "hang", Timeout: 10 * time.Second})
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("want context.Canceled, got %v", err)
	}
	fake.WaitFor(protocol.MethodCancel, 1, 2*time.Second)
}

func TestHelloMustComeFirst(t *testing.T) {
	f := newFixture(t, bridge.Options{HelloTimeout: 200 * time.Millisecond})
	reg := protocol.New(protocol.MethodRegister, protocol.Register{})
	raw, _ := json.Marshal(reg)
	fake := bridgetest.Dial(t, f.url, bridgetest.Options{Raw: string(raw)})
	code, reason := fake.WaitClosed(2 * time.Second)
	if code != websocket.StatusPolicyViolation || !strings.Contains(reason, "expected hello first") {
		t.Fatalf("close %d %q", code, reason)
	}

	silent := bridgetest.Dial(t, f.url, bridgetest.Options{SkipHello: true})
	code, reason = silent.WaitClosed(2 * time.Second)
	if code != websocket.StatusPolicyViolation || !strings.Contains(reason, "expected hello first") {
		t.Fatalf("silent socket: close %d %q", code, reason)
	}
}

func TestHelloRejected(t *testing.T) {
	f := newFixture(t, bridge.Options{})
	cases := []struct {
		name string
		opt  bridgetest.Options
		want string
	}{
		{"bad token", bridgetest.Options{Token: "wrong-token-000000000"}, "token rejected"},
		{"outdated protocol", bridgetest.Options{Token: token, ProtocolVersion: 0, Raw: `{"jsonrpc":"2.0","method":"hello","params":{"connector":"excel","protocol_version":0,"instance_id":"x","token":"` + token + `"}}`}, "bridge-outdated"},
		{"wrong connector", bridgetest.Options{Token: token, Connector: "figma"}, "figma"},
		{"missing instance", bridgetest.Options{Token: token, Raw: `{"jsonrpc":"2.0","method":"hello","params":{"connector":"excel","protocol_version":1,"token":"` + token + `"}}`}, "instance_id"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			fake := bridgetest.Dial(t, f.url, tc.opt)
			code, reason := fake.WaitClosed(2 * time.Second)
			if code != websocket.StatusPolicyViolation || !strings.Contains(reason, tc.want) {
				t.Fatalf("close %d %q, want 1008 containing %q", code, reason, tc.want)
			}
			if _, ok := f.reg.Get(f.uid, "excel", "inst-1"); ok {
				t.Fatal("rejected hello was registered")
			}
		})
	}
}

func TestOriginPolicy(t *testing.T) {
	f := newFixture(t, bridge.Options{AllowedOrigins: []string{"https://script-lab.example"}})
	own := "http://" + strings.TrimPrefix(f.srv.URL, "http://")
	allowed := []string{"", own, "https://" + strings.TrimPrefix(f.srv.URL, "http://"), "https://script-lab.example", "HTTPS://SCRIPT-LAB.EXAMPLE"}
	for _, o := range allowed {
		fake := bridgetest.Dial(t, f.url, bridgetest.Options{Token: token, InstanceID: "o", Origin: o})
		f.waitRegistered(t, "o")
		fake.Close()
	}
	for _, o := range []string{"https://evil.example", "null", "https://script-lab.example.evil"} {
		req, _ := http.NewRequest("GET", f.srv.URL+"/excel/bridge", nil)
		req.Header.Set("Origin", o)
		req.Header.Set("Connection", "Upgrade")
		req.Header.Set("Upgrade", "websocket")
		req.Header.Set("Sec-WebSocket-Version", "13")
		req.Header.Set("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==")
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		resp.Body.Close()
		if resp.StatusCode != http.StatusForbidden {
			t.Fatalf("origin %q: status %d, want 403", o, resp.StatusCode)
		}
	}
}

func TestNewestWinsWithReplaced(t *testing.T) {
	f := newFixture(t, bridge.Options{})
	first := bridgetest.Dial(t, f.url, bridgetest.Options{Token: token, InstanceID: "same"})
	first.Handle = bridgetest.NoReply
	b1 := f.waitRegistered(t, "same")

	// An exec in flight on the first connection must fail when it is replaced.
	errc := make(chan error, 1)
	go func() {
		_, err := f.svc.Exec(context.Background(), b1, bridge.ExecRequest{Script: "hang", Timeout: 10 * time.Second})
		errc <- err
	}()
	first.WaitFor(protocol.MethodExec, 1, 2*time.Second)

	second := bridgetest.Dial(t, f.url, bridgetest.Options{Token: token, InstanceID: "same"})
	f.waitGone(t, b1)
	b2 := f.waitRegistered(t, "same")
	if b2 == b1 {
		t.Fatal("second connection did not replace the first")
	}
	first.WaitFor(protocol.MethodReplaced, 1, 2*time.Second)
	if code, _ := first.WaitClosed(5 * time.Second); code != websocket.StatusNormalClosure {
		t.Fatalf("loser closed with %d", code)
	}
	select {
	case err := <-errc:
		if !errors.Is(err, bridge.ErrBridgeGone) {
			t.Fatalf("in-flight exec on the replaced connection: %v, want ErrBridgeGone", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("in-flight exec on the replaced connection never failed")
	}
	// The winner works, and the loser's teardown did not unregister it.
	res, err := f.svc.Exec(context.Background(), b2, bridge.ExecRequest{Script: "ok", Timeout: 2 * time.Second})
	if err != nil || string(res.Result) != `"ok"` {
		t.Fatalf("winner exec: %v %s", err, res.Result)
	}
	if cur, ok := f.reg.Get(f.uid, "excel", "same"); !ok || cur != b2 {
		t.Fatal("winner was unregistered by the loser's teardown")
	}
	_ = second
}

func TestDisconnectFailsInFlightAndUnregisters(t *testing.T) {
	f := newFixture(t, bridge.Options{})
	fake := bridgetest.Dial(t, f.url, bridgetest.Options{Token: token, InstanceID: "a"})
	fake.Handle = bridgetest.NoReply
	b := f.waitRegistered(t, "a")
	errc := make(chan error, 1)
	go func() {
		_, err := f.svc.Exec(context.Background(), b, bridge.ExecRequest{Script: "hang", Timeout: 10 * time.Second})
		errc <- err
	}()
	fake.WaitFor(protocol.MethodExec, 1, 2*time.Second)
	fake.Close()
	if err := <-errc; !errors.Is(err, bridge.ErrBridgeGone) {
		t.Fatalf("got %v, want ErrBridgeGone", err)
	}
	f.waitGone(t, b)
	if _, err := f.svc.Exec(context.Background(), b, bridge.ExecRequest{Script: "x", Timeout: time.Second}); !errors.Is(err, bridge.ErrBridgeGone) {
		t.Fatalf("exec on a gone bridge: %v", err)
	}
}

func TestMultiMiBFragmentedResult(t *testing.T) {
	f := newFixture(t, bridge.Options{})
	fake := bridgetest.Dial(t, f.url, bridgetest.Options{Token: token, InstanceID: "a"})
	payload := `"` + strings.Repeat("x", 5<<20) + `"`
	fake.Handle = func(ex protocol.Exec) protocol.Result {
		// Reply out of band in 64 KiB fragments, as Chrome does for a large
		// message; the default Handle path would send one frame.
		go fake.SendFragmented(protocol.New(protocol.MethodResult, protocol.Result{ID: ex.ID, OK: true, Result: json.RawMessage(payload)}), 64<<10)
		return bridgetest.NoReply(ex)
	}
	b := f.waitRegistered(t, "a")
	res, err := f.svc.Exec(context.Background(), b, bridge.ExecRequest{Script: "big", Timeout: 30 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Result) != len(payload) || string(res.Result) != payload {
		t.Fatalf("got %d bytes, want %d intact", len(res.Result), len(payload))
	}
}

func TestResultOverCapClosesConnection(t *testing.T) {
	f := newFixture(t, bridge.Options{ResultBytes: 1 << 20})
	fake := bridgetest.Dial(t, f.url, bridgetest.Options{Token: token, InstanceID: "a"})
	fake.Handle = func(ex protocol.Exec) protocol.Result {
		if ex.Limits.ResultBytes != 1<<20 {
			t.Errorf("advertised cap %d", ex.Limits.ResultBytes)
		}
		return protocol.Result{ID: ex.ID, OK: true, Result: json.RawMessage(`"` + strings.Repeat("x", 3<<20) + `"`)}
	}
	b := f.waitRegistered(t, "a")
	_, err := f.svc.Exec(context.Background(), b, bridge.ExecRequest{Script: "big", Timeout: 5 * time.Second})
	if !errors.Is(err, bridge.ErrBridgeGone) {
		t.Fatalf("got %v, want ErrBridgeGone from the read-limit close", err)
	}
	if code, _ := fake.WaitClosed(5 * time.Second); code != websocket.StatusMessageTooBig {
		t.Fatalf("closed with %d, want 1009", code)
	}
}

func TestRegisterUpdatesDocumentsAndPingPong(t *testing.T) {
	f := newFixture(t, bridge.Options{PingInterval: 50 * time.Millisecond})
	fake := bridgetest.Dial(t, f.url, bridgetest.Options{Token: token, InstanceID: "a"})
	b := f.waitRegistered(t, "a")
	fake.Send(protocol.New(protocol.MethodRegister, protocol.Register{Documents: []protocol.Document{{ID: "u", Title: "New", Active: true, Detail: map[string]any{"sheet": "S2"}}}}))
	deadline := time.Now().Add(2 * time.Second)
	for {
		if d, ok := b.Document(""); ok && d.Title == "New" && d.Detail["sheet"] == "S2" {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("documents not updated: %+v", b.Documents())
		}
		time.Sleep(5 * time.Millisecond)
	}
	// Hub pings; the fake answers with pong (bridgetest does) and stays.
	fake.WaitFor(protocol.MethodPing, 2, 2*time.Second)
	if _, ok := f.reg.Get(f.uid, "excel", "a"); !ok {
		t.Fatal("responsive bridge was dropped")
	}
	// Bridge pings; hub answers.
	fake.Send(protocol.New(protocol.MethodPing, protocol.Ping{}))
	fake.WaitFor(protocol.MethodPong, 1, 2*time.Second)
}

func TestUnresponsiveBridgeIsClosed(t *testing.T) {
	f := newFixture(t, bridge.Options{PingInterval: 30 * time.Millisecond})
	// A raw socket that says hello and then never answers anything.
	hello, _ := json.Marshal(protocol.New(protocol.MethodHello, protocol.Hello{Connector: "excel", ProtocolVersion: 1, InstanceID: "mute", Token: token}))
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, f.url, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.CloseNow()
	if err := conn.Write(ctx, websocket.MessageText, hello); err != nil {
		t.Fatal(err)
	}
	b := f.waitRegistered(t, "mute")
	for {
		_, _, err := conn.Read(ctx)
		if err != nil {
			if websocket.CloseStatus(err) != websocket.StatusNormalClosure {
				t.Fatalf("closed with %v", err)
			}
			break
		}
	}
	f.waitGone(t, b)
}

func TestNoticeAttachesToInFlightExec(t *testing.T) {
	f := newFixture(t, bridge.Options{})
	fake := bridgetest.Dial(t, f.url, bridgetest.Options{Token: token, InstanceID: "a"})
	fake.Handle = func(ex protocol.Exec) protocol.Result {
		fake.Send(protocol.New(protocol.MethodNotice, protocol.Notice{ExecID: ex.ID, Record: diag.Record{Severity: "warning", Code: "cannot-cancel", Source: "fake", Message: "m"}}))
		return protocol.Result{ID: ex.ID, OK: true, Result: json.RawMessage(`1`)}
	}
	b := f.waitRegistered(t, "a")
	res, err := f.svc.Exec(context.Background(), b, bridge.ExecRequest{Script: "x", Timeout: 2 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Notices) != 1 || res.Notices[0].Code != "cannot-cancel" {
		t.Fatalf("notices: %+v", res.Notices)
	}
}
