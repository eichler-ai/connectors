// Command mcp-server is the Revit MCP Server (the broker), per PRD §04/§05:
// a single process that speaks MCP over stdio to Claude/agents, and TCP/
// NDJSON to one or more Revit MCP Bridge (add-in) instances.
//
// Because the broker is stdio-spawned per MCP client (PRD §05 "Broker
// singleton & port contention"), every invocation first races for an
// exclusive lock file. The winner becomes primary: it binds the TCP port,
// mints a fresh auth token, writes broker.json, and runs the real MCP
// server. Everyone else becomes secondary: it reads the primary's
// broker.json and transparently pipes its own stdio MCP traffic through a
// TCP connection to the primary instead.
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/broker"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/discovery"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/execution"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/mcpserver"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/singleton"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/updatecheck"
)

const serverName = "revit-mcp-server"

// version is overridable at build time via -ldflags.
var version = "dev"

// stdinRelay owns the single physical read of os.Stdin for the life of the
// process. Reading os.Stdin can't be portably cancelled once a call is
// blocked waiting for the next byte, so instead of letting more than one
// piece of code ever call os.Stdin.Read() directly — which is exactly how a
// promotion from secondary to primary could race an orphaned copy-goroutine
// against the newly-primary's own stdio transport for the same descriptor,
// silently dropping whichever message the orphan wins — exactly one
// goroutine ever does that raw read; every consumer instead reads from a
// channel, which (unlike a blocking syscall) is select-able and so
// genuinely can be walked away from.
type stdinRelay struct {
	chunks chan []byte

	// closed flips true just before chunks is closed — i.e. once the
	// physical stdin has hit EOF or a read error and no new input can ever
	// arrive. Read via exhausted() below.
	closed atomic.Bool

	// pending carries forward data a departing turnReader couldn't finish
	// consuming — its own buffered leftover, or a chunk it happened to
	// read from chunks right as it was told to stop — so the NEXT
	// turnReader constructed over this same relay (by run()'s
	// role-transition loop) picks up exactly where the previous one left
	// off. Without this, that data would either be routed into the
	// departing role's already-dying connection or silently dropped —
	// exactly the promotion-handoff bug this relay exists to prevent.
	mu      sync.Mutex
	pending []byte
}

func newStdinRelay() *stdinRelay {
	r := &stdinRelay{chunks: make(chan []byte)}
	go func() {
		buf := make([]byte, 32*1024)
		for {
			n, err := os.Stdin.Read(buf)
			if n > 0 {
				chunk := make([]byte, n)
				copy(chunk, buf[:n])
				r.chunks <- chunk
			}
			if err != nil {
				r.closed.Store(true)
				close(r.chunks)
				return
			}
		}
	}()
	return r
}

// exhausted reports whether the physical stdin has reached EOF (or a read
// error) AND no donated data remains for a next reader — i.e. no possible
// future input exists for any role this process could take. run()'s
// re-election loop checks this after a secondary's turn ends: an MCP
// client closing stdin is the documented stdio shutdown signal, and
// without this check a secondary whose upstream also dropped would re-run
// the election forever — every turnReader.Read returning EOF instantly,
// re-dialing and re-authing against the primary about twice a second, a
// leaked process for any host that closes stdin without also killing the
// subprocess (v1 integrated review).
func (r *stdinRelay) exhausted() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.closed.Load() && len(r.pending) == 0
}

// donate pushes data a departing turnReader couldn't finish consuming back
// onto the relay for the next turnReader to pick up. Safe to call
// concurrently, though in practice at most one turnReader is ever actively
// consuming from a given relay at a time (run() waits for a departing
// secondary's copy goroutines to fully exit before building the next
// turnReader — see run()), so there's never more than one donor in flight
// and nothing to reorder against.
func (r *stdinRelay) donate(b []byte) {
	if len(b) == 0 {
		return
	}
	r.mu.Lock()
	r.pending = append(r.pending, b...)
	r.mu.Unlock()
}

// takePending drains and returns whatever a previous turnReader over this
// relay donated back, if anything.
func (r *stdinRelay) takePending() []byte {
	r.mu.Lock()
	p := r.pending
	r.pending = nil
	r.mu.Unlock()
	return p
}

// turnReader adapts one "turn" of consuming the shared stdinRelay — one
// role (primary or secondary), for as long as it's active — into a plain
// io.Reader. Read returns io.EOF, a clean/expected stop rather than an
// error, the instant stop is closed, even if the relay itself still has
// more data queued for whichever role reads next. This is what lets a
// role's own copy-loop actually exit promptly on a role change, instead of
// staying orphaned in a blocking read the way a direct os.Stdin.Read()
// would.
type turnReader struct {
	relay    *stdinRelay
	stop     <-chan struct{}
	leftover []byte
}

func (r *turnReader) Read(p []byte) (int, error) {
	// Checked first, and non-blocking: once this role has been told to
	// stop, it must never hand data — buffered leftover included — to its
	// caller. A stopped reader's leftover is instead donated back to the
	// relay so the NEXT turnReader constructed over the same relay picks
	// it up, rather than it either being handed to a caller that's about
	// to be torn down or silently dropped if nothing calls Read again.
	select {
	case <-r.stop:
		r.donate()
		return 0, io.EOF
	default:
	}

	if len(r.leftover) == 0 {
		// Pick up anything the previous turnReader over this relay
		// donated back on being stopped, before pulling anything new.
		r.leftover = r.relay.takePending()
	}
	if len(r.leftover) > 0 {
		n := copy(p, r.leftover)
		r.leftover = r.leftover[n:]
		return n, nil
	}

	select {
	case <-r.stop:
		return 0, io.EOF
	case chunk, ok := <-r.relay.chunks:
		if !ok {
			return 0, io.EOF // physical stdin itself closed/EOF'd
		}
		return r.deliverOrDonate(p, chunk)
	}
}

// deliverOrDonate handles a chunk this reader just won from relay.chunks in
// Read's blocking select. Split out from Read (rather than inlined) so the
// specific behavior it implements — the case a stopped reader can still win
// that race, since select has no priority between its two cases and Go
// picks pseudo-randomly among whichever are ready — can be unit tested
// directly and deterministically (see TestTurnReaderDeliverOrDonate*),
// without depending on actually winning that race via real goroutine
// scheduling, which turned out to be far too sensitive to host CPU
// contention to make a reliable end-to-end regression test (see the
// now-informational-only rate check in TestTurnReaderStopDoesNotStealFromNextReader
// and its comment for the full story of why that approach didn't hold up).
func (r *turnReader) deliverOrDonate(p, chunk []byte) (int, error) {
	// select has no priority between its cases, so it's entirely possible to
	// win the chunk race even though stop was also (or is concurrently
	// being) closed. Re-check non-blocking: if stop is now closed, this
	// role is being torn down and this chunk was never meant for it; donate
	// it back rather than ever handing it to this role's caller, so it's
	// the newly-promoted role's turnReader that gets it instead.
	select {
	case <-r.stop:
		r.relay.donate(chunk)
		return 0, io.EOF
	default:
	}
	n := copy(p, chunk)
	if n < len(chunk) {
		r.leftover = chunk[n:]
	}
	return n, nil
}

// donate pushes back any buffered leftover this reader hasn't yet handed to
// its caller, on being told to stop.
func (r *turnReader) donate() {
	if len(r.leftover) > 0 {
		r.relay.donate(r.leftover)
		r.leftover = nil
	}
}

// nopCloseWriter adapts an io.Writer (os.Stdout) into an io.WriteCloser
// with a no-op Close — what mcp.StdioTransport does internally, but
// unexported there. Needed here because runPrimary uses mcp.IOTransport
// directly instead of StdioTransport, so it can share the stdin relay with
// runSecondary rather than reading os.Stdin on its own.
type nopCloseWriter struct{ io.Writer }

func (nopCloseWriter) Close() error { return nil }

func main() {
	mode := flag.String("mode", envOr("REVIT_MCP_MODE", "local"), "connection topology: \"local\" (127.0.0.1 only, default) or \"remote\" (bind a configured non-loopback interface) — PRD §05")
	bindAddr := flag.String("bind", envOr("REVIT_MCP_BIND", ""), "non-loopback bind address, required when -mode=remote (e.g. the Parallels shared-network host adapter address)")
	port := flag.Int("port", envIntOr("REVIT_MCP_PORT", 0), "TCP port for the add-in-facing listener; 0 picks an ephemeral port (discovered via broker.json)")
	appDataDir := flag.String("app-data-dir", os.Getenv("REVIT_MCP_APPDATA"), "override the platform app-data directory (mainly for tests/dev); defaults to the PRD §09 convention")
	flag.Parse()

	logger := log.New(os.Stderr, "["+serverName+"] ", log.LstdFlags|log.Lmsgprefix)

	if err := run(*mode, *bindAddr, *port, *appDataDir, logger); err != nil {
		logger.Fatalf("fatal: %v", err)
	}
}

func envOr(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func envIntOr(key string, def int) int {
	if v := os.Getenv(key); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			return n
		}
	}
	return def
}

func run(mode, bindAddr string, port int, appDataDirOverride string, logger *log.Logger) error {
	if mode != "local" && mode != "remote" {
		return fmt.Errorf("invalid -mode %q: must be \"local\" or \"remote\"", mode)
	}
	if mode == "remote" {
		if bindAddr == "" {
			return fmt.Errorf("-bind is required in remote mode (PRD §05: a specific configured non-loopback interface, never 0.0.0.0)")
		}
		// Parsed, not string-compared: 0.0.0.0, ::, and ::0 are all the same
		// "every interface" address, and a bare string check misses forms
		// like ::0 that IsUnspecified() catches correctly regardless of how
		// the address was spelled. -bind must itself be a literal IP
		// address per PRD §05/§10 ("a specific configured non-loopback
		// address") — a value net.ParseIP can't parse at all (a hostname,
		// or a malformed literal like "0" or "0x0.0x0.0x0.0x0") is never
		// valid here, regardless of what net.Listen might later resolve it
		// to; rejecting only the parseable-and-unspecified case would let
		// exactly those bypass validation and still end up binding every
		// interface.
		ip := net.ParseIP(bindAddr)
		if ip == nil {
			return fmt.Errorf("-bind %q is not a valid IP address literal (PRD §05/§10: -bind must be a specific configured non-loopback IP address, not a hostname or malformed literal)", bindAddr)
		}
		if ip.IsUnspecified() {
			return fmt.Errorf("-bind %q is not allowed (PRD §05/§10: never bind every interface — pass the specific configured non-loopback address instead)", bindAddr)
		}
	}
	if mode == "local" {
		bindAddr = "127.0.0.1"
	}

	dataDir := appDataDirOverride
	if dataDir == "" {
		if mode == "remote" {
			// PRD §05: in remote mode, broker.json must be written to the
			// shared drive's agreed root (the same location §09's
			// file-exchange mechanism uses), NOT the local platform
			// app-data directory singleton.AppDataDir() resolves — that
			// directory is local-mode-only and the remote add-in side
			// never looks there. Silently falling back to it here would
			// mean the add-in's remote-mode discovery just never finds
			// broker.json, with nothing anywhere explaining why. Fail
			// fast instead: require the operator to pass the shared-drive
			// root explicitly via -app-data-dir.
			return fmt.Errorf("-app-data-dir is required in remote mode (PRD §05: broker.json must be written to the shared drive's agreed root, not the local app-data directory)")
		}
		d, err := singleton.AppDataDir()
		if err != nil {
			return fmt.Errorf("resolving app-data directory: %w", err)
		}
		dataDir = d
	}
	if err := os.MkdirAll(dataDir, 0o755); err != nil {
		return fmt.Errorf("creating app-data directory %q: %w", dataDir, err)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	lockPath := filepath.Join(dataDir, "broker.lock")

	// One stdin relay for the whole process lifetime (see its own doc
	// comment) — both branches below read through it via a fresh
	// turnReader per role-attempt, never os.Stdin directly.
	relay := newStdinRelay()

	// PRD §05 "Broker singleton & port contention": whichever process wins
	// the lock is primary; everyone else proxies as secondary. If a
	// secondary's upstream connection to the primary drops for any reason
	// other than this process's own shutdown, "a secondary that notices its
	// upstream connection drop re-runs the same lock-acquisition step — it
	// may become the new primary, or find another secondary got there
	// first and keep proxying." This loop is what makes that re-election
	// actually happen, instead of the secondary process just exiting
	// (killing whatever MCP client session it was proxying for) the moment
	// the primary it was following goes away.
	for {
		lock, primary, err := singleton.AcquireLock(lockPath)
		if err != nil {
			return fmt.Errorf("acquiring singleton lock: %w", err)
		}

		stop := make(chan struct{})
		reader := &turnReader{relay: relay, stop: stop}

		if primary {
			err := runPrimary(ctx, bindAddr, port, dataDir, logger, reader)
			close(stop)
			lock.Release()
			return err // this process's own session ending is a real shutdown, not something to retry
		}

		// runSecondary closes stop itself, as the very first thing it does
		// on returning — before it closes its upstream connection to the
		// primary — so its own upload goroutine (still blocked in
		// turnReader.Read) gets the earliest possible chance to notice and
		// bail before that connection dies underneath it. wg tracks both
		// of runSecondary's copy goroutines: wait for them to actually
		// exit, not just for stop to be closed, before looping around to
		// build a fresh turnReader over the shared relay — otherwise the
		// still-running goroutine would be a second live consumer racing
		// the next role's reader for the same relay.chunks, which is
		// exactly the "misrouted into an already-closed connection" bug
		// this whole mechanism exists to prevent.
		var wg sync.WaitGroup
		err = runSecondary(ctx, dataDir, logger, reader, stop, &wg)
		wg.Wait()
		if ctx.Err() != nil {
			return nil // clean shutdown (signal), not a dropped-primary retry case
		}
		if relay.exhausted() {
			// The MCP client closed stdin — its normal stdio shutdown
			// signal, after which it is not waiting on further responses —
			// and nothing remains for a successor role to consume. Exit
			// instead of re-running the election: there is no session left
			// to proxy, and retrying anyway busy-loops dialing the primary
			// forever (see stdinRelay.exhausted).
			logger.Printf("secondary: stdin closed and drained; exiting")
			return nil
		}
		logger.Printf("secondary: upstream connection to primary ended (%v); re-attempting lock acquisition", err)
		time.Sleep(500 * time.Millisecond) // bound the retry rate if the lock is held by something that never releases it
	}
}

func runPrimary(ctx context.Context, bindAddr string, port int, dataDir string, logger *log.Logger, stdin io.Reader) error {
	ln, err := net.Listen("tcp", net.JoinHostPort(bindAddr, strconv.Itoa(port)))
	if err != nil {
		return fmt.Errorf("binding TCP listener on %s:%d: %w", bindAddr, port, err)
	}
	defer ln.Close()

	tcpAddr, ok := ln.Addr().(*net.TCPAddr)
	if !ok {
		return fmt.Errorf("unexpected listener address type %T", ln.Addr())
	}

	token, err := singleton.GenerateToken()
	if err != nil {
		return fmt.Errorf("generating auth token: %w", err)
	}

	info := singleton.BrokerInfo{
		Host:      bindAddr,
		Port:      tcpAddr.Port,
		PID:       os.Getpid(),
		StartedAt: time.Now().UTC(),
		Token:     token,
		Version:   version,
	}
	if err := singleton.WriteBrokerJSON(dataDir, info); err != nil {
		return fmt.Errorf("writing broker.json: %w", err)
	}
	logger.Printf("primary: listening on %s:%d, broker.json written to %s", bindAddr, tcpAddr.Port, dataDir)

	mcpServer := mcp.NewServer(&mcp.Implementation{Name: serverName, Version: version}, nil)
	execMgr := execution.NewManager()
	mcpserver.Register(mcpServer, execMgr)
	reg := registry.New()
	discoveryRouter := discovery.NewRouter(reg)
	mcpserver.RegisterDiscovery(mcpServer, discoveryRouter)
	mcpserver.RegisterInstances(mcpServer, reg, execMgr)
	// No dependencies and no Revit needed: get_skills answers even with nothing connected.
	mcpserver.RegisterSkills(mcpServer)

	b := &broker.Broker{
		Token:     token,
		Registry:  reg,
		Execution: execMgr,
		Discovery: discoveryRouter,
		MCPServer: mcpServer,
		Logger:    logger,
	}

	serveErr := make(chan error, 1)
	go func() { serveErr <- b.Serve(ctx, ln) }()

	// Heartbeat prune sweep (PRD §05) — reclaims instances that have gone
	// silent (wedged, or disconnected and never reconnected) from the
	// registry so list_instances doesn't accumulate stale entries forever.
	go func() {
		ticker := time.NewTicker(30 * time.Second)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				if pruned := reg.PruneStale(time.Now()); len(pruned) > 0 {
					logger.Printf("registry: pruned %d stale instance(s): %v", len(pruned), pruned)
					for _, id := range pruned {
						// Pruning removed only the registry entry; the
						// socket may well still be open (a quiet, wedged,
						// or suspended add-in). Close it too, or the
						// instance lingers executable-but-invisible —
						// absent from list_instances forever (resumed
						// pings no-op for unregistered ids) while
						// execute_script still routes to it. Closing runs
						// recovery through the normal teardown-then-
						// reconnect-then-re-register path instead.
						execMgr.CloseInstanceConn(id)
					}
				}
			}
		}
	}()

	// Broker's own GitHub latest-release check (PRD §12): the broker, not
	// the add-in, makes this outbound call — the add-in only ever reads the
	// result back out of broker.json on its existing reconnect-loop poll.
	// Injected client carries a real timeout; a zero-value http.Client has
	// none, and a hung TCP connection to a broken proxy must never be able
	// to block this goroutine (let alone startup) forever.
	go updatecheck.Run(ctx, &http.Client{Timeout: 10 * time.Second}, dataDir, version, logger)

	// The primary's own MCP session runs over its own stdio, exactly like
	// every secondary's proxied session runs over TCP (PRD §05: "From the
	// agent's point of view behavior is identical regardless of which
	// broker process it happens to be talking to"). IOTransport, not
	// StdioTransport, deliberately — it reads through the shared stdin
	// relay (via stdin) rather than os.Stdin directly, so a promotion from
	// secondary never races two independent physical stdin readers.
	stdioTransport := &mcp.IOTransport{Reader: io.NopCloser(stdin), Writer: nopCloseWriter{os.Stdout}}
	err = mcpServer.Run(ctx, stdioTransport)
	stop := ctx.Err() != nil
	if err != nil && !stop {
		return fmt.Errorf("stdio MCP session ended: %w", err)
	}
	return nil
}

func runSecondary(ctx context.Context, dataDir string, logger *log.Logger, stdin io.Reader, stop chan struct{}, wg *sync.WaitGroup) error {
	var info singleton.BrokerInfo
	var err error
	// The primary listens before anything else (PRD §05), but there's still
	// a brief window between winning the lock and finishing the
	// broker.json write; retry briefly rather than failing outright.
	deadline := time.Now().Add(5 * time.Second)
	for {
		info, err = singleton.ReadBrokerJSON(dataDir)
		if err == nil {
			break
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("reading broker.json from %q after waiting for the primary: %w", dataDir, err)
		}
		time.Sleep(100 * time.Millisecond)
	}

	conn, err := net.Dial("tcp", net.JoinHostPort(info.Host, strconv.Itoa(info.Port)))
	if err != nil {
		return fmt.Errorf("dialing primary broker at %s:%d: %w", info.Host, info.Port, err)
	}
	defer conn.Close()
	logger.Printf("secondary: proxying stdio through primary at %s:%d", info.Host, info.Port)

	authReq, err := transport.NewRequest(json.RawMessage(`"auth"`), "auth", map[string]any{
		"token": info.Token,
		"role":  string(broker.RoleAgentClient),
	})
	if err != nil {
		return fmt.Errorf("building auth request: %w", err)
	}
	b, err := json.Marshal(authReq)
	if err != nil {
		return fmt.Errorf("encoding auth request: %w", err)
	}
	if _, err := conn.Write(append(b, '\n')); err != nil {
		return fmt.Errorf("sending auth request to primary: %w", err)
	}

	br := bufio.NewReader(conn)
	// Bounded, matching the broker's own pre-auth read deadline (broker.go)
	// — our own just-dialed primary should answer almost immediately, and a
	// hung/misbehaving one shouldn't be able to block this secondary
	// forever waiting for an auth response that never comes.
	_ = conn.SetReadDeadline(time.Now().Add(10 * time.Second))
	line, err := br.ReadBytes('\n')
	if err != nil {
		return fmt.Errorf("reading auth response from primary: %w", err)
	}
	_ = conn.SetReadDeadline(time.Time{})
	var resp transport.Message
	if err := json.Unmarshal(line, &resp); err != nil {
		return fmt.Errorf("decoding auth response from primary: %w", err)
	}
	if resp.Error != nil {
		return fmt.Errorf("primary rejected this secondary's auth: %s", resp.Error.Message)
	}

	// From here, transparently pipe stdin -> conn and conn -> stdout. The
	// MCP stdio protocol is itself NDJSON, matching the wire framing we
	// just used for auth (PRD §05 "Framing"), so no re-encoding is needed —
	// this process is a pure byte-level proxy of its own stdio traffic.
	// Either direction closing ends the proxy: the MCP client closes stdin
	// as its normal stdio-subprocess shutdown signal (it isn't waiting on
	// further responses once it does), and the primary closing its end
	// means there's nothing left to proxy either way.
	errCh := make(chan error, 2)
	wg.Add(2)
	go func() {
		defer wg.Done()
		_, err := io.Copy(conn, stdin)
		errCh <- err
	}()
	go func() {
		defer wg.Done()
		_, err := io.Copy(os.Stdout, br)
		errCh <- err
	}()

	select {
	case <-ctx.Done():
		close(stop)
		return nil
	case err := <-errCh:
		close(stop)
		return err
	}
}
