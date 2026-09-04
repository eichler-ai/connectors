package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"reflect"
	"sync"
)

// sessionContinuity keeps one MCP client's session alive across a change of
// upstream (revit/docs/self-update-architecture.md §5.6, Part B step 1).
//
// A secondary is a byte-level proxy of its own stdio to the primary, and the
// primary's MCP server is what answered the client's `initialize`. When that
// primary is replaced -- the hosted primary restarts after an update, or it
// dies and this process reconnects to the winner of the re-election, or
// promotes itself -- the successor starts a FRESH mcp.Server session that
// rejects every request with `method "..." is invalid during session
// initialization` until it too has seen an `initialize`. The client never
// learns its upstream changed, so without help it is wedged (measured; see
// §5.6). The fix has three parts, all on this one per-process holder so they
// survive across run()'s role turns:
//
//  1. capture: the very first NDJSON line the client sends is its
//     `initialize` request. capturing() tees it out of the stdin stream as it
//     passes through -- bytes are never consumed separately from the proxy, so
//     framing of everything behind it is untouched.
//  2. replay: replayPrefix() is what a successor upstream must be fed before
//     any live client bytes -- the cached `initialize` plus a synthesized
//     `notifications/initialized`, which is how a client completes the
//     handshake and the state the go-sdk server expects to reach.
//  3. dedupe: the client must receive exactly one `initialize` response for
//     the one request it sent. outputWriter() watches responses for the
//     cached request's id: the first one through is the client's answer
//     (normally on the first upstream, but on a successor if the first
//     upstream died before answering); every later one is a replay's duplicate
//     and is dropped.
//
// Not a general re-synchronisation: a request that was in flight on the old
// upstream when it died is lost with it, exactly as before this change.
type sessionContinuity struct {
	mu sync.Mutex

	// partial accumulates the first client line until its '\n' arrives (it
	// can span reads, and in principle role turns). Bounded by
	// maxInitializeLineBytes: past that the line is not an initialize any
	// sane client sent, and capture gives up rather than retain it.
	partial []byte
	// captureDone is set once the first line has been fully seen -- whether or
	// not it turned out to be a usable initialize request. After it, capturing
	// readers are pure passthrough.
	captureDone bool

	// initLine is the cached `initialize` request, newline-terminated; nil
	// when the first line was not a usable initialize (or has not arrived).
	initLine []byte
	// initID is that request's JSON-RPC id, decoded, for matching responses.
	initID any

	// answered records that the client has received an initialize response.
	answered bool
	// answerPending is set while the CURRENT upstream owes an initialize
	// response: after the client's own request was forwarded, or after a
	// replay. Only while it is set does the output path parse lines at all --
	// the rest of the time it is the same streaming byte copy it always was.
	// Reset per upstream, since a dead upstream will never answer.
	answerPending bool

	// replays counts successor upstreams fed the prefix; for logging and tests.
	replays int

	logger *log.Logger
}

// maxInitializeLineBytes bounds the partial first-line buffer. A real
// initialize request is well under 4 KiB; 1 MiB leaves room for any client
// while keeping the bound explicit (CONVENTIONS.md: every retained buffer has
// a stated bound).
const maxInitializeLineBytes = 1 << 20

// maxWatchedOutputLineBytes bounds how much of a single upstream output line
// outputWriter buffers while watching for an initialize response. The
// response itself is a few KiB; a line past this bound is some other result,
// so the writer stops watching and streams it through rather than hold it.
const maxWatchedOutputLineBytes = 8 << 20

// initializedNotification is the handshake-completing notification a client
// sends after receiving its initialize result; replayed with the cached
// request so a successor session reaches the same state the original did.
var initializedNotification = []byte(`{"jsonrpc":"2.0","method":"notifications/initialized"}` + "\n")

func newSessionContinuity(logger *log.Logger) *sessionContinuity {
	return &sessionContinuity{logger: logger}
}

func (s *sessionContinuity) logf(format string, args ...any) {
	if s.logger != nil {
		s.logger.Printf(format, args...)
	}
}

// capturing wraps the client-side stdin reader for one role turn. It changes
// no bytes and consumes nothing on its own: every byte read through it is
// returned to the caller exactly as the underlying reader produced it.
func (s *sessionContinuity) capturing(r io.Reader) io.Reader {
	return &capturingReader{s: s, r: r}
}

type capturingReader struct {
	s *sessionContinuity
	r io.Reader
}

func (c *capturingReader) Read(p []byte) (int, error) {
	n, err := c.r.Read(p)
	if n > 0 {
		c.s.observe(p[:n])
	}
	return n, err
}

// observe feeds client->upstream bytes to the first-line capture. Cheap once
// captureDone is set (one atomic-free bool check under the mutex).
func (s *sessionContinuity) observe(b []byte) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.captureDone {
		return
	}
	i := bytes.IndexByte(b, '\n')
	if i < 0 {
		if len(s.partial)+len(b) > maxInitializeLineBytes {
			s.captureDone = true
			s.partial = nil
			s.logf("session-continuity: the client's first message exceeds %d bytes without a newline; not an initialize request, so the session will not survive a primary restart", maxInitializeLineBytes)
			return
		}
		s.partial = append(s.partial, b...)
		return
	}
	line := append(s.partial, b[:i+1]...)
	s.partial = nil
	s.captureDone = true

	id, err := initializeRequestID(line)
	if err != nil {
		s.logf("session-continuity: the client's first message is not an initialize request (%v); the session will not survive a primary restart", err)
		return
	}
	s.initLine = line
	s.initID = id
	// The client's own initialize is now on its way to the current upstream,
	// which owes exactly one response for it.
	s.answerPending = true
}

// initializeRequestID validates that line is a JSON-RPC `initialize` request
// and returns its decoded id.
func initializeRequestID(line []byte) (any, error) {
	var msg struct {
		JSONRPC string          `json:"jsonrpc"`
		ID      json.RawMessage `json:"id"`
		Method  string          `json:"method"`
	}
	if err := json.Unmarshal(line, &msg); err != nil {
		return nil, err
	}
	if msg.Method != "initialize" {
		return nil, fmt.Errorf("method is %q, not \"initialize\"", msg.Method)
	}
	if len(msg.ID) == 0 || string(msg.ID) == "null" {
		return nil, errors.New("initialize has no request id")
	}
	var id any
	if err := json.Unmarshal(msg.ID, &id); err != nil {
		return nil, err
	}
	return id, nil
}

// replayPrefix returns the bytes a SUCCESSOR upstream must be fed before any
// live client bytes, or nil when there is nothing to replay (no initialize
// captured yet). Calling it declares a new upstream: the previous one can no
// longer answer anything, so its pending response is written off and one is
// expected from the successor.
func (s *sessionContinuity) replayPrefix() []byte {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.initLine == nil {
		// No initialize captured: either this is the first upstream and the
		// client has not spoken yet (the normal first-connection case, where
		// the real initialize flows through and is captured on the way), or
		// the client never sent one. Nothing to replay, nothing pending.
		s.answerPending = false
		return nil
	}
	s.answerPending = true
	s.replays++
	s.logf("session-continuity: replaying the client's cached initialize to a new upstream (replay %d); its duplicate response will not reach the client", s.replays)
	prefix := make([]byte, 0, len(s.initLine)+len(initializedNotification))
	prefix = append(prefix, s.initLine...)
	prefix = append(prefix, initializedNotification...)
	return prefix
}

// outputWriter wraps the client-facing stdout for one upstream. While an
// initialize response is pending it frames upstream output into lines
// and decides per line via forwardLine; otherwise it streams bytes through
// untouched. Not safe for concurrent Write calls -- neither is os.Stdout for
// interleaved NDJSON, and every caller drives it from one goroutine.
func (s *sessionContinuity) outputWriter(w io.Writer) io.Writer {
	return &continuityWriter{s: s, w: w}
}

type continuityWriter struct {
	s *sessionContinuity
	w io.Writer
	// pending is the partial line held back while watching, bounded by
	// maxWatchedOutputLineBytes.
	pending []byte
	// streaming is set once a watched line overflowed the bound: the rest of
	// that line (and everything after, until watching is re-entered by a
	// replay) is streamed through.
	streaming bool
}

func (c *continuityWriter) Write(p []byte) (int, error) {
	total := len(p)
	for len(p) > 0 {
		if c.streaming {
			// Finish the overflowed line, then re-check whether to watch.
			i := bytes.IndexByte(p, '\n')
			if i < 0 {
				return total, writeAll(c.w, p)
			}
			if err := writeAll(c.w, p[:i+1]); err != nil {
				return total, err
			}
			p = p[i+1:]
			c.streaming = false
			continue
		}
		if !c.s.watching() {
			return total, writeAll(c.w, p)
		}
		i := bytes.IndexByte(p, '\n')
		if i < 0 {
			if len(c.pending)+len(p) > maxWatchedOutputLineBytes {
				// Too big to be an initialize response (a real one is a
				// few KiB), so stop holding it -- and stop watching for
				// good: if it somehow were the answer, letting a later
				// replay's duplicate through would show the client two
				// responses, and watching forever would parse every line
				// of every later upstream. Either way the client is
				// treated as answered from here.
				c.s.giveUpWatching()
				c.streaming = true
				if err := writeAll(c.w, c.pending); err != nil {
					return total, err
				}
				c.pending = nil
				continue
			}
			c.pending = append(c.pending, p...)
			return total, nil
		}
		line := append(c.pending, p[:i+1]...)
		c.pending = nil
		p = p[i+1:]
		if c.s.forwardLine(line) {
			if err := writeAll(c.w, line); err != nil {
				return total, err
			}
		}
	}
	return total, nil
}

func writeAll(w io.Writer, b []byte) error {
	for len(b) > 0 {
		n, err := w.Write(b)
		if err != nil {
			return err
		}
		b = b[n:]
	}
	return nil
}

// watching reports whether an initialize response is still expected from the
// current upstream, i.e. whether output must be inspected per line.
func (s *sessionContinuity) watching() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.answerPending
}

// giveUpWatching marks the client as answered without having matched a
// response -- see the overflow path in continuityWriter.Write. It logs, since
// an initialize response over the cap is not something a healthy build emits.
func (s *sessionContinuity) giveUpWatching() {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.answerPending = false
	s.answered = true
	s.logf("session-continuity: an upstream output line exceeded %d bytes while an initialize response was pending; streaming it and treating the client as answered", maxWatchedOutputLineBytes)
}

// forwardLine decides whether one complete upstream->client line reaches the
// client. Everything that is not a response to the cached initialize's id is
// forwarded. The first such response is the client's answer and is forwarded;
// any later one is a replay's duplicate and is dropped -- the client sent one
// initialize and must see exactly one result for it.
func (s *sessionContinuity) forwardLine(line []byte) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	if !s.answerPending {
		return true
	}
	matched, rpcErr := responseTo(line, s.initID)
	if !matched {
		return true
	}
	s.answerPending = false
	if !s.answered {
		s.answered = true
		return true
	}
	if rpcErr != "" {
		// Not a routine duplicate: the successor refused the replayed
		// initialize (a protocol-version or capability rejection, e.g. from
		// a skewed build). The client keeps its original answer and cannot
		// be told; every following request will be refused as "invalid
		// during session initialization". Say why, once, where the person
		// debugging that will look.
		s.logf("session-continuity: the new upstream REJECTED the replayed initialize: %s. The client's session cannot continue through it; its next requests will fail until the client reconnects", rpcErr)
		return false
	}
	s.logf("session-continuity: dropped the duplicate initialize response from the new upstream; the client's session continues")
	return false
}

// responseTo reports whether line is a JSON-RPC response (result or error,
// no method) whose id equals id, and for an error response, its message.
func responseTo(line []byte, id any) (matched bool, rpcErr string) {
	var msg struct {
		ID     json.RawMessage `json:"id"`
		Method string          `json:"method"`
		Result json.RawMessage `json:"result"`
		Error  *struct {
			Code    int    `json:"code"`
			Message string `json:"message"`
		} `json:"error"`
	}
	if err := json.Unmarshal(line, &msg); err != nil {
		return false, ""
	}
	if msg.Method != "" || (len(msg.Result) == 0 && msg.Error == nil) || len(msg.ID) == 0 {
		return false, ""
	}
	var got any
	if err := json.Unmarshal(msg.ID, &got); err != nil {
		return false, ""
	}
	if !reflect.DeepEqual(got, id) {
		return false, ""
	}
	if msg.Error != nil {
		return true, fmt.Sprintf("code %d: %s", msg.Error.Code, msg.Error.Message)
	}
	return true, ""
}
