// Package broker implements the broker's TCP-facing side: accepting
// connections from add-ins and from secondary-broker agent-client proxies,
// gating every connection on the shared auth token before anything else is
// processed (PRD §10), and wiring successfully-authenticated add-in
// connections into the instance registry and execution manager.
package broker

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/discovery"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/execution"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/singleton"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

const source = "mcp-server.internal.broker"

// Role identifies what kind of party a TCP connection belongs to, per PRD
// §05/§10.
type Role string

const (
	RoleAddIn       Role = "add-in"
	RoleAgentClient Role = "agent-client"
)

// authParams is the payload of the mandatory first message every connecting
// party must send, per PRD §10: "Every connecting party ... must present it
// on first message before any other command is accepted."
type authParams struct {
	Token string `json:"token"`
	Role  Role   `json:"role"`
}

// registerParams is the payload of the add-in's `register` notification,
// per PRD §05.
type registerParams struct {
	InstanceID   string              `json:"instance_id"`
	PID          int                 `json:"pid"`
	RevitVersion string              `json:"revit_version"`
	Documents    []registry.Document `json:"documents"`
}

// Broker ties the instance registry and execution manager to the TCP
// listener. It also runs an MCP server session over every successfully
// authenticated agent-client connection, so a secondary broker process's
// proxied stdio traffic is routed exactly like the primary's own stdio
// session (PRD §05 "Broker singleton & port contention").
type Broker struct {
	Token     string
	Registry  *registry.Registry
	Execution *execution.Manager
	Discovery *discovery.Router
	MCPServer *mcp.Server

	// Logger receives best-effort diagnostic lines (connection lifecycle,
	// rejected auth attempts). Defaults to the standard logger if nil.
	Logger *log.Logger
}

func (b *Broker) logf(format string, args ...any) {
	if b.Logger != nil {
		b.Logger.Printf(format, args...)
	}
}

// Serve accepts connections on ln until ctx is done or Accept fails. Each
// connection is handled in its own goroutine.
func (b *Broker) Serve(ctx context.Context, ln net.Listener) error {
	go func() {
		<-ctx.Done()
		ln.Close()
	}()
	for {
		conn, err := ln.Accept()
		if err != nil {
			select {
			case <-ctx.Done():
				return nil
			default:
				return err
			}
		}
		go b.handleConn(ctx, conn)
	}
}

// tail is the remainder of a net.Conn's stream after its first NDJSON line
// has been consumed via a bufio.Reader — reads continue from that same
// buffered reader so no bytes are lost, while writes and Close pass through
// to the underlying connection directly.
type tail struct {
	r    *bufio.Reader
	conn net.Conn
}

func (t *tail) Read(p []byte) (int, error)  { return t.r.Read(p) }
func (t *tail) Write(p []byte) (int, error) { return t.conn.Write(p) }
func (t *tail) Close() error                { return t.conn.Close() }

// maxAuthLineBytes bounds the unauthenticated first line the same way
// Framer's Scanner bounds every line post-auth (framing.go's
// maxLineBytes) — a peer that never sends a newline must not be able to
// grow bufio.Reader.ReadBytes' accumulated buffer without limit before
// auth has even succeeded. Small on purpose: a real auth message is a few
// hundred bytes.
const maxAuthLineBytes = 64 * 1024

// capReader bounds total bytes read until lifted, then reads through
// unbounded. Used to cap only the pre-auth line while still handing the
// same underlying *bufio.Reader off to tail afterward — an io.LimitReader
// can't do this, since once its budget is spent it stays exhausted for the
// life of the reader, which would silently truncate all legitimate
// post-auth traffic through the same buffer.
type capReader struct {
	r      io.Reader
	limit  int
	n      int
	lifted bool
}

func (c *capReader) lift() { c.lifted = true }

func (c *capReader) Read(p []byte) (int, error) {
	if c.lifted {
		return c.r.Read(p)
	}
	if c.n >= c.limit {
		return 0, fmt.Errorf("broker: line exceeds %d bytes without a newline", c.limit)
	}
	if room := c.limit - c.n; len(p) > room {
		p = p[:room]
	}
	n, err := c.r.Read(p)
	c.n += n
	return n, err
}

func (b *Broker) handleConn(ctx context.Context, conn net.Conn) {
	cr := &capReader{r: conn, limit: maxAuthLineBytes}
	br := bufio.NewReader(cr)
	_ = conn.SetReadDeadline(time.Now().Add(10 * time.Second))
	line, err := br.ReadBytes('\n')
	if err != nil {
		b.logf("broker: connection from %s closed before sending auth: %v", conn.RemoteAddr(), err)
		conn.Close()
		return
	}
	_ = conn.SetReadDeadline(time.Time{})
	// The rest of the connection's life (post-auth wire traffic) is
	// legitimately unbounded per line up to Framer's own maxLineBytes —
	// only the pre-auth phase needed the tighter cap.
	cr.lift()

	rest := &tail{r: br, conn: conn}
	fr := transport.NewFramer(rest, rest)

	var msg transport.Message
	if jsonErr := json.Unmarshal(line, &msg); jsonErr != nil || !msg.IsRequest() || msg.Method != "auth" {
		writeAuthRejection(fr, msg.ID, "auth_required",
			"the first message on a new connection must be a JSON-RPC request with method \"auth\" and a valid token")
		conn.Close()
		return
	}

	var params authParams
	if err := json.Unmarshal(msg.Params, &params); err != nil {
		writeAuthRejection(fr, msg.ID, "auth_malformed", fmt.Sprintf("auth params could not be decoded: %s", err.Error()))
		conn.Close()
		return
	}

	if !singleton.ValidateToken(b.Token, params.Token) {
		writeAuthRejection(fr, msg.ID, "auth_invalid_token", "the presented token does not match the broker's current token")
		conn.Close()
		return
	}
	if params.Role != RoleAddIn && params.Role != RoleAgentClient {
		writeAuthRejection(fr, msg.ID, "auth_invalid_role", fmt.Sprintf("unknown role %q; expected %q or %q", params.Role, RoleAddIn, RoleAgentClient))
		conn.Close()
		return
	}

	okResp, _ := transport.NewResultResponse(*msg.ID, map[string]any{"ok": true})
	if err := fr.WriteMessage(okResp); err != nil {
		conn.Close()
		return
	}

	switch params.Role {
	case RoleAddIn:
		b.serveAddIn(rest)
	case RoleAgentClient:
		b.serveAgentClient(ctx, rest)
	}
}

func writeAuthRejection(fr *transport.Framer, id *json.RawMessage, code, message string) {
	rec := diag.New(diag.SeverityError, code, source, message).
		WithRemedy("reconnect and send a valid auth request as the very first message")
	var idRaw json.RawMessage
	if id != nil {
		idRaw = *id
	}
	_ = fr.WriteMessage(transport.NewErrorResponse(idRaw, transport.ErrCodeUnauthorized, message, rec))
}

// serveAddIn handles a connection authenticated with role add-in: it
// expects a `register` notification (PRD §05) and attaches the connection
// to the execution manager so execute_script/poll_execution/
// cancel_execution can route to it.
//
// instanceID is safe to read after conn.Serve() returns with no extra
// synchronization: Conn dispatches notifications inline on the same
// goroutine that's driving Serve's read loop (see transport.Conn.Serve's
// own comment), so the register handler below has always finished running
// — including having called AttachInstance — by the time Serve returns.
// Attach is therefore guaranteed to happen-before the DetachInstance call
// at the end of this function, never after it.
func (b *Broker) serveAddIn(rwc io.ReadWriteCloser) {
	conn := transport.NewConn(rwc)
	defer conn.Close() // idempotent alongside failPending's own close; belt-and-suspenders against any path that skips it
	var instanceID string

	conn.SetNotificationHandler(func(method string, params json.RawMessage) {
		switch method {
		case "register":
			var rp registerParams
			if err := json.Unmarshal(params, &rp); err != nil {
				b.logf("broker: malformed register notification: %v", err)
				return
			}
			instanceID = rp.InstanceID
			b.Registry.Register(&registry.Instance{
				InstanceID:   rp.InstanceID,
				PID:          rp.PID,
				RevitVersion: rp.RevitVersion,
				Documents:    rp.Documents,
			})
			b.Execution.AttachInstance(rp.InstanceID, conn)
			b.Discovery.AttachInstance(rp.InstanceID, conn)
		case "ping":
			// Heartbeat (PRD §05) — instanceID is only known once this
			// connection's own register has arrived; a ping can't
			// meaningfully precede that.
			if instanceID != "" {
				b.Registry.RecordPing(instanceID)
			}
		}
	})

	_ = conn.Serve()
	if instanceID != "" {
		b.Execution.DetachInstance(instanceID)
		b.Discovery.DetachInstance(instanceID)
	}
}

// serveAgentClient handles a connection authenticated with role
// agent-client (a secondary broker process proxying its own stdio MCP
// traffic, per PRD §05): the remaining stream is run as a normal MCP server
// session, identical in behavior to the primary's own stdio session.
func (b *Broker) serveAgentClient(ctx context.Context, rwc io.ReadWriteCloser) {
	t := &mcp.IOTransport{Reader: rwc, Writer: rwc}
	if err := b.MCPServer.Run(ctx, t); err != nil {
		b.logf("broker: agent-client session ended: %v", err)
	}
}
