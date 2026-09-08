package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"sync"
	"sync/atomic"
	"time"

	"golang.org/x/net/websocket"
)

// Request is what the bridge sends to the add-in.
type Request struct {
	ID     string `json:"id"`
	Script string `json:"script"`
	// TimeoutMs is advisory: the add-in cannot kill a running script, but it reports the deadline in
	// its log so a hang is visible from the task pane too.
	TimeoutMs int64 `json:"timeoutMs"`
}

// Response is what the add-in sends back. Result is left as raw JSON so the CLI can print it as-is.
type Response struct {
	ID         string          `json:"id"`
	OK         bool            `json:"ok"`
	Result     json.RawMessage `json:"result,omitempty"`
	Error      *ScriptError    `json:"error,omitempty"`
	DurationMs float64         `json:"durationMs"`
	Truncated  bool            `json:"truncated,omitempty"`
}

// ScriptError carries whatever Office.js put on the thrown error. Code and DebugInfo are the
// OfficeExtension.Error fields; they are absent for plain JavaScript errors.
type ScriptError struct {
	Name      string          `json:"name"`
	Message   string          `json:"message"`
	Code      string          `json:"code,omitempty"`
	DebugInfo json.RawMessage `json:"debugInfo,omitempty"`
	Stack     string          `json:"stack,omitempty"`
}

// hello is the first message the add-in sends after connecting.
type hello struct {
	Type     string `json:"type"`
	Host     string `json:"host"`
	Platform string `json:"platform"`
	Version  string `json:"version"`
	Workbook string `json:"workbook"`
}

// Hub holds the single add-in connection the POC supports and routes exec requests to it.
type Hub struct {
	mu      sync.Mutex
	conn    *websocket.Conn
	info    hello
	pending map[string]chan Response
	seq     atomic.Int64
}

func NewHub() *Hub { return &Hub{pending: map[string]chan Response{}} }

var ErrNotConnected = errors.New("no add-in connected: open the task pane in Excel")

// Status describes the current add-in connection, for the /status endpoint.
type Status struct {
	Connected bool   `json:"connected"`
	Host      string `json:"host,omitempty"`
	Platform  string `json:"platform,omitempty"`
	Version   string `json:"version,omitempty"`
	Workbook  string `json:"workbook,omitempty"`
}

func (h *Hub) Status() Status {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.conn == nil {
		return Status{}
	}
	return Status{true, h.info.Host, h.info.Platform, h.info.Version, h.info.Workbook}
}

// Serve is the websocket.Handler for /ws. A new connection replaces any previous one: Excel reloads
// the task pane freely (tab switch, resize, re-open) and the stale socket may not have closed yet.
func (h *Hub) Serve(ws *websocket.Conn) {
	var hi hello
	if err := websocket.JSON.Receive(ws, &hi); err != nil || hi.Type != "hello" {
		log.Printf("ws: rejected connection from %s: bad hello (%v)", ws.Request().RemoteAddr, err)
		ws.Close()
		return
	}
	h.mu.Lock()
	if old := h.conn; old != nil {
		// Tell the evicted client why, so it does not reconnect and evict us back (two live runner
		// instances otherwise ping-pong forever, and in-flight replies go down the wrong socket).
		log.Printf("ws: replacing previous add-in connection")
		_ = websocket.JSON.Send(old, map[string]string{"type": "replaced"})
		old.Close()
	}
	h.conn, h.info = ws, hi
	h.mu.Unlock()
	log.Printf("ws: add-in connected: %s %s v%s workbook=%q", hi.Host, hi.Platform, hi.Version, hi.Workbook)

	// x/net/websocket's Codec.Receive returns a single frame, and Chrome fragments large messages
	// (a multi-MB export arrives as many frames), so decode from the connection's byte stream instead:
	// Conn.Read chains frames, and JSON objects are self-delimiting.
	dec := json.NewDecoder(ws)
	for {
		var resp Response
		if err := dec.Decode(&resp); err != nil {
			if !errors.Is(err, io.EOF) {
				log.Printf("ws: receive: %v", err)
			}
			break
		}
		h.mu.Lock()
		ch := h.pending[resp.ID]
		delete(h.pending, resp.ID)
		h.mu.Unlock()
		if ch == nil {
			log.Printf("ws: reply for unknown/expired request %s dropped", resp.ID)
			continue
		}
		ch <- resp
	}

	h.mu.Lock()
	if h.conn == ws {
		h.conn = nil
		h.info = hello{}
	}
	h.mu.Unlock()
	log.Printf("ws: add-in disconnected")
}

// Exec sends script to the add-in and waits for its reply or ctx's deadline.
func (h *Hub) Exec(ctx context.Context, script string) (Response, error) {
	id := fmt.Sprintf("r%d", h.seq.Add(1))
	timeout := time.Duration(0)
	if dl, ok := ctx.Deadline(); ok {
		timeout = time.Until(dl)
	}
	ch := make(chan Response, 1)

	h.mu.Lock()
	conn := h.conn
	if conn == nil {
		h.mu.Unlock()
		return Response{}, ErrNotConnected
	}
	h.pending[id] = ch
	h.mu.Unlock()

	err := websocket.JSON.Send(conn, Request{ID: id, Script: script, TimeoutMs: timeout.Milliseconds()})
	if err != nil {
		h.forget(id)
		return Response{}, fmt.Errorf("send to add-in: %w", err)
	}
	select {
	case resp := <-ch:
		return resp, nil
	case <-ctx.Done():
		h.forget(id)
		return Response{}, fmt.Errorf("script %s: %w (the add-in cannot interrupt a running script; reload the task pane if it is hung)", id, ctx.Err())
	}
}

func (h *Hub) forget(id string) {
	h.mu.Lock()
	delete(h.pending, id)
	h.mu.Unlock()
}
