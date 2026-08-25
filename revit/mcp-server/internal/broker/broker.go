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

func (b *Broker) handleConn(ctx context.Context, conn net.Conn) {
	br := bufio.NewReader(conn)
	_ = conn.SetReadDeadline(time.Now().Add(10 * time.Second))
	line, err := br.ReadBytes('\n')
	if err != nil {
		b.logf("broker: connection from %s closed before sending auth: %v", conn.RemoteAddr(), err)
		conn.Close()
		return
	}
	_ = conn.SetReadDeadline(time.Time{})

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
func (b *Broker) serveAddIn(rwc io.ReadWriteCloser) {
	conn := transport.NewConn(rwc)
	var instanceID string

	conn.SetNotificationHandler(func(method string, params json.RawMessage) {
		if method != "register" {
			return
		}
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
	})

	_ = conn.Serve()
	if instanceID != "" {
		b.Execution.DetachInstance(instanceID)
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
