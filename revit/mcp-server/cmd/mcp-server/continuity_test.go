package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"io"
	"log"
	"net"
	"os"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/broker"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/mcpserver"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/singleton"
)

const (
	initLine     = `{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}` + "\n"
	initedLine   = `{"jsonrpc":"2.0","method":"notifications/initialized"}` + "\n"
	initRespLine = `{"jsonrpc":"2.0","id":0,"result":{"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"s","version":"1"}}}` + "\n"
)

func toolsListLine(id int) string {
	return `{"jsonrpc":"2.0","id":` + strconv.Itoa(id) + `,"method":"tools/list"}` + "\n"
}

// --- capture -------------------------------------------------------------

// TestContinuityCapturesFirstLineWithoutAlteringTheStream pins the capture
// side's core promise: the reader is a transparent tee. Bytes come out exactly
// as they went in, whatever the chunking, and the cache holds exactly the
// first line -- including when that line arrives split across reads and when
// one read carries the tail of it plus the start of the next message.
func TestContinuityCapturesFirstLineWithoutAlteringTheStream(t *testing.T) {
	c := newSessionContinuity(nil)
	input := initLine + initedLine + toolsListLine(1)
	// Chunk boundaries chosen to split the initialize mid-line and to put the
	// end of initialize plus the start of the next message in one chunk.
	chunks := []string{input[:10], input[10 : len(initLine)-3], input[len(initLine)-3 : len(initLine)+7], input[len(initLine)+7:]}
	var feed []io.Reader
	for _, ch := range chunks {
		feed = append(feed, strings.NewReader(ch))
	}
	out, err := io.ReadAll(c.capturing(io.MultiReader(feed...)))
	if err != nil {
		t.Fatal(err)
	}
	if string(out) != input {
		t.Fatalf("stream altered:\n got %q\nwant %q", out, input)
	}
	if string(c.initLine) != initLine {
		t.Fatalf("cached line = %q, want the initialize line", c.initLine)
	}
	if c.initID != float64(0) {
		t.Fatalf("cached id = %#v, want 0", c.initID)
	}
	if !c.watching() {
		t.Fatal("after forwarding the client's initialize, one response should be outstanding")
	}
}

func TestContinuityDoesNotCacheANonInitializeFirstLine(t *testing.T) {
	for name, first := range map[string]string{
		"notification":  initedLine,
		"other request": toolsListLine(1),
		"not json":      "hello\n",
		"no id":         `{"jsonrpc":"2.0","method":"initialize","params":{}}` + "\n",
	} {
		t.Run(name, func(t *testing.T) {
			c := newSessionContinuity(nil)
			if _, err := io.ReadAll(c.capturing(strings.NewReader(first + initLine))); err != nil {
				t.Fatal(err)
			}
			if c.initLine != nil {
				t.Fatalf("cached %q; only a first-line initialize may be cached", c.initLine)
			}
			if c.replayPrefix() != nil {
				t.Fatal("nothing to replay without a cached initialize")
			}
			if c.watching() {
				t.Fatal("nothing outstanding without a cached initialize")
			}
		})
	}
}

func TestContinuityGivesUpOnAnOversizedFirstLine(t *testing.T) {
	c := newSessionContinuity(nil)
	big := strings.Repeat("x", maxInitializeLineBytes+1)
	if _, err := io.ReadAll(c.capturing(strings.NewReader(big))); err != nil {
		t.Fatal(err)
	}
	if !c.captureDone || c.partial != nil || c.initLine != nil {
		t.Fatalf("oversized first line must end capture and retain nothing: done=%v partial=%d cached=%d", c.captureDone, len(c.partial), len(c.initLine))
	}
}

// --- replay prefix -------------------------------------------------------

func TestContinuityReplayPrefixIsInitializePlusInitialized(t *testing.T) {
	c := newSessionContinuity(nil)
	if c.replayPrefix() != nil {
		t.Fatal("first upstream, nothing captured: prefix must be nil")
	}
	if _, err := io.ReadAll(c.capturing(strings.NewReader(initLine))); err != nil {
		t.Fatal(err)
	}
	got := c.replayPrefix()
	if string(got) != initLine+initedLine {
		t.Fatalf("prefix = %q\nwant initialize line followed by notifications/initialized", got)
	}
	if c.replays != 1 {
		t.Fatalf("replays = %d, want 1", c.replays)
	}
}

// --- output dedupe -------------------------------------------------------

// writeChunked drives a writer with p split into n-byte pieces, the way a
// socket read loop hands bytes to io.Copy: no relation to line boundaries.
func writeChunked(t *testing.T, w io.Writer, p string, n int) {
	t.Helper()
	for len(p) > 0 {
		k := n
		if k > len(p) {
			k = len(p)
		}
		if _, err := io.WriteString(w, p[:k]); err != nil {
			t.Fatal(err)
		}
		p = p[k:]
	}
}

func TestContinuityFirstInitializeResponseReachesTheClient(t *testing.T) {
	c := newSessionContinuity(nil)
	_, _ = io.ReadAll(c.capturing(strings.NewReader(initLine)))
	var out bytes.Buffer
	w := c.outputWriter(&out)
	toolsResp := `{"jsonrpc":"2.0","id":1,"result":{"tools":[]}}` + "\n"
	for _, n := range []int{1, 7, 4096} {
		out.Reset()
		c.answered, c.outstanding = false, 1
		writeChunked(t, w, initRespLine+toolsResp, n)
		if out.String() != initRespLine+toolsResp {
			t.Fatalf("chunk=%d: client got %q, want the initialize response and the tools response", n, out.String())
		}
		if c.watching() {
			t.Fatalf("chunk=%d: still watching after the response arrived", n)
		}
	}
}

func TestContinuityDropsTheReplaysDuplicateInitializeResponse(t *testing.T) {
	c := newSessionContinuity(nil)
	_, _ = io.ReadAll(c.capturing(strings.NewReader(initLine)))
	var out bytes.Buffer
	// First upstream answers the client's own initialize.
	writeChunked(t, c.outputWriter(&out), initRespLine, 3)
	// A successor upstream: the prefix is replayed, and it answers too.
	if c.replayPrefix() == nil {
		t.Fatal("expected a replay prefix")
	}
	toolsResp := `{"jsonrpc":"2.0","id":2,"result":{"tools":[]}}` + "\n"
	writeChunked(t, c.outputWriter(&out), initRespLine+toolsResp, 5)
	if out.String() != initRespLine+toolsResp {
		t.Fatalf("client got %q\nwant exactly one initialize response followed by the tools response", out.String())
	}
	if c.watching() {
		t.Fatal("nothing should be outstanding after the duplicate was swallowed")
	}
}

// If the first upstream died before answering, the replay's response IS the
// client's answer and must be forwarded, not swallowed.
func TestContinuityForwardsTheReplayResponseWhenTheClientWasNeverAnswered(t *testing.T) {
	c := newSessionContinuity(nil)
	_, _ = io.ReadAll(c.capturing(strings.NewReader(initLine)))
	var out bytes.Buffer
	_ = c.outputWriter(&out) // first upstream: produced nothing, then died
	c.replayPrefix()
	writeChunked(t, c.outputWriter(&out), initRespLine, 2)
	if out.String() != initRespLine {
		t.Fatalf("client got %q, want the (first and only) initialize response", out.String())
	}
	// And a third upstream's duplicate is now dropped.
	c.replayPrefix()
	writeChunked(t, c.outputWriter(&out), initRespLine, 2)
	if out.String() != initRespLine {
		t.Fatalf("client got %q, want no second initialize response", out.String())
	}
}

// An error response to the replayed initialize is still a response to it (the
// client must not see it), and is matched by id like a result. Lines that
// merely mention the id, or requests/notifications from the server, pass.
func TestContinuityMatchesResponsesByIdOnly(t *testing.T) {
	c := newSessionContinuity(nil)
	_, _ = io.ReadAll(c.capturing(strings.NewReader(initLine)))
	c.answered = true // the client already has its answer
	var out bytes.Buffer
	w := c.outputWriter(&out)
	c.replayPrefix()
	lines := []string{
		`{"jsonrpc":"2.0","method":"notifications/message","params":{"id":0}}` + "\n", // notification, forwarded
		`{"jsonrpc":"2.0","id":"0","result":{}}` + "\n",                               // string "0" is not number 0, forwarded
		`{"jsonrpc":"2.0","id":0,"error":{"code":-32600,"message":"nope"}}` + "\n",    // the duplicate (as an error), dropped
		`{"jsonrpc":"2.0","id":0,"result":{}}` + "\n",                                 // no longer outstanding, forwarded
	}
	writeChunked(t, w, strings.Join(lines, ""), 11)
	want := lines[0] + lines[1] + lines[3]
	if out.String() != want {
		t.Fatalf("client got:\n%s\nwant:\n%s", out.String(), want)
	}
}

// A successor that refuses the replayed initialize is a real condition the
// client can never be told about, so the log must name it as a rejection --
// not file it as a routine dropped duplicate.
func TestContinuityNamesARejectedReplayInTheLog(t *testing.T) {
	var logBuf bytes.Buffer
	c := newSessionContinuity(log.New(&logBuf, "", 0))
	_, _ = io.ReadAll(c.capturing(strings.NewReader(initLine)))
	var out bytes.Buffer
	writeChunked(t, c.outputWriter(&out), initRespLine, 64)
	c.replayPrefix()
	writeChunked(t, c.outputWriter(&out), `{"jsonrpc":"2.0","id":0,"error":{"code":-32602,"message":"unsupported protocol version"}}`+"\n", 64)
	if out.String() != initRespLine {
		t.Fatalf("the rejection must not reach the client; client got %q", out.String())
	}
	if !strings.Contains(logBuf.String(), "REJECTED the replayed initialize: code -32602: unsupported protocol version") {
		t.Fatalf("log does not name the rejection:\n%s", logBuf.String())
	}
	if strings.Contains(logBuf.String(), "dropped the duplicate") {
		t.Fatalf("a rejection must not be logged as a routine duplicate:\n%s", logBuf.String())
	}
}

func TestContinuityStreamsAnOverlongWatchedLine(t *testing.T) {
	c := newSessionContinuity(nil)
	_, _ = io.ReadAll(c.capturing(strings.NewReader(initLine)))
	var out bytes.Buffer
	w := c.outputWriter(&out)
	huge := `{"jsonrpc":"2.0","id":9,"result":"` + strings.Repeat("y", maxWatchedOutputLineBytes) + `"}` + "\n"
	writeChunked(t, w, huge+initRespLine, 1<<16)
	if out.String() != huge+initRespLine {
		t.Fatal("an overlong line must stream through intact, and watching resumes on the next line")
	}
}

// --- against real go-sdk primaries, in process ---------------------------

// realPrimary is an in-process stand-in for a primary broker: the real
// broker.Broker serving the real mcp.Server over TCP, exactly what a secondary
// talks to. kill() ends it the way a dead primary looks from outside -- every
// connection closed.
type realPrimary struct {
	root string
	kill context.CancelFunc
}

func startRealPrimary(t *testing.T, root string) *realPrimary {
	t.Helper()
	server := mcp.NewServer(&mcp.Implementation{Name: "primary-test", Version: "0.0.0"}, nil)
	mcpserver.RegisterSkills(server, "test") // one real tool is enough for tools/list
	token, err := singleton.GenerateToken()
	if err != nil {
		t.Fatal(err)
	}
	b := &broker.Broker{Token: token, MCPServer: server}
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	go b.Serve(ctx, ln)
	fp, err := mcpserver.ToolSchemaFingerprint()
	if err != nil {
		t.Fatal(err)
	}
	addr := ln.Addr().(*net.TCPAddr)
	if err := singleton.WriteBrokerJSON(root, singleton.BrokerInfo{Host: "127.0.0.1", Port: addr.Port, PID: os.Getpid(), StartedAt: time.Now(), Token: token, SchemaFingerprint: fp}); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(cancel)
	return &realPrimary{root: root, kill: cancel}
}

// deafPrimary accepts the auth handshake and then never answers anything --
// the shape of a primary that dies between receiving the client's initialize
// and responding to it.
func startDeafPrimary(t *testing.T, root string) *realPrimary {
	t.Helper()
	token, _ := singleton.GenerateToken()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	go func() {
		conn, err := ln.Accept()
		if err != nil {
			return
		}
		br := bufio.NewReader(conn)
		if _, err := br.ReadBytes('\n'); err == nil {
			_, _ = conn.Write([]byte(`{"jsonrpc":"2.0","id":"auth","result":{"ok":true}}` + "\n"))
		}
		<-ctx.Done()
		conn.Close()
	}()
	fp, _ := mcpserver.ToolSchemaFingerprint()
	addr := ln.Addr().(*net.TCPAddr)
	if err := singleton.WriteBrokerJSON(root, singleton.BrokerInfo{Host: "127.0.0.1", Port: addr.Port, PID: os.Getpid(), StartedAt: time.Now(), Token: token, SchemaFingerprint: fp}); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { cancel(); ln.Close() })
	return &realPrimary{root: root, kill: func() { cancel(); ln.Close() }}
}

// secondaryUnderTest runs runSecondary the way run() does -- one turnReader
// per upstream over one shared relay, one continuity holder -- with a test
// client on the far side of the relay/stdout.
type secondaryUnderTest struct {
	t          *testing.T
	root       string
	relay      *stdinRelay
	continuity *sessionContinuity
	lines      chan string
	outW       *io.PipeWriter
}

func newSecondaryUnderTest(t *testing.T, root string) *secondaryUnderTest {
	t.Helper()
	outR, outW := io.Pipe()
	s := &secondaryUnderTest{
		t:          t,
		root:       root,
		relay:      &stdinRelay{chunks: make(chan []byte)},
		continuity: newSessionContinuity(log.New(io.Discard, "", 0)),
		lines:      make(chan string, 64),
		outW:       outW,
	}
	go func() {
		sc := bufio.NewScanner(outR)
		sc.Buffer(make([]byte, 1<<20), 1<<20)
		for sc.Scan() {
			s.lines <- sc.Text()
		}
	}()
	return s
}

// connect starts one upstream turn; the returned channel yields runSecondary's
// error once the turn ends, after both copy goroutines have exited.
func (s *secondaryUnderTest) connect() <-chan error {
	done := make(chan error, 1)
	go func() {
		stop := make(chan struct{})
		reader := s.continuity.capturing(&turnReader{relay: s.relay, stop: stop})
		var wg sync.WaitGroup
		err := runSecondary(context.Background(), s.root, log.New(io.Discard, "", 0), reader, s.outW, s.continuity, stop, &wg)
		wg.Wait()
		done <- err
	}()
	return done
}

func (s *secondaryUnderTest) send(line string) {
	select {
	case s.relay.chunks <- []byte(line):
	case <-time.After(5 * time.Second):
		s.t.Fatalf("no reader consumed %q", line)
	}
}

func (s *secondaryUnderTest) recv() map[string]any {
	s.t.Helper()
	select {
	case l := <-s.lines:
		var m map[string]any
		if err := json.Unmarshal([]byte(l), &m); err != nil {
			s.t.Fatalf("non-JSON line to client: %q", l)
		}
		return m
	case <-time.After(10 * time.Second):
		s.t.Fatal("timed out waiting for a line to the client")
		return nil
	}
}

func (s *secondaryUnderTest) expectNothing(d time.Duration) {
	s.t.Helper()
	select {
	case l := <-s.lines:
		s.t.Fatalf("unexpected line reached the client: %q", l)
	case <-time.After(d):
	}
}

func assertToolsResult(t *testing.T, m map[string]any, id float64) {
	t.Helper()
	if m["id"] != id {
		t.Fatalf("response id = %v, want %v (message: %v)", m["id"], id, m)
	}
	res, ok := m["result"].(map[string]any)
	if !ok {
		t.Fatalf("expected a tools/list result, got %v", m)
	}
	if tools, ok := res["tools"].([]any); !ok || len(tools) == 0 {
		t.Fatalf("expected a non-empty tools list, got %v", m)
	}
}

// The §5.6 case, against real go-sdk sessions: a client initialized through
// primary 1 keeps working through primary 2 without re-initializing, and
// never sees a second initialize response.
func TestSecondaryReplaysInitializeToASuccessorPrimary(t *testing.T) {
	root := t.TempDir()
	p1 := startRealPrimary(t, root)
	s := newSecondaryUnderTest(t, root)

	turn1 := s.connect()
	s.send(initLine)
	first := s.recv()
	if first["id"] != float64(0) || first["result"] == nil {
		t.Fatalf("initialize via primary 1: %v", first)
	}
	s.send(initedLine)
	s.send(toolsListLine(1))
	assertToolsResult(t, s.recv(), 1)

	p1.kill()
	if err := <-turn1; err == nil {
		t.Log("turn 1 ended cleanly (EOF from upstream)")
	}

	startRealPrimary(t, root)
	turn2 := s.connect()
	// The client knows nothing of the change: its next message is a request.
	s.send(toolsListLine(2))
	assertToolsResult(t, s.recv(), 2)
	s.expectNothing(300 * time.Millisecond)
	if s.continuity.replays != 1 {
		t.Fatalf("replays = %d, want 1", s.continuity.replays)
	}

	// Continue to prove the session is live, not a one-off.
	s.send(toolsListLine(3))
	assertToolsResult(t, s.recv(), 3)
	select {
	case err := <-turn2:
		t.Fatalf("turn 2 ended unexpectedly: %v", err)
	default:
	}
}

// Primary 1 died with the client's initialize unanswered: the replay's answer
// is the one the client gets, exactly once.
func TestSecondaryForwardsTheReplayAnswerWhenTheFirstPrimaryNeverAnswered(t *testing.T) {
	root := t.TempDir()
	deaf := startDeafPrimary(t, root)
	s := newSecondaryUnderTest(t, root)

	turn1 := s.connect()
	s.send(initLine)
	s.expectNothing(200 * time.Millisecond)
	deaf.kill()
	<-turn1

	startRealPrimary(t, root)
	s.connect()
	first := s.recv()
	if first["id"] != float64(0) || first["result"] == nil {
		t.Fatalf("expected the client's initialize answer from primary 2, got %v", first)
	}
	s.send(initedLine)
	s.send(toolsListLine(1))
	assertToolsResult(t, s.recv(), 1)
	s.expectNothing(200 * time.Millisecond)
}
