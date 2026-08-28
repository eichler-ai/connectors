// Package mcpclient is a minimal MCP client speaking JSON-RPC 2.0 over a
// subprocess's stdio -- the same entry point a real MCP host (Claude Code)
// uses, not the broker's internal add-in/TCP wire protocol. Deliberately
// black-box: it knows nothing about the server's internal packages, only
// the public MCP surface, so it exercises the exact interface an agent
// actually sees (PRD's "no fake integration tier" rule, applied to the
// harness itself).
package mcpclient

import (
	"bufio"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"sync/atomic"
	"time"
)

// Client manages one mcp-server subprocess and speaks line-delimited
// JSON-RPC 2.0 over its stdin/stdout, matching mcp.IOTransport's framing
// (see cmd/mcp-server/main.go).
type Client struct {
	cmd     *exec.Cmd
	stdin   io.WriteCloser
	scanner *bufio.Scanner
	nextID  int64
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
// deployed for the target platform (PRD §04: this is Windows-only, since
// Revit itself is).
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

	c := &Client{cmd: cmd, stdin: stdin, scanner: scanner}

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
	if _, err := c.stdin.Write(append(line, '\n')); err != nil {
		return nil, fmt.Errorf("write request: %w", err)
	}

	type result struct {
		resp response
		err  error
	}
	done := make(chan result, 1)
	go func() {
		for c.scanner.Scan() {
			var resp response
			if err := json.Unmarshal(c.scanner.Bytes(), &resp); err != nil {
				continue // notification or malformed line -- skip, keep reading for our id
			}
			if resp.ID == id {
				done <- result{resp: resp}
				return
			}
		}
		done <- result{err: fmt.Errorf("stdout closed before response to id %d: %w", id, c.scanner.Err())}
	}()

	select {
	case r := <-done:
		if r.err != nil {
			return nil, r.err
		}
		if r.resp.Error != nil {
			return nil, fmt.Errorf("rpc error %d: %s", r.resp.Error.Code, r.resp.Error.Message)
		}
		return r.resp.Result, nil
	case <-time.After(timeout):
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

// Close terminates the subprocess. Safe to call once; a second call is a
// no-op error the caller can ignore.
func (c *Client) Close() error {
	_ = c.stdin.Close()
	if c.cmd.Process == nil {
		return nil
	}
	_ = c.cmd.Process.Kill()
	return c.cmd.Wait()
}
