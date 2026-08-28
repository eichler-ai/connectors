// Package mcpclient is a minimal MCP client speaking JSON-RPC 2.0 over a
// subprocess's stdio -- the same entry point a real MCP host (Claude Code)
// uses, not the broker's internal add-in/TCP wire protocol. Deliberately
// black-box: it knows nothing about the server's internal packages, only
// the public MCP surface, so it exercises the exact interface an agent
// actually sees (PRD's "no fake integration tier" rule, applied to the
// harness itself).
//
// The broker binary this drives runs on whatever platform it was built for
// -- Windows when testing against a VM-hosted Revit (PRD §04), macOS when
// testing against this project's own Mac-native remote-mode dev topology
// (PRD §05). This package itself is platform-agnostic either way.
package mcpclient

import (
	"bufio"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"sync"
	"sync/atomic"
	"time"
)

// Client manages one mcp-server subprocess and speaks line-delimited
// JSON-RPC 2.0 over its stdin/stdout, matching mcp.IOTransport's framing
// (see cmd/mcp-server/main.go).
//
// Exactly one goroutine (started in Start) ever reads the stdout scanner --
// bufio.Scanner is not safe for concurrent use, and demultiplexing incoming
// responses by id into per-call channels here (rather than one reader
// goroutine per call) is what makes a timed-out call's channel abandonable
// without leaving a goroutine parked on a shared reader forever.
type Client struct {
	cmd    *exec.Cmd
	stdin  io.WriteCloser
	nextID int64

	mu      sync.Mutex
	pending map[int64]chan response
	closed  bool

	readerDone chan struct{}
}

type request struct {
	JSONRPC string `json:"jsonrpc"`
	ID      int64  `json:"id,omitempty"`
	Method  string `json:"method"`
	Params  any    `json:"params,omitempty"`
}

type response struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      int64           `json:"id"`
	Result  json.RawMessage `json:"result"`
	Error   *rpcError       `json:"error"`
}

type rpcError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}

// Start launches exePath with args (e.g. "-mode", "local") and completes
// the MCP initialize handshake. exePath is the built mcp-server binary --
// the harness never builds or embeds it; the caller supplies a path already
// deployed for the platform under test.
func Start(exePath string, args ...string) (*Client, error) {
	cmd := exec.Command(exePath, args...)
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return nil, fmt.Errorf("stdin pipe: %w", err)
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return nil, fmt.Errorf("stdout pipe: %w", err)
	}
	// Surface the subprocess's own diagnostic stream directly to this
	// process's stderr rather than swallowing it -- a broker that fails to
	// start (e.g. can't bind, singleton contention) is otherwise a silent
	// hang on the first read. (cmd.Stderr left nil would discard it, not
	// forward it -- os/exec connects a nil Stderr to /dev/null.)
	cmd.Stderr = os.Stderr

	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("start %s: %w", exePath, err)
	}

	scanner := bufio.NewScanner(stdout)
	scanner.Buffer(make([]byte, 0, 64*1024), 16*1024*1024)

	c := &Client{
		cmd:        cmd,
		stdin:      stdin,
		pending:    make(map[int64]chan response),
		readerDone: make(chan struct{}),
	}
	go c.readLoop(scanner)

	initParams := map[string]any{
		"protocolVersion": "2024-11-05",
		"capabilities":    map[string]any{},
		"clientInfo":      map[string]any{"name": "test-harness", "version": "0.0.1"},
	}
	if _, err := c.call("initialize", initParams, 10*time.Second); err != nil {
		c.Close()
		return nil, fmt.Errorf("initialize: %w", err)
	}
	if err := c.notify("notifications/initialized", nil); err != nil {
		c.Close()
		return nil, fmt.Errorf("notifications/initialized: %w", err)
	}
	return c, nil
}

// readLoop is the sole reader of scanner for this Client's lifetime. It
// dispatches each response to the pending channel matching its id (if one
// is still waiting -- a timed-out call's channel was already removed from
// pending, so a late response is just dropped) and exits on EOF/error,
// closing readerDone so Close can safely wait for it before calling
// cmd.Wait (os/exec: reads from a command's pipes must finish before Wait
// is called).
func (c *Client) readLoop(scanner *bufio.Scanner) {
	defer close(c.readerDone)
	for scanner.Scan() {
		var resp response
		if err := json.Unmarshal(scanner.Bytes(), &resp); err != nil {
			continue // notification or malformed line -- skip, keep reading
		}
		c.mu.Lock()
		ch, ok := c.pending[resp.ID]
		if ok {
			delete(c.pending, resp.ID)
		}
		c.mu.Unlock()
		if ok {
			ch <- resp
		}
	}
	// Stream ended (EOF or read error): wake every still-pending call rather
	// than leaving them to wait out their full timeout for no reason.
	c.mu.Lock()
	pending := c.pending
	c.pending = nil
	c.mu.Unlock()
	for _, ch := range pending {
		close(ch)
	}
}

// CallTool invokes name via tools/call and returns the raw result. Timeout
// is the caller's responsibility to pick -- discovery/registration calls
// are fast; execute_script can legitimately run long.
func (c *Client) CallTool(name string, arguments map[string]any, timeout time.Duration) (json.RawMessage, error) {
	params := map[string]any{"name": name, "arguments": arguments}
	return c.call("tools/call", params, timeout)
}

func (c *Client) call(method string, params any, timeout time.Duration) (json.RawMessage, error) {
	id := atomic.AddInt64(&c.nextID, 1)
	req := request{JSONRPC: "2.0", ID: id, Method: method, Params: params}
	line, err := json.Marshal(req)
	if err != nil {
		return nil, err
	}

	// Buffered 1: if this call times out below, readLoop must still be able
	// to hand off a late-arriving response (or the stream-closed close())
	// without blocking on a channel nobody is receiving from anymore.
	ch := make(chan response, 1)
	c.mu.Lock()
	if c.closed || c.pending == nil {
		c.mu.Unlock()
		return nil, fmt.Errorf("client closed")
	}
	c.pending[id] = ch
	c.mu.Unlock()

	if _, err := c.stdin.Write(append(line, '\n')); err != nil {
		c.mu.Lock()
		delete(c.pending, id)
		c.mu.Unlock()
		return nil, fmt.Errorf("write request: %w", err)
	}

	select {
	case resp, ok := <-ch:
		if !ok {
			return nil, fmt.Errorf("stdout closed before response to id %d", id)
		}
		if resp.Error != nil {
			return nil, fmt.Errorf("rpc error %d: %s", resp.Error.Code, resp.Error.Message)
		}
		return resp.Result, nil
	case <-time.After(timeout):
		c.mu.Lock()
		delete(c.pending, id)
		c.mu.Unlock()
		return nil, fmt.Errorf("timed out after %s waiting for response to %s (id %d)", timeout, method, id)
	}
}

func (c *Client) notify(method string, params any) error {
	req := request{JSONRPC: "2.0", Method: method, Params: params}
	line, err := json.Marshal(req)
	if err != nil {
		return err
	}
	_, err = c.stdin.Write(append(line, '\n'))
	return err
}

// Close terminates the subprocess. Safe to call more than once -- the
// second call's cmd.Wait() returns exec's own "Wait was already called"
// error, which callers can ignore alongside this method's own return value.
func (c *Client) Close() error {
	c.mu.Lock()
	c.closed = true
	c.mu.Unlock()

	_ = c.stdin.Close()
	if c.cmd.Process != nil {
		_ = c.cmd.Process.Kill()
	}
	// Wait for readLoop to observe EOF (guaranteed once the process is
	// killed) before calling Wait -- see readLoop's doc comment.
	<-c.readerDone
	if c.cmd.Process == nil {
		return nil
	}
	return c.cmd.Wait()
}
