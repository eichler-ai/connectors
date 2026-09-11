// Package dialer is the server's side of PRD §05's dial-in design: it scans the
// instances directory, dials every live Rhino MCP Bridge it is not already
// connected to, authenticates with the file's token, and keeps the registry in
// step with each connection's register and ping notifications. One server, N
// connections, no election -- a second server process on the same machine does
// exactly the same thing independently.
package dialer

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"strconv"
	"sync"
	"time"

	"github.com/eichler-ai/connectors/internal/servercore/transport"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/instancefile"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/registry"
)

// Bounds (CONVENTIONS.md: every retained record states its bound). The connection
// table holds one link per connected instance and one failure timestamp per pid
// that refused us; both are pruned when the instance file disappears.
const (
	DefaultScanInterval = 2 * time.Second
	dialTimeout         = 3 * time.Second
	authTimeout         = 10 * time.Second
	// registerTimeout bounds the wait for the plug-in's register after auth. The
	// plug-in serves register from a cache and never blocks on Rhino's main
	// thread, so a register that does not arrive promptly is a bridge that is
	// wedged or a different build; the connection is closed and the pid backed
	// off rather than held open forever (review of #281).
	registerTimeout = 15 * time.Second
	// retryBackoff is how long a pid that refused or failed auth is left alone
	// before the next scan tries it again. The instance file is the source of
	// truth for "should be reachable"; this only stops a broken plug-in from
	// being dialled twice a second.
	retryBackoff = 5 * time.Second
)

// Options configure a Manager.
type Options struct {
	InstancesDir  string
	ServerID      string
	ServerVersion string
	Registry      *registry.Registry
	Alive         instancefile.Alive // nil: instancefile.ProcessAlive
	ScanInterval  time.Duration      // 0: DefaultScanInterval
	// RegisterTimeout bounds the wait for register after auth; 0: registerTimeout.
	RegisterTimeout time.Duration
	Logf            func(format string, args ...any)
	// Now is injectable for tests.
	Now func() time.Time
}

type link struct {
	file  *instancefile.File
	conn  *transport.Conn
	epoch uint64
	// attachDone is closed by the attach-hook goroutine once every attach hook
	// has run. The detach-hook goroutine waits on it before running the detach
	// hooks, so a hook consumer always sees this connection's attach strictly
	// before its detach even though the two run on separate goroutines -- without
	// it a very fast connect->disconnect could deliver detach first, leaving a
	// consumer (the semsearch manager) with a stale entry for a dead instance
	// (issue #296). Set under Manager.mu in the first-register block; nil until
	// then, so a connection that dies before registering has no ordering to wait on.
	attachDone chan struct{}
}

// Manager owns the connections. Safe for concurrent use.
type Manager struct {
	opts Options

	mu       sync.Mutex
	links    map[string]*link // by instance_id
	dialing  map[int]bool     // by pid: a dial in flight
	failedAt map[int]time.Time
	// attachHooks are told about every successful register/close, for the
	// search index and execution routing that later phases attach.
	attachHooks []func(instanceID string, conn *transport.Conn, attached bool)
}

func New(opts Options) *Manager {
	if opts.Alive == nil {
		opts.Alive = instancefile.ProcessAlive
	}
	if opts.ScanInterval == 0 {
		opts.ScanInterval = DefaultScanInterval
	}
	if opts.RegisterTimeout == 0 {
		opts.RegisterTimeout = registerTimeout
	}
	if opts.Logf == nil {
		opts.Logf = func(string, ...any) {}
	}
	if opts.Now == nil {
		opts.Now = time.Now
	}
	return &Manager{opts: opts, links: map[string]*link{}, dialing: map[int]bool{}, failedAt: map[int]time.Time{}}
}

// OnAttach registers a hook called with attached=true once an instance has
// registered over a new connection and attached=false when that connection ends.
func (m *Manager) OnAttach(h func(instanceID string, conn *transport.Conn, attached bool)) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.attachHooks = append(m.attachHooks, h)
}

// Run scans until ctx ends. The first scan happens immediately.
func (m *Manager) Run(ctx context.Context) {
	t := time.NewTicker(m.opts.ScanInterval)
	defer t.Stop()
	for {
		m.ScanOnce(ctx)
		select {
		case <-ctx.Done():
			m.closeAll()
			return
		case <-t.C:
		}
	}
}

// ScanOnce runs one directory scan and starts a dial for every live, unconnected instance.
func (m *Manager) ScanOnce(ctx context.Context) {
	res, err := instancefile.Scan(m.opts.InstancesDir, m.opts.Alive)
	if err != nil {
		m.opts.Logf("dialer: scanning %s: %v", m.opts.InstancesDir, err)
		return
	}
	for _, p := range res.Removed {
		m.opts.Logf("dialer: removed stale instance file %s (process exited)", p)
	}
	for _, s := range res.Skipped {
		m.opts.Logf("dialer: skipped %s", s)
	}
	livePIDs := map[int]bool{}
	for _, f := range res.Live {
		livePIDs[f.PID] = true
		m.mu.Lock()
		_, connected := m.links[f.InstanceID]
		inFlight := m.dialing[f.PID]
		recentlyFailed := m.opts.Now().Sub(m.failedAt[f.PID]) < retryBackoff
		if !connected && !inFlight && !recentlyFailed {
			m.dialing[f.PID] = true
			go m.connect(ctx, f)
		}
		m.mu.Unlock()
	}
	// Forget failures for pids whose file is gone, so the tables stay bounded by the directory.
	m.mu.Lock()
	for pid := range m.failedAt {
		if !livePIDs[pid] {
			delete(m.failedAt, pid)
		}
	}
	m.mu.Unlock()
}

type authParams struct {
	Token         string `json:"token"`
	Role          string `json:"role"`
	ServerID      string `json:"server_id"`
	ServerVersion string `json:"server_version"`
}

type registerParams struct {
	InstanceID     string              `json:"instance_id"`
	PID            int                 `json:"pid"`
	RhinoVersion   string              `json:"rhino_version"`
	Platform       string              `json:"platform"`
	BridgeVersion  string              `json:"bridge_version"`
	Documents      []registry.Document `json:"documents"`
	ExecutionState string              `json:"execution_state"`
}

type pingParams struct {
	Memory         *registry.MemorySample `json:"memory"`
	ExecutionState string                 `json:"execution_state"`
}

func (m *Manager) connect(ctx context.Context, f *instancefile.File) {
	defer func() {
		m.mu.Lock()
		delete(m.dialing, f.PID)
		m.mu.Unlock()
	}()

	addr := net.JoinHostPort("127.0.0.1", strconv.Itoa(f.Port))
	d := net.Dialer{Timeout: dialTimeout}
	nc, err := d.DialContext(ctx, "tcp", addr)
	if err != nil {
		m.fail(f, fmt.Errorf("dial %s: %w", addr, err))
		return
	}

	conn := transport.NewConn(nc)
	lk := &link{file: f, conn: conn}
	registered := make(chan struct{}, 1)
	var instanceID string
	conn.SetNotificationHandler(func(method string, params json.RawMessage) {
		switch method {
		case "register":
			var rp registerParams
			if err := json.Unmarshal(params, &rp); err != nil {
				m.opts.Logf("dialer: malformed register from pid %d: %v", f.PID, err)
				return
			}
			if rp.InstanceID != f.InstanceID {
				// The file and the process disagree about who this is; trust neither,
				// and back the pid off so it is not redialled every scan.
				m.fail(f, fmt.Errorf("registered as %s but the instance file says %s", rp.InstanceID, f.InstanceID))
				conn.Close()
				return
			}
			m.mu.Lock()
			first := lk.epoch == 0
			epoch := lk.epoch
			m.mu.Unlock()
			newEpoch := m.opts.Registry.Register(&registry.Instance{
				InstanceID: rp.InstanceID, PID: rp.PID, RhinoVersion: rp.RhinoVersion, Platform: rp.Platform,
				BridgeVersion: rp.BridgeVersion, Documents: rp.Documents, ExecutionState: rp.ExecutionState,
			}, epoch, m.opts.Now())
			if newEpoch == 0 {
				// The registry refused a stale epoch: another connection owns this
				// instance now. This link is the displaced one; end it.
				m.opts.Logf("dialer: instance %s re-registered from a connection that no longer owns it; closing", rp.InstanceID)
				conn.Close()
				return
			}
			m.mu.Lock()
			lk.epoch = newEpoch
			instanceID = rp.InstanceID
			if first {
				if old, ok := m.links[instanceID]; ok && old != lk {
					// A redial displaced a link whose Serve has not returned yet
					// (half-open socket). Close it; its own teardown is epoch-guarded.
					m.opts.Logf("dialer: instance %s re-registered on a new connection; closing the displaced one", instanceID)
					old.conn.Close()
				}
				m.links[instanceID] = lk
				lk.attachDone = make(chan struct{})
			}
			hooks := append([]func(string, *transport.Conn, bool){}, m.attachHooks...)
			attachDone := lk.attachDone
			attachID := instanceID // snapshot under the lock; the goroutine must not read the mutable closure var
			m.mu.Unlock()
			if first {
				m.opts.Logf("dialer: connected to Rhino %s pid %d (%s, %s) with %d document(s)", rp.RhinoVersion, rp.PID, rp.Platform, rp.BridgeVersion, len(rp.Documents))
				select {
				case registered <- struct{}{}:
				default:
				}
				// Hooks run off the read loop: one that calls back into the
				// connection (conn.Call) would deadlock inline, and a slow one
				// (an index build) would stall this connection's pings (review of #281).
				// Closing attachDone last is what lets the detach goroutine order
				// itself strictly after this one (issue #296).
				go func() {
					defer close(attachDone)
					for _, h := range hooks {
						h(attachID, conn, true)
					}
				}()
			}
		case "ping":
			m.mu.Lock()
			epoch := lk.epoch
			id := instanceID
			m.mu.Unlock()
			if epoch == 0 {
				return
			}
			var pp pingParams
			if len(params) > 0 {
				if err := json.Unmarshal(params, &pp); err != nil {
					m.opts.Logf("dialer: malformed ping params from %s (liveness still recorded): %v", id, err)
				}
			}
			m.opts.Registry.RecordPingState(id, epoch, m.opts.Now(), pp.Memory, pp.ExecutionState)
		}
	})

	serveErr := make(chan error, 1)
	go func() { serveErr <- conn.Serve() }()

	actx, cancel := context.WithTimeout(ctx, authTimeout)
	_, rpcErr, err := conn.Call(actx, "auth", authParams{Token: f.Token, Role: "agent-client", ServerID: m.opts.ServerID, ServerVersion: m.opts.ServerVersion})
	cancel()
	if err != nil || rpcErr != nil {
		conn.Close()
		if rpcErr != nil {
			m.fail(f, fmt.Errorf("auth refused: %s", rpcErr.Message))
		} else {
			m.fail(f, fmt.Errorf("auth: %w", err))
		}
		return
	}

	// Register must follow promptly (the plug-in sends it right after the auth
	// answer, from a cache). Wait for it, bounded; a connection that never
	// registers is closed and backed off, never held open (review of #281).
	serveEnded := false
	select {
	case <-registered:
	case err = <-serveErr:
		// The connection ended. But the register notification runs INLINE on the
		// read loop, so a drop RIGHT AFTER a successful register makes both this and
		// `registered` ready at once, and select picks at random -- landing here
		// ~half the time even though register succeeded. Re-check registered without
		// blocking: if it fired, this is a normal post-register end, so fall through
		// to teardown (below) rather than the "before register" path, which returns
		// WITHOUT teardown and would orphan m.links[id] forever (never redialled) and
		// leak the attach hook's index (issue #296, register-race path).
		select {
		case <-registered:
			serveEnded = true // register did happen; err holds the serve error already
		default:
			m.fail(f, fmt.Errorf("connection ended before register: %w", err))
			return
		}
	case <-time.After(m.opts.RegisterTimeout):
		conn.Close()
		<-serveErr
		m.fail(f, fmt.Errorf("no register within %s after auth", m.opts.RegisterTimeout))
		return
	case <-ctx.Done():
		conn.Close()
		<-serveErr
		return
	}

	// Serve until the connection ends, then tear down under the epoch guard. When
	// the register-race branch above already received the serve error, don't wait
	// on serveErr again (it is a one-shot buffered channel and would block forever).
	if !serveEnded {
		err = <-serveErr
	}
	m.mu.Lock()
	epoch := lk.epoch
	id := instanceID
	if id != "" && m.links[id] == lk {
		delete(m.links, id)
	}
	hooks := append([]func(string, *transport.Conn, bool){}, m.attachHooks...)
	attachDone := lk.attachDone
	m.mu.Unlock()
	if id != "" {
		removed := m.opts.Registry.RemoveIfEpoch(id, epoch)
		if !errors.Is(err, net.ErrClosed) {
			m.opts.Logf("dialer: connection to %s (pid %d) ended: %v (registry entry removed: %v)", id, f.PID, err, removed)
		}
		go func() {
			// Order detach strictly after attach for this connection: a fast
			// connect->disconnect spawns both goroutines, and without this wait the
			// detach could run first, leaving the hook consumer a stale entry (#296).
			// id != "" implies the first-register block ran, so attachDone is set.
			// The wait is unbounded ON PURPOSE -- a timeout would run detach out of
			// order and defeat the fix -- and relies on the same contract the attach
			// hooks already carry: they must not block (see the attach comment). It
			// cannot wedge on a panicking hook, since attachDone is closed via defer.
			if attachDone != nil {
				<-attachDone
			}
			for _, h := range hooks {
				h(id, conn, false)
			}
		}()
	} else {
		m.opts.Logf("dialer: connection to pid %d ended before it registered: %v", f.PID, err)
	}
}

func (m *Manager) fail(f *instancefile.File, err error) {
	m.mu.Lock()
	m.failedAt[f.PID] = m.opts.Now()
	m.mu.Unlock()
	m.opts.Logf("dialer: pid %d (%s): %v; retrying in %s", f.PID, f.Path, err, retryBackoff)
}

// Conn returns the live connection for an instance, for callers that route requests.
func (m *Manager) Conn(instanceID string) (*transport.Conn, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	lk, ok := m.links[instanceID]
	if !ok {
		return nil, false
	}
	return lk.conn, true
}

// CloseInstance closes the connection owning epoch, if it is still current
// (the registry's prune sweep calls this so a silent socket does not linger).
func (m *Manager) CloseInstance(instanceID string, epoch uint64) {
	m.mu.Lock()
	lk, ok := m.links[instanceID]
	current := ok && lk.epoch == epoch
	m.mu.Unlock()
	if current {
		lk.conn.Close()
	}
}

// Connected reports the instance ids with a live, registered connection.
func (m *Manager) Connected() []string {
	m.mu.Lock()
	defer m.mu.Unlock()
	out := make([]string, 0, len(m.links))
	for id := range m.links {
		out = append(out, id)
	}
	return out
}

func (m *Manager) closeAll() {
	m.mu.Lock()
	links := make([]*link, 0, len(m.links))
	for _, lk := range m.links {
		links = append(links, lk)
	}
	m.mu.Unlock()
	for _, lk := range links {
		lk.conn.Close()
	}
}
