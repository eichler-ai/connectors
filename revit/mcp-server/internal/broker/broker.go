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

// pingParams is the payload of the add-in's `ping` heartbeat (PRD §05). It
// carries no instance_id (the connection already established that at register)
// and, since issue #31, an optional memory sample the add-in reads from its own
// Revit process; older add-ins send a bare ping and Memory stays nil.
type pingParams struct {
	Memory *registry.MemorySample `json:"memory"`
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
	// Search, when set, is told about every instance attach/detach so the
	// broker-side search_functions index (issue #107) is built as soon as an
	// instance registers and dropped when it goes.
	Search SearchIndexer

	// Logger receives best-effort diagnostic lines (connection lifecycle,
	// rejected auth attempts). Defaults to the standard logger if nil.
	Logger *log.Logger
}

// SearchIndexer is what Broker needs from internal/semsearch/manager.
type SearchIndexer interface {
	OnAttach(instanceID, revitVersion string)
	OnDetach(instanceID string)
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
		writeAuthRejection(fr, msg.ID, "auth-required",
			"the first message on a new connection must be a JSON-RPC request with method \"auth\" and a valid token")
		conn.Close()
		return
	}

	var params authParams
	if err := json.Unmarshal(msg.Params, &params); err != nil {
		writeAuthRejection(fr, msg.ID, "auth-malformed", fmt.Sprintf("auth params could not be decoded: %s", err.Error()))
		conn.Close()
		return
	}

	if !singleton.ValidateToken(b.Token, params.Token) {
		writeAuthRejection(fr, msg.ID, "auth-invalid-token", "the presented token does not match the broker's current token")
		conn.Close()
		return
	}
	if params.Role != RoleAddIn && params.Role != RoleAgentClient {
		writeAuthRejection(fr, msg.ID, "auth-invalid-role", fmt.Sprintf("unknown role %q; expected %q or %q", params.Role, RoleAddIn, RoleAgentClient))
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
// instanceID (and registerEpoch alongside it) is safe to read after
// conn.Serve() returns with no extra synchronization: Conn dispatches
// notifications inline on the same goroutine that's driving Serve's read
// loop (see transport.Conn.Serve's own comment), so the register handler
// below has always finished running — including having called
// AttachInstance — by the time Serve returns. Attach is therefore
// guaranteed to happen-before the DetachInstance call at the end of this
// function, never after it.
func (b *Broker) serveAddIn(rwc io.ReadWriteCloser) {
	conn := transport.NewConn(rwc)
	defer conn.Close() // idempotent alongside failPending's own close; belt-and-suspenders against any path that skips it
	var instanceID string
	var registerEpoch uint64

	conn.SetNotificationHandler(func(method string, params json.RawMessage) {
		switch method {
		case "register":
			var rp registerParams
			if err := json.Unmarshal(params, &rp); err != nil {
				b.logf("broker: malformed register notification: %v", err)
				return
			}
			// A second register on the SAME connection under a DIFFERENT
			// instance_id would otherwise strand the first: instanceID and
			// registerEpoch are single locals, so overwriting them leaves the
			// previous registration attached to this connection in all three
			// stores with nothing left holding the identity needed to detach
			// it. Serve's own teardown below would then only ever clean up the
			// last instance_id seen, and the earlier one would sit in the
			// registry -- advertised by list_instances, routable to a dead
			// connection -- until the 5-minute prune sweep aged it out.
			//
			// The real add-in never sends two instance_ids down one socket, so
			// this is defensive rather than observed. It is here because it is
			// the same orphan class as the teardown race in issue #111, and
			// found while fixing that: the guards are cheap and identical to
			// the ones the close path already uses.
			if instanceID != "" && instanceID != rp.InstanceID {
				b.logf("broker: connection re-registered from instance %s to %s; detaching the former", instanceID, rp.InstanceID)
				b.Execution.DetachInstance(instanceID, conn)
				b.Discovery.DetachInstance(instanceID, conn)
				if b.Search != nil {
					b.Search.OnDetach(instanceID)
				}
				b.Registry.RemoveIfEpoch(instanceID, registerEpoch)
			}

			instanceID = rp.InstanceID
			registerEpoch = b.Registry.Register(&registry.Instance{
				InstanceID:   rp.InstanceID,
				PID:          rp.PID,
				RevitVersion: rp.RevitVersion,
				Documents:    rp.Documents,
			}, time.Now())
			if displaced := b.Execution.AttachInstance(rp.InstanceID, conn); displaced != nil {
				// A register under an instance_id that's already attached
				// means the add-in redialed (usually after a network blip
				// left the old socket half-open, its serve goroutine still
				// blocked in a read that may not error for a long time).
				// Close the displaced connection: that unblocks its
				// goroutine so its teardown runs now — harmlessly, per the
				// identity/epoch guards below — and releases the socket,
				// instead of leaking both until TCP itself notices.
				b.logf("broker: instance %s re-registered on a new connection; closing its displaced one", rp.InstanceID)
				displaced.Close()
			}
			b.Discovery.AttachInstance(rp.InstanceID, conn)
			if b.Search != nil {
				b.Search.OnAttach(rp.InstanceID, rp.RevitVersion)
			}
		case "ping":
			// Heartbeat (PRD §05) — instanceID is only known once this
			// connection's own register has arrived; a ping can't
			// meaningfully precede that.
			if instanceID != "" {
				// Memory (issue #31) is best-effort: a malformed or absent
				// params object just means no sample this tick, never a
				// dropped heartbeat — the liveness timestamp still records.
				// A bare ping (no params — the common case, and every older
				// add-in) skips the decode entirely; only params that are
				// PRESENT but unparseable are logged, mirroring the malformed-
				// register log above, so a real wire regression leaves a trace
				// rather than silently yielding no sample forever.
				var pp pingParams
				if len(params) > 0 {
					if err := json.Unmarshal(params, &pp); err != nil {
						b.logf("broker: malformed ping params from instance %s (liveness still recorded): %v", instanceID, err)
					}
				}
				b.Registry.RecordPing(instanceID, time.Now(), pp.Memory)
			}
		}
	})

	_ = conn.Serve()
	if instanceID != "" {
		// A torn-down connection is immediate, certain proof that THIS
		// connection's registration is gone -- remove it now rather than
		// leaving it to age out via the heartbeat prune sweep (PRD §05),
		// which exists for the different case of a socket that's still
		// open but has gone quiet. Every step is guarded on this
		// connection (or the epoch its own register minted) still being
		// current, because this teardown can run arbitrarily late: a
		// half-open socket's Serve may not return until long after the
		// add-in redialed and re-registered the same stable instance_id,
		// and an unguarded teardown here would deregister that live
		// replacement (v1 integrated review). Each store guards itself —
		// conn identity for the two conn maps, the register epoch for the
		// registry — so no cross-store interleaving with a concurrent
		// re-register can strand or clobber anything.
		if b.Discovery.DetachInstance(instanceID, conn) && b.Search != nil {
			b.Search.OnDetach(instanceID)
		}
		b.Execution.DetachInstance(instanceID, conn)
		b.Registry.RemoveIfEpoch(instanceID, registerEpoch)
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
