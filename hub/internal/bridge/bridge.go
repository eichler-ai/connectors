// Package bridge accepts bridge connections on /<connector>/bridge and runs
// the exec/result exchange over them (PRD §07/§08). It owns everything that
// is about a socket — hello enforcement, origin policy, size limits,
// keepalive, in-flight exec correlation — and hands the resulting identity to
// the registry, which owns "which connection is live for this key".
package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"net/url"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"

	"github.com/eichler-ai/connectors/hub/internal/registry"
	"github.com/eichler-ai/connectors/hub/protocol"
	"github.com/eichler-ai/connectors/internal/auth"
)

// Options tune the service; zero values take the defaults below.
type Options struct {
	// ResultBytes is Limits.ResultBytes sent with every exec. Default 16 MiB,
	// the POC's figure, so a whole-document export fits.
	ResultBytes int
	// PingInterval is how often the hub sends a protocol ping; a bridge that
	// has sent nothing for three intervals is closed as unresponsive.
	PingInterval time.Duration
	// HelloTimeout bounds how long a fresh socket may stay silent before the
	// hub gives up waiting for hello.
	HelloTimeout time.Duration
	// AllowedOrigins are browser origins ("https://host[:port]") permitted to
	// open the socket in addition to the hub's own. The hub's public origin
	// and any HUB_ALLOWED_ORIGINS go here.
	AllowedOrigins []string
	Logger         *slog.Logger
}

const (
	defaultResultBytes  = 16 << 20
	defaultPingInterval = 30 * time.Second
	defaultHelloTimeout = 10 * time.Second
	// defaultExecTimeout only guards a caller that passes none; the hub
	// package applies the real default and maximum.
	defaultExecTimeout = 30 * time.Second
	// envelopeHeadroom is added to ResultBytes for the socket read limit: the
	// result payload sits inside a JSON envelope with an error/notices/head
	// alongside, and a bridge that truncated at exactly ResultBytes must not
	// be cut off by the framing.
	envelopeHeadroom = 1 << 20
	// sendTimeout bounds a single write to a bridge. A browser that has
	// stopped reading (backgrounded tab) must not block the hub's handler.
	sendTimeout = 10 * time.Second
)

// Errors Exec returns, each mapped to a diagnostic code by the hub package.
var (
	// ErrTimeout: the bridge did not reply within the exec's timeout. A late
	// reply is dropped, not delivered.
	ErrTimeout = errors.New("script timed out")
	// ErrBridgeGone: the bridge disconnected or was replaced while the exec
	// was in flight (§07: `bridge-reconnected`).
	ErrBridgeGone = errors.New("bridge connection ended while the script was running")
)

// Service is one bridge endpoint per hub process, shared by every connector;
// the connector slug arrives with the request path.
type Service struct {
	reg  *registry.Registry
	auth auth.Authenticator
	opts Options
	log  *slog.Logger

	// origins is the allow-list in canonical form (lowercase scheme://host).
	origins map[string]bool

	// pending holds every exec awaiting a result. Bounded by construction:
	// an entry exists only for the lifetime of one Exec call, which is itself
	// bounded by its timeout, and every entry is removed on result, timeout,
	// caller cancellation, or the bridge's disconnect/replacement.
	mu      sync.Mutex
	pending map[string]*pendingExec
	seq     atomic.Int64
}

type pendingExec struct {
	bridge *registry.Bridge
	// ch has capacity 1 so a delivery never blocks the reader on a caller
	// that already gave up; the second delivery for an id is impossible
	// because the entry is deleted on the first.
	ch chan protocol.Result
	// notices collects notice messages the bridge sends about this exec
	// while it runs (e.g. cannot-cancel); merged into the result.
	notices []protocol.Notice
	// format is set only for a pending export: the file format/extension the
	// hub itself asked the bridge to produce. The files upload handler
	// trusts this, never a client-supplied value, when it picks the stored
	// object's extension — see UploadAuthorize.
	format string
}

// New builds the service.
func New(reg *registry.Registry, a auth.Authenticator, opts Options) *Service {
	if opts.ResultBytes == 0 {
		opts.ResultBytes = defaultResultBytes
	}
	if opts.PingInterval == 0 {
		opts.PingInterval = defaultPingInterval
	}
	if opts.HelloTimeout == 0 {
		opts.HelloTimeout = defaultHelloTimeout
	}
	if opts.Logger == nil {
		opts.Logger = slog.Default()
	}
	s := &Service{reg: reg, auth: a, opts: opts, log: opts.Logger, origins: map[string]bool{}, pending: map[string]*pendingExec{}}
	for _, o := range opts.AllowedOrigins {
		if c := canonicalOrigin(o); c != "" {
			s.origins[c] = true
		}
	}
	return s
}

// ResultBytes is the cap advertised to bridges.
func (s *Service) ResultBytes() int { return s.opts.ResultBytes }

func canonicalOrigin(o string) string {
	u, err := url.Parse(strings.TrimSpace(o))
	if err != nil || u.Scheme == "" || u.Host == "" {
		return ""
	}
	return strings.ToLower(u.Scheme + "://" + u.Host)
}

// originAllowed is the policy: no Origin header (a non-browser bridge such as
// a desktop plugin) passes; a browser origin must be the hub's own host — on
// either scheme, because behind Cloud Run the request arrives as http while
// the page's origin is https — or on the allow-list.
func (s *Service) originAllowed(r *http.Request) bool {
	origin := r.Header.Get("Origin")
	if origin == "" {
		return true
	}
	c := canonicalOrigin(origin)
	if c == "" {
		return false
	}
	host := strings.ToLower(r.Host)
	return c == "https://"+host || c == "http://"+host || s.origins[c]
}

// Handler returns the http.Handler for /<connector>/bridge.
func (s *Service) Handler(connector string) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !s.originAllowed(r) {
			s.log.Warn("bridge: origin rejected", "connector", connector, "origin", r.Header.Get("Origin"), "host", r.Host)
			http.Error(w, "origin not allowed", http.StatusForbidden)
			return
		}
		conn, err := websocket.Accept(w, r, &websocket.AcceptOptions{
			// Origin was checked above by our own policy, which is the one
			// place it is defined; the library's pattern matcher is not used.
			InsecureSkipVerify: true,
		})
		if err != nil {
			s.log.Warn("bridge: accept failed", "connector", connector, "err", err)
			return
		}
		s.serve(r.Context(), connector, conn, r.RemoteAddr)
	})
}

// serve runs one connection to completion.
func (s *Service) serve(ctx context.Context, connector string, conn *websocket.Conn, remote string) {
	// Large results arrive fragmented from browsers; coder/websocket
	// reassembles them behind Read, and this limit is the only guard on how
	// big that reassembly may get.
	conn.SetReadLimit(int64(s.opts.ResultBytes + envelopeHeadroom))

	hello, err := s.readHello(ctx, conn)
	if err != nil {
		s.log.Warn("bridge: hello refused", "connector", connector, "remote", remote, "reason", err)
		_ = conn.Close(websocket.StatusPolicyViolation, err.Error())
		return
	}
	if hello.Connector != connector {
		reason := fmt.Sprintf("hello is for connector %q on the %q endpoint", hello.Connector, connector)
		s.log.Warn("bridge: hello refused", "connector", connector, "remote", remote, "reason", reason)
		_ = conn.Close(websocket.StatusPolicyViolation, reason)
		return
	}
	principal, err := s.auth.VerifyBridgeToken(ctx, hello.Token)
	if err != nil {
		s.log.Warn("bridge: hello refused", "connector", connector, "remote", remote, "instance_id", hello.InstanceID, "reason", "token rejected")
		_ = conn.Close(websocket.StatusPolicyViolation, "token rejected")
		return
	}
	// A pane's token is bound to one connector (auth.Principal.Scopes); a
	// token with no scopes (the dev token) is good for any.
	if len(principal.Scopes) > 0 && !slices.Contains(principal.Scopes, connector) {
		s.log.Warn("bridge: hello refused", "connector", connector, "remote", remote, "instance_id", hello.InstanceID, "user", principal.UserID, "reason", "token is for another connector")
		_ = conn.Close(websocket.StatusPolicyViolation, "token rejected: it was issued for another connector")
		return
	}

	b := &registry.Bridge{
		Key:             registry.Key{UserID: principal.UserID, Connector: connector, InstanceID: hello.InstanceID},
		Host:            hello.Host,
		BridgeVersion:   hello.BridgeVersion,
		ProtocolVersion: hello.ProtocolVersion,
		Since:           time.Now(),
		Link:            &link{conn: conn},
	}
	if old := s.reg.Register(b, hello.Documents); old != nil {
		s.log.Info("bridge: replaced", "user", b.UserID, "connector", connector, "instance_id", b.InstanceID)
		// The loser learns why so it stands down instead of reconnecting and
		// evicting us back. Its in-flight execs fail now rather than when its
		// half-open socket finally notices; the close runs off this goroutine
		// because a close handshake against a dead peer waits for its timeout.
		s.failPending(old, ErrBridgeGone)
		go func() {
			ctx, cancel := context.WithTimeout(context.Background(), sendTimeout)
			defer cancel()
			_ = old.Link.Send(ctx, protocol.New(protocol.MethodReplaced, protocol.Replaced{Reason: "a newer connection with this instance_id took over"}))
			old.Link.Close("replaced by a newer connection")
		}()
	}
	s.log.Info("bridge: connected", "user", b.UserID, "connector", connector, "instance_id", b.InstanceID,
		"host", hello.Host.App, "platform", hello.Host.Platform, "host_version", hello.Host.Version,
		"bridge_version", hello.BridgeVersion, "documents", len(hello.Documents), "remote", remote)

	lastSeen := &atomic.Int64{}
	lastSeen.Store(time.Now().UnixNano())
	stop := make(chan struct{})
	go s.keepalive(b, lastSeen, stop)

	s.readLoop(ctx, b, conn, lastSeen)
	close(stop)

	removed := s.reg.Unregister(b)
	s.failPending(b, ErrBridgeGone)
	s.log.Info("bridge: disconnected", "user", b.UserID, "connector", connector, "instance_id", b.InstanceID, "was_live", removed)
}

func (s *Service) readHello(ctx context.Context, conn *websocket.Conn) (protocol.Hello, error) {
	// The timeout is a close frame from another goroutine rather than a read
	// deadline: coder/websocket tears the connection down when a read context
	// expires, and then no reason can be sent — the silent client would see a
	// bare EOF instead of "expected hello first".
	timer := time.AfterFunc(s.opts.HelloTimeout, func() {
		_ = conn.Close(websocket.StatusPolicyViolation, fmt.Sprintf("expected hello first: nothing within %s", s.opts.HelloTimeout))
	})
	msg, err := readMessage(ctx, conn)
	if !timer.Stop() {
		return protocol.Hello{}, fmt.Errorf("expected hello first: nothing within %s", s.opts.HelloTimeout)
	}
	if err != nil {
		return protocol.Hello{}, fmt.Errorf("expected hello first: %w", err)
	}
	if msg.Method != protocol.MethodHello {
		return protocol.Hello{}, fmt.Errorf("expected hello first, got %q", msg.Method)
	}
	var h protocol.Hello
	if err := msg.Decode(&h); err != nil {
		return protocol.Hello{}, fmt.Errorf("bad hello: %w", err)
	}
	if h.ProtocolVersion != protocol.Version {
		return protocol.Hello{}, fmt.Errorf("bridge-outdated: protocol_version %d, hub speaks %d", h.ProtocolVersion, protocol.Version)
	}
	if h.InstanceID == "" {
		return protocol.Hello{}, errors.New("bad hello: instance_id is required")
	}
	return h, nil
}

func readMessage(ctx context.Context, conn *websocket.Conn) (protocol.Message, error) {
	typ, data, err := conn.Read(ctx)
	if err != nil {
		return protocol.Message{}, err
	}
	if typ != websocket.MessageText {
		return protocol.Message{}, errors.New("binary frames are not part of the protocol")
	}
	var msg protocol.Message
	if err := json.Unmarshal(data, &msg); err != nil {
		return protocol.Message{}, fmt.Errorf("not a JSON-RPC message: %w", err)
	}
	return msg, nil
}

func (s *Service) readLoop(ctx context.Context, b *registry.Bridge, conn *websocket.Conn, lastSeen *atomic.Int64) {
	for {
		msg, err := readMessage(ctx, conn)
		if err != nil {
			if websocket.CloseStatus(err) != websocket.StatusNormalClosure {
				s.log.Info("bridge: read ended", "instance_id", b.InstanceID, "err", err)
			}
			return
		}
		lastSeen.Store(time.Now().UnixNano())
		switch msg.Method {
		case protocol.MethodRegister:
			var reg protocol.Register
			if err := msg.Decode(&reg); err != nil {
				s.log.Warn("bridge: bad register", "instance_id", b.InstanceID, "err", err)
				continue
			}
			s.reg.UpdateDocuments(b, reg.Documents)
			s.log.Info("bridge: register", "user", b.UserID, "connector", b.Connector, "instance_id", b.InstanceID, "documents", len(reg.Documents))
		case protocol.MethodResult:
			var res protocol.Result
			if err := msg.Decode(&res); err != nil {
				s.log.Warn("bridge: bad result", "instance_id", b.InstanceID, "err", err)
				continue
			}
			s.deliver(b, res)
		case protocol.MethodNotice:
			var n protocol.Notice
			if err := msg.Decode(&n); err != nil {
				s.log.Warn("bridge: bad notice", "instance_id", b.InstanceID, "err", err)
				continue
			}
			s.notice(b, n)
		case protocol.MethodPing:
			go s.send(b, protocol.New(protocol.MethodPong, protocol.Pong{}))
		case protocol.MethodPong:
			// lastSeen already updated; nothing else to do.
		case protocol.MethodHello:
			s.log.Warn("bridge: second hello ignored", "instance_id", b.InstanceID)
		default:
			s.log.Warn("bridge: unknown method ignored", "instance_id", b.InstanceID, "method", msg.Method)
		}
	}
}

func (s *Service) keepalive(b *registry.Bridge, lastSeen *atomic.Int64, stop <-chan struct{}) {
	t := time.NewTicker(s.opts.PingInterval)
	defer t.Stop()
	for {
		select {
		case <-stop:
			return
		case <-t.C:
			if since := time.Since(time.Unix(0, lastSeen.Load())); since > 3*s.opts.PingInterval {
				s.log.Warn("bridge: unresponsive, closing", "instance_id", b.InstanceID, "silent_for", since.Round(time.Second))
				b.Link.Close("no message for " + since.Round(time.Second).String())
				return
			}
			s.send(b, protocol.New(protocol.MethodPing, protocol.Ping{}))
		}
	}
}

func (s *Service) send(b *registry.Bridge, msg protocol.Message) {
	ctx, cancel := context.WithTimeout(context.Background(), sendTimeout)
	defer cancel()
	if err := s.reg.Send(ctx, b, msg); err != nil && !errors.Is(err, registry.ErrNotRegistered) {
		s.log.Warn("bridge: send failed", "instance_id", b.InstanceID, "method", msg.Method, "err", err)
	}
}

// ExecRequest is what a connector asks the hub to run.
type ExecRequest struct {
	DocumentID string
	Script     string
	Language   string
	Timeout    time.Duration
}

// Exec sends the script to b and waits for its result, the timeout, or ctx.
// On timeout a cancel is forwarded (best effort) and a reply arriving later
// is dropped: the caller has already been told, and a result nobody is
// waiting for must not leak into a later exec with a recycled channel.
func (s *Service) Exec(ctx context.Context, b *registry.Bridge, req ExecRequest) (protocol.Result, error) {
	if req.Timeout <= 0 {
		req.Timeout = defaultExecTimeout
	}
	id := fmt.Sprintf("e%d", s.seq.Add(1))
	msg := protocol.New(protocol.MethodExec, protocol.Exec{
		ID:         id,
		DocumentID: req.DocumentID,
		Script:     req.Script,
		Language:   req.Language,
		TimeoutMs:  req.Timeout.Milliseconds(),
		Limits:     protocol.Limits{ResultBytes: s.opts.ResultBytes},
	})
	return s.roundTrip(ctx, b, id, msg, req.Timeout, "")
}

// ExportRequest is what a connector asks the hub to have the bridge produce
// as a file (§10/§11).
type ExportRequest struct {
	DocumentID string
	Format     string
	Timeout    time.Duration
}

// Export sends `export` to b and waits for its outcome: an error `result`
// from the bridge (it never got as far as uploading), a synthetic success
// `result` the files upload handler delivers through CompleteExport once the
// bytes are safely stored, the timeout, or ctx. The wait is otherwise
// identical to Exec's — same pending map, same cancel-on-timeout, same
// dropped-late-reply behaviour — because an export id is correlated exactly
// like an exec id (§10).
func (s *Service) Export(ctx context.Context, b *registry.Bridge, req ExportRequest) (protocol.Result, error) {
	if req.Timeout <= 0 {
		req.Timeout = defaultExecTimeout
	}
	id := fmt.Sprintf("x%d", s.seq.Add(1))
	msg := protocol.New(protocol.MethodExport, protocol.Export{ID: id, Format: req.Format, DocumentID: req.DocumentID})
	return s.roundTrip(ctx, b, id, msg, req.Timeout, req.Format)
}

// roundTrip is Exec and Export's shared send-and-wait: register a pending
// entry, send msg, and wait for its result channel, timeout, or ctx. format
// is empty for an exec (UploadAuthorize refuses any pending entry with no
// format, so an exec id can never be mistaken for an export's).
func (s *Service) roundTrip(ctx context.Context, b *registry.Bridge, id string, msg protocol.Message, timeout time.Duration, format string) (protocol.Result, error) {
	p := &pendingExec{bridge: b, ch: make(chan protocol.Result, 1), format: format}
	s.mu.Lock()
	s.pending[id] = p
	s.mu.Unlock()

	sendCtx, cancel := context.WithTimeout(ctx, sendTimeout)
	err := s.reg.Send(sendCtx, b, msg)
	cancel()
	if err != nil {
		s.forget(id)
		if errors.Is(err, registry.ErrNotRegistered) {
			return protocol.Result{}, ErrBridgeGone
		}
		return protocol.Result{}, fmt.Errorf("send %s to bridge: %w", id, err)
	}

	timer := time.NewTimer(timeout)
	defer timer.Stop()
	select {
	case res, ok := <-p.ch:
		if !ok {
			return protocol.Result{}, ErrBridgeGone
		}
		s.mu.Lock()
		for _, n := range p.notices {
			res.Notices = append(res.Notices, n.Record)
		}
		s.mu.Unlock()
		return res, nil
	case <-timer.C:
		s.forget(id)
		go s.send(b, protocol.New(protocol.MethodCancel, protocol.Cancel{ID: id}))
		return protocol.Result{}, fmt.Errorf("%s after %s: %w", id, timeout, ErrTimeout)
	case <-ctx.Done():
		s.forget(id)
		go s.send(b, protocol.New(protocol.MethodCancel, protocol.Cancel{ID: id}))
		return protocol.Result{}, fmt.Errorf("%s: %w", id, ctx.Err())
	}
}

// UploadAuthorize is called by the files upload handler before it reads a
// single byte off the request body. It looks up the pending export by id and
// refuses anything the hub did not itself ask for: an unknown id (never
// issued, or the export already timed out and forgot it), an exec id (no
// format), or an id whose pending export belongs to a different user (§13:
// isolation between users — an upload can only ever complete its own user's
// export). On success it returns the format the hub asked the bridge to
// produce, which the caller uses to pick the stored object's extension —
// never whatever the uploader's request claims.
func (s *Service) UploadAuthorize(id, uploaderUser string) (format string, ok bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	p := s.pending[id]
	if p == nil || p.format == "" || p.bridge.UserID != uploaderUser {
		return "", false
	}
	return p.format, true
}

// CompleteExport delivers the outcome of an upload to the Export call
// waiting on id, exactly as a `result` message from the bridge itself would
// (deliver is symmetric: whichever arrives first — the pane's own `result`,
// or this — wins, and the loser is dropped with a log line, which is also
// what happens to an upload that finishes after the export already timed
// out). Re-checks ownership so a caller cannot complete an export it never
// authorized with UploadAuthorize.
func (s *Service) CompleteExport(id, uploaderUser string, res protocol.Result) bool {
	s.mu.Lock()
	p := s.pending[id]
	if p != nil && (p.format == "" || p.bridge.UserID != uploaderUser) {
		p = nil
	}
	if p != nil {
		delete(s.pending, id)
	}
	s.mu.Unlock()
	if p == nil {
		s.log.Info("bridge: upload completed for an unknown, expired or foreign export id", "exec_id", id, "uploader", uploaderUser)
		return false
	}
	p.ch <- res
	return true
}

func (s *Service) forget(id string) {
	s.mu.Lock()
	delete(s.pending, id)
	s.mu.Unlock()
}

// deliver hands a result to its waiting Exec, or drops it with a log line.
func (s *Service) deliver(from *registry.Bridge, res protocol.Result) {
	s.mu.Lock()
	p := s.pending[res.ID]
	if p != nil && p.bridge != from {
		// An id is only ever sent to one bridge, so this is a bridge replying
		// to an exec it was never given. Ignore it rather than let one
		// connection complete another's work.
		p = nil
	}
	if p != nil {
		delete(s.pending, res.ID)
	}
	s.mu.Unlock()
	if p == nil {
		s.log.Info("bridge: result dropped (late or unknown exec)", "instance_id", from.InstanceID, "exec_id", res.ID)
		return
	}
	p.ch <- res
}

// notice attaches an exec-scoped notice to its pending exec, or logs it.
func (s *Service) notice(from *registry.Bridge, n protocol.Notice) {
	s.mu.Lock()
	p := s.pending[n.ExecID]
	if p != nil && p.bridge == from {
		p.notices = append(p.notices, n)
	}
	s.mu.Unlock()
	s.log.Info("bridge: notice", "user", from.UserID, "connector", from.Connector, "instance_id", from.InstanceID,
		"exec_id", n.ExecID, "code", n.Record.Code, "severity", n.Record.Severity, "message", n.Record.Message)
}

// failPending ends every exec waiting on b with err by closing its channel.
func (s *Service) failPending(b *registry.Bridge, err error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for id, p := range s.pending {
		if p.bridge == b {
			delete(s.pending, id)
			close(p.ch)
			s.log.Info("bridge: exec failed with the connection", "instance_id", b.InstanceID, "exec_id", id, "err", err)
		}
	}
}

// link adapts a websocket.Conn to registry.Link.
type link struct {
	conn *websocket.Conn
}

func (l *link) Send(ctx context.Context, msg protocol.Message) error {
	data, err := json.Marshal(msg)
	if err != nil {
		return err
	}
	return l.conn.Write(ctx, websocket.MessageText, data)
}

func (l *link) Close(reason string) {
	_ = l.conn.Close(websocket.StatusNormalClosure, reason)
}
