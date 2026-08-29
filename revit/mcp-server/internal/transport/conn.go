package transport

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strconv"
	"sync"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
)

const source = "mcp-server.internal.transport"

// ErrClosed is returned by Conn operations attempted after Close.
var ErrClosed = errors.New("transport: connection closed")

// RequestHandler answers an incoming JSON-RPC request from the peer. A
// non-nil *RPCError becomes the error response; otherwise the returned value
// is marshaled as the result.
type RequestHandler func(ctx context.Context, method string, params json.RawMessage) (result any, rpcErr *RPCError)

// NotificationHandler observes an incoming JSON-RPC notification.
type NotificationHandler func(method string, params json.RawMessage)

// Conn is a bidirectional JSON-RPC 2.0 peer connection, NDJSON-framed per
// PRD §05. Either side may originate requests/notifications; Conn matches
// responses to outstanding calls by ID and dispatches inbound
// requests/notifications to caller-supplied handlers. This is the broker's
// view of one add-in connection, one agent-client proxy connection, or (from
// the add-in's hypothetical perspective, useful for the in-process fake peer
// used in tests) the other end of the same link.
type Conn struct {
	framer *Framer
	closer io.Closer

	nextID int64

	mu      sync.Mutex
	pending map[string]chan *Message
	closed  bool

	onRequest      RequestHandler
	onNotification NotificationHandler
}

// NewConn wraps rwc (typically a net.Conn) as a Conn. Call Serve to start
// processing inbound traffic.
func NewConn(rwc io.ReadWriteCloser) *Conn {
	return &Conn{
		framer:  NewFramer(rwc, rwc),
		closer:  rwc,
		pending: make(map[string]chan *Message),
	}
}

// SetRequestHandler installs the handler invoked for inbound requests. Must
// be called before Serve to avoid a race with the read loop; safe to call
// again later to swap handlers.
func (c *Conn) SetRequestHandler(h RequestHandler) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.onRequest = h
}

// SetNotificationHandler installs the handler invoked for inbound
// notifications.
func (c *Conn) SetNotificationHandler(h NotificationHandler) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.onNotification = h
}

// Serve runs the read loop until the connection is closed or a framing
// error occurs. It returns a non-nil error in both cases (io.EOF on a clean
// peer close). Run it in its own goroutine.
func (c *Conn) Serve() error {
	for {
		msg, err := c.framer.ReadMessage()
		if err != nil {
			c.failPending()
			return err
		}

		switch {
		case msg.IsResponse():
			c.deliverResponse(msg)
		case msg.IsNotification():
			c.mu.Lock()
			h := c.onNotification
			c.mu.Unlock()
			if h != nil {
				// Dispatched inline, not via `go`, deliberately: callers
				// (e.g. broker.serveAddIn's register handler) rely on a
				// notification's handler having fully run by the time a
				// subsequent read — or Serve itself returning — is
				// observed. An async dispatch here raced the handler
				// against Serve's own exit, so a connection that dropped
				// right after `register` could leave the instance attached
				// but never detached. Handlers must stay fast/non-blocking,
				// same as any other code on the read loop's own goroutine.
				h(msg.Method, msg.Params)
			}
		case msg.IsRequest():
			c.handleRequest(msg)
		}
	}
}

func (c *Conn) deliverResponse(msg *Message) {
	key := string(*msg.ID)
	c.mu.Lock()
	ch, ok := c.pending[key]
	if ok {
		delete(c.pending, key)
	}
	c.mu.Unlock()
	if ok {
		ch <- msg
	}
}

func (c *Conn) handleRequest(msg *Message) {
	c.mu.Lock()
	h := c.onRequest
	c.mu.Unlock()

	go func() {
		var result any
		var rpcErr *RPCError
		if h == nil {
			text := fmt.Sprintf("no handler registered for method %q on this connection", msg.Method)
			rpcErr = &RPCError{
				Code:    ErrCodeMethodNotFound,
				Message: text,
				Data: diag.New(diag.SeverityError, "no-handler-registered", source, text).
					WithDetail(map[string]any{"method": msg.Method}).
					WithRemedy("this is a broker-side wiring bug, not something the caller can retry around — report it"),
			}
		} else {
			result, rpcErr = h(context.Background(), msg.Method, msg.Params)
		}

		var resp *Message
		if rpcErr != nil {
			resp = NewErrorResponse(*msg.ID, rpcErr.Code, rpcErr.Message, rpcErr.Data)
		} else {
			var err error
			resp, err = NewResultResponse(*msg.ID, result)
			if err != nil {
				encodeMsg := fmt.Sprintf("encoding the result for method %q as JSON failed: %s", msg.Method, err.Error())
				resp = NewErrorResponse(*msg.ID, ErrCodeInternalError, encodeMsg,
					diag.New(diag.SeverityError, "result-encode-failed", source, encodeMsg).
						WithDetail(map[string]any{"method": msg.Method}))
			}
		}
		_ = c.framer.WriteMessage(resp)
	}()
}

// failPending marks the connection closed and unblocks every pending Call.
// It also closes the underlying resource itself — Serve's only path back
// to its caller after a read error is through here, and Close() no-ops
// once c.closed is already true, so without this the fd/socket a Serve
// loop was reading from would never actually be released (a leak on every
// disconnect, not just an explicit Close() call).
func (c *Conn) failPending() {
	c.mu.Lock()
	alreadyClosed := c.closed
	c.closed = true
	pending := c.pending
	c.pending = make(map[string]chan *Message)
	c.mu.Unlock()

	for _, ch := range pending {
		close(ch)
	}
	if !alreadyClosed {
		_ = c.closer.Close()
	}
}

// Call sends a JSON-RPC request and blocks until a response arrives, ctx is
// done, or the connection closes. err is a transport-level failure
// (timeout, closed connection); rpcErr is a peer-reported JSON-RPC error —
// the two are distinct so callers can tell "never heard back" from "heard
// back with an error".
func (c *Conn) Call(ctx context.Context, method string, params any) (result json.RawMessage, rpcErr *RPCError, err error) {
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return nil, nil, ErrClosed
	}
	c.nextID++
	idStr := strconv.FormatInt(c.nextID, 10)
	idRaw := json.RawMessage(strconv.Quote(idStr))
	key := string(idRaw)
	ch := make(chan *Message, 1)
	c.pending[key] = ch
	c.mu.Unlock()

	req, err := NewRequest(idRaw, method, params)
	if err != nil {
		c.mu.Lock()
		delete(c.pending, key)
		c.mu.Unlock()
		return nil, nil, fmt.Errorf("transport: building request: %w", err)
	}
	if err := c.framer.WriteMessage(req); err != nil {
		c.mu.Lock()
		delete(c.pending, key)
		c.mu.Unlock()
		return nil, nil, fmt.Errorf("transport: sending request: %w", err)
	}

	select {
	case msg, ok := <-ch:
		if !ok {
			return nil, nil, ErrClosed
		}
		return msg.Result, msg.Error, nil
	case <-ctx.Done():
		c.mu.Lock()
		delete(c.pending, key)
		c.mu.Unlock()
		return nil, nil, ctx.Err()
	}
}

// Notify sends a JSON-RPC notification (no response expected).
func (c *Conn) Notify(method string, params any) error {
	msg, err := NewNotification(method, params)
	if err != nil {
		return fmt.Errorf("transport: building notification: %w", err)
	}
	return c.framer.WriteMessage(msg)
}

// Close closes the underlying connection and unblocks any in-flight Serve
// read and pending Calls.
func (c *Conn) Close() error {
	c.mu.Lock()
	already := c.closed
	c.closed = true
	c.mu.Unlock()
	if already {
		return nil
	}
	return c.closer.Close()
}
