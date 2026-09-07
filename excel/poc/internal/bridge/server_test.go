package bridge

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"testing/fstest"
	"time"

	"golang.org/x/net/websocket"
)

// fakeAddin dials /ws the way taskpane.js does and answers scripts with the given function.
func fakeAddin(t *testing.T, srv *httptest.Server, origin string, answer func(Request) *Response) *websocket.Conn {
	t.Helper()
	wsURL := "wss" + strings.TrimPrefix(srv.URL, "https") + "/ws"
	cfg, err := websocket.NewConfig(wsURL, origin)
	if err != nil {
		t.Fatal(err)
	}
	cfg.TlsConfig = srv.Client().Transport.(*http.Transport).TLSClientConfig
	ws, err := websocket.DialConfig(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err := websocket.JSON.Send(ws, hello{Type: "hello", Host: "Excel", Platform: "OfficeOnline", Version: "16.0", Workbook: "Book.xlsx"}); err != nil {
		t.Fatal(err)
	}
	go func() {
		for {
			var req Request
			if websocket.JSON.Receive(ws, &req) != nil {
				return
			}
			if resp := answer(req); resp != nil {
				resp.ID = req.ID
				_ = websocket.JSON.Send(ws, resp)
			}
		}
	}()
	return ws
}

func exec(t *testing.T, srv *httptest.Server, script string, timeoutMs int64) (int, ExecResult) {
	t.Helper()
	body, _ := json.Marshal(ExecRequest{Script: script, TimeoutMs: timeoutMs})
	resp, err := srv.Client().Post(srv.URL+"/exec", "application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	out, _ := io.ReadAll(resp.Body)
	var res ExecResult
	if err := json.Unmarshal(out, &res); err != nil {
		t.Fatalf("HTTP %d, non-JSON body: %s", resp.StatusCode, out)
	}
	return resp.StatusCode, res
}

func newServer(t *testing.T) (*httptest.Server, *Hub) {
	hub := NewHub()
	srv := httptest.NewTLSServer(Handler(hub, fstest.MapFS{"taskpane.html": {Data: []byte("<html>")}}))
	t.Cleanup(srv.Close)
	return srv, hub
}

func waitConnected(t *testing.T, hub *Hub) {
	t.Helper()
	for i := 0; i < 100; i++ {
		if hub.Status().Connected {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("add-in never registered")
}

func TestExecRoundTrip(t *testing.T) {
	srv, hub := newServer(t)
	ws := fakeAddin(t, srv, srv.URL, func(r Request) *Response {
		return &Response{OK: true, Result: json.RawMessage(`{"echo":` + strconvQuote(r.Script) + `}`), DurationMs: 3}
	})
	defer ws.Close()
	waitConnected(t, hub)

	st := hub.Status()
	if st.Workbook != "Book.xlsx" || st.Platform != "OfficeOnline" {
		t.Errorf("status = %+v", st)
	}
	code, res := exec(t, srv, "return 1", 0)
	var compact bytes.Buffer
	_ = json.Compact(&compact, res.Result)
	if code != 200 || !res.OK || compact.String() != `{"echo":"return 1"}` {
		t.Errorf("code=%d res=%+v", code, res)
	}
}

func TestExecScriptError(t *testing.T) {
	srv, hub := newServer(t)
	ws := fakeAddin(t, srv, srv.URL, func(r Request) *Response {
		return &Response{OK: false, Error: &ScriptError{Name: "RichApi.Error", Message: "not found", Code: "ItemNotFound",
			DebugInfo: json.RawMessage(`{"errorLocation":"WorksheetCollection.getItem"}`)}}
	})
	defer ws.Close()
	waitConnected(t, hub)
	code, res := exec(t, srv, "x", 0)
	if code != 200 || res.OK || res.Error == nil || res.Error.Code != "ItemNotFound" {
		t.Errorf("code=%d res=%+v", code, res)
	}
}

func TestExecNoAddin(t *testing.T) {
	srv, _ := newServer(t)
	code, res := exec(t, srv, "x", 0)
	if code != http.StatusServiceUnavailable || !strings.Contains(res.BridgeError, "no add-in connected") {
		t.Errorf("code=%d res=%+v", code, res)
	}
}

func TestExecTimeoutThenLateReplyDropped(t *testing.T) {
	srv, hub := newServer(t)
	replies := make(chan Request, 1)
	ws := fakeAddin(t, srv, srv.URL, func(r Request) *Response { replies <- r; return nil })
	defer ws.Close()
	waitConnected(t, hub)

	code, res := exec(t, srv, "while(true){}", 100)
	if code != http.StatusGatewayTimeout || !strings.Contains(res.BridgeError, "deadline") {
		t.Errorf("code=%d res=%+v", code, res)
	}
	req := <-replies
	if req.TimeoutMs <= 0 || req.TimeoutMs > 100 {
		t.Errorf("advisory timeout = %d, want (0,100]", req.TimeoutMs)
	}
	// A late reply must not break the hub: send it, then prove a fresh exec still works.
	_ = websocket.JSON.Send(ws, Response{ID: req.ID, OK: true, Result: json.RawMessage(`"late"`)})
	ws2 := fakeAddin(t, srv, srv.URL, func(r Request) *Response { return &Response{OK: true, Result: json.RawMessage(`2`)} })
	defer ws2.Close()
	time.Sleep(50 * time.Millisecond)
	if code, res := exec(t, srv, "return 2", 0); code != 200 || string(res.Result) != "2" {
		t.Errorf("after replacement: code=%d res=%+v", code, res)
	}
}

func TestRejectsForeignOrigin(t *testing.T) {
	srv, hub := newServer(t)
	wsURL := "wss" + strings.TrimPrefix(srv.URL, "https") + "/ws"
	cfg, _ := websocket.NewConfig(wsURL, "https://evil.example")
	cfg.TlsConfig = srv.Client().Transport.(*http.Transport).TLSClientConfig
	if ws, err := websocket.DialConfig(cfg); err == nil {
		ws.Close()
		t.Fatal("foreign origin accepted")
	}
	if hub.Status().Connected {
		t.Fatal("hub registered a rejected connection")
	}
}

func TestReplacementConnectionWins(t *testing.T) {
	srv, hub := newServer(t)
	ws1 := fakeAddin(t, srv, srv.URL, func(r Request) *Response { return &Response{OK: true, Result: json.RawMessage(`1`)} })
	defer ws1.Close()
	waitConnected(t, hub)
	ws2 := fakeAddin(t, srv, srv.URL, func(r Request) *Response { return &Response{OK: true, Result: json.RawMessage(`2`)} })
	defer ws2.Close()
	time.Sleep(50 * time.Millisecond)
	if _, res := exec(t, srv, "x", 0); string(res.Result) != "2" {
		t.Errorf("got %s from old connection", res.Result)
	}
}

func TestStaticFiles(t *testing.T) {
	srv, _ := newServer(t)
	resp, err := srv.Client().Get(srv.URL + "/taskpane.html")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if b, _ := io.ReadAll(resp.Body); resp.StatusCode != 200 || string(b) != "<html>" {
		t.Errorf("%d %q", resp.StatusCode, b)
	}
}

func strconvQuote(s string) string { b, _ := json.Marshal(s); return string(b) }
