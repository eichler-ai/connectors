// Package bridgetest is a fake bridge for tests: it dials a hub's
// /<connector>/bridge socket the way a task pane does, speaks protocol v1,
// and answers exec with whatever the test's Handle says. It lives outside
// internal/ so connector packages in other modules can use it too; it is not
// part of the hub's runtime.
package bridgetest

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/coder/websocket"

	"github.com/eichler-ai/connectors/hub/protocol"
)

// Fake is one connected fake bridge.
type Fake struct {
	t    testing.TB
	conn *websocket.Conn
	// Handle answers an exec. Nil means echo: ok with the script as the JSON
	// result string.
	Handle func(protocol.Exec) protocol.Result

	// Received collects every hub → bridge message by method so tests can
	// assert on replaced/cancel/ping without racing the reader.
	mu       sync.Mutex
	received map[string][]protocol.Message
	notify   chan struct{}
	closed   chan struct{}
	closeErr error
}

// Options shape the hello.
type Options struct {
	Connector       string
	InstanceID      string
	Token           string
	ProtocolVersion int
	Documents       []protocol.Document
	Origin          string
	// SkipHello dials and returns without sending anything, for hello-first
	// tests; Raw is sent verbatim as the first message instead of hello.
	SkipHello bool
	Raw       string
}

// Dial connects to wsURL (ws:// or wss://) and sends hello unless told not
// to. A failed dial is a test fatal; a refused hello is observable through
// WaitClosed.
func Dial(t testing.TB, wsURL string, opt Options) *Fake {
	t.Helper()
	if opt.ProtocolVersion == 0 {
		opt.ProtocolVersion = protocol.Version
	}
	if opt.Connector == "" {
		opt.Connector = "excel"
	}
	if opt.InstanceID == "" {
		opt.InstanceID = "inst-1"
	}
	hdr := http.Header{}
	if opt.Origin != "" {
		hdr.Set("Origin", opt.Origin)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, wsURL, &websocket.DialOptions{HTTPHeader: hdr})
	if err != nil {
		t.Fatalf("bridgetest: dial %s: %v", wsURL, err)
	}
	// Match the hub's own limit so large exec scripts and results both fit.
	conn.SetReadLimit(32 << 20)
	f := &Fake{t: t, conn: conn, received: map[string][]protocol.Message{}, notify: make(chan struct{}, 1), closed: make(chan struct{})}
	t.Cleanup(func() { _ = conn.CloseNow() })
	switch {
	case opt.Raw != "":
		if err := conn.Write(ctx, websocket.MessageText, []byte(opt.Raw)); err != nil {
			t.Fatalf("bridgetest: write raw: %v", err)
		}
	case !opt.SkipHello:
		f.Send(protocol.New(protocol.MethodHello, protocol.Hello{
			Connector: opt.Connector, ProtocolVersion: opt.ProtocolVersion, BridgeVersion: "fake",
			InstanceID: opt.InstanceID, Host: protocol.Host{App: "Excel", Platform: "test", Version: "0"},
			Documents: opt.Documents, Token: opt.Token,
		}))
	}
	go f.readLoop()
	return f
}

// Send writes one message to the hub.
func (f *Fake) Send(msg protocol.Message) {
	f.t.Helper()
	data, _ := json.Marshal(msg)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := f.conn.Write(ctx, websocket.MessageText, data); err != nil {
		f.t.Logf("bridgetest: send %s: %v", msg.Method, err)
	}
}

// SendFragmented writes one message as many WebSocket fragments, the way a
// browser sends a large text message.
func (f *Fake) SendFragmented(msg protocol.Message, fragment int) {
	f.t.Helper()
	data, _ := json.Marshal(msg)
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	w, err := f.conn.Writer(ctx, websocket.MessageText)
	if err != nil {
		f.t.Fatalf("bridgetest: writer: %v", err)
	}
	for len(data) > 0 {
		n := min(fragment, len(data))
		if _, err := w.Write(data[:n]); err != nil {
			f.t.Fatalf("bridgetest: write fragment: %v", err)
		}
		data = data[n:]
	}
	if err := w.Close(); err != nil {
		f.t.Fatalf("bridgetest: close writer: %v", err)
	}
}

func (f *Fake) readLoop() {
	defer close(f.closed)
	for {
		_, data, err := f.conn.Read(context.Background())
		if err != nil {
			f.closeErr = err
			return
		}
		var msg protocol.Message
		if err := json.Unmarshal(data, &msg); err != nil {
			f.t.Errorf("bridgetest: hub sent non-JSON-RPC: %s", data)
			continue
		}
		f.mu.Lock()
		f.received[msg.Method] = append(f.received[msg.Method], msg)
		f.mu.Unlock()
		select {
		case f.notify <- struct{}{}:
		default:
		}
		switch msg.Method {
		case protocol.MethodExec:
			var ex protocol.Exec
			if err := msg.Decode(&ex); err != nil {
				f.t.Errorf("bridgetest: bad exec: %v", err)
				continue
			}
			var res protocol.Result
			if f.Handle != nil {
				res = f.Handle(ex)
			} else {
				res = Echo(ex)
			}
			if res.ID == "" {
				res.ID = ex.ID
			}
			if res.ID == "-" {
				continue // Handle asked for no reply
			}
			go f.Send(protocol.New(protocol.MethodResult, res))
		case protocol.MethodPing:
			go f.Send(protocol.New(protocol.MethodPong, protocol.Pong{}))
		}
	}
}

// Echo is the default Handle: ok, with the script text as the result.
func Echo(ex protocol.Exec) protocol.Result {
	raw, _ := json.Marshal(ex.Script)
	return protocol.Result{ID: ex.ID, OK: true, Result: raw, DurationMs: 1}
}

// NoReply is a Handle that never answers, for timeout tests.
func NoReply(protocol.Exec) protocol.Result { return protocol.Result{ID: "-"} }

// Received returns the hub → bridge messages seen so far for method.
func (f *Fake) Received(method string) []protocol.Message {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]protocol.Message(nil), f.received[method]...)
}

// WaitFor blocks until at least n messages of method have arrived.
func (f *Fake) WaitFor(method string, n int, timeout time.Duration) []protocol.Message {
	f.t.Helper()
	deadline := time.After(timeout)
	for {
		if got := f.Received(method); len(got) >= n {
			return got
		}
		select {
		case <-f.notify:
		case <-f.closed:
			if got := f.Received(method); len(got) >= n {
				return got
			}
			f.t.Fatalf("bridgetest: connection closed before %d %s message(s) arrived: %v", n, method, f.closeErr)
		case <-deadline:
			f.t.Fatalf("bridgetest: no %s message within %s (have %d)", method, timeout, len(f.Received(method)))
		}
	}
}

// WaitClosed blocks until the hub closes the socket and returns the close
// status and reason.
func (f *Fake) WaitClosed(timeout time.Duration) (websocket.StatusCode, string) {
	f.t.Helper()
	select {
	case <-f.closed:
	case <-time.After(timeout):
		f.t.Fatalf("bridgetest: socket still open after %s", timeout)
	}
	var ce websocket.CloseError
	if !errors.As(f.closeErr, &ce) {
		return -1, fmt.Sprint(f.closeErr)
	}
	return ce.Code, ce.Reason
}

// Close ends the connection from the bridge side.
func (f *Fake) Close() { _ = f.conn.Close(websocket.StatusNormalClosure, "bye") }

// WSURL turns an httptest server URL plus path into the ws(s):// equivalent.
func WSURL(httpURL, path string) string {
	return strings.Replace(strings.Replace(httpURL, "https://", "wss://", 1), "http://", "ws://", 1) + path
}
