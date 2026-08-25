package transport

import (
	"context"
	"encoding/json"
	"io"
	"net"
	"testing"
	"time"
)

// pipeConns returns two Conns wired together over an in-memory net.Pipe,
// each with its read loop already running.
func pipeConns(t *testing.T) (a, b *Conn) {
	t.Helper()
	c1, c2 := net.Pipe()
	a = NewConn(c1)
	b = NewConn(c2)
	go a.Serve()
	go b.Serve()
	t.Cleanup(func() {
		a.Close()
		b.Close()
	})
	return a, b
}

func TestConnCallReceivesResult(t *testing.T) {
	a, b := pipeConns(t)

	b.SetRequestHandler(func(ctx context.Context, method string, params json.RawMessage) (any, *RPCError) {
		if method != "ping" {
			return nil, &RPCError{Code: ErrCodeMethodNotFound, Message: "unknown method"}
		}
		return map[string]string{"reply": "pong"}, nil
	})

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()

	var result map[string]string
	raw, rpcErr, err := a.Call(ctx, "ping", nil)
	if err != nil {
		t.Fatalf("Call: %v", err)
	}
	if rpcErr != nil {
		t.Fatalf("unexpected RPC error: %+v", rpcErr)
	}
	if err := json.Unmarshal(raw, &result); err != nil {
		t.Fatalf("Unmarshal result: %v", err)
	}
	if result["reply"] != "pong" {
		t.Errorf("result = %+v, want reply=pong", result)
	}
}

func TestConnCallReceivesRPCError(t *testing.T) {
	a, b := pipeConns(t)

	b.SetRequestHandler(func(ctx context.Context, method string, params json.RawMessage) (any, *RPCError) {
		return nil, &RPCError{Code: ErrCodeInvalidParams, Message: "bad params"}
	})

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()

	_, rpcErr, err := a.Call(ctx, "whatever", nil)
	if err != nil {
		t.Fatalf("Call: %v", err)
	}
	if rpcErr == nil || rpcErr.Code != ErrCodeInvalidParams {
		t.Fatalf("got rpcErr=%+v, want code %d", rpcErr, ErrCodeInvalidParams)
	}
}

func TestConnCallTimesOutWhenPeerNeverResponds(t *testing.T) {
	a, b := pipeConns(t)
	b.SetRequestHandler(func(ctx context.Context, method string, params json.RawMessage) (any, *RPCError) {
		select {} // never respond
	})

	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()

	_, _, err := a.Call(ctx, "slow", nil)
	if err == nil {
		t.Fatalf("expected timeout error")
	}
}

func TestConnNotifyDeliversToHandler(t *testing.T) {
	a, b := pipeConns(t)

	received := make(chan string, 1)
	b.SetNotificationHandler(func(method string, params json.RawMessage) {
		received <- method
	})

	if err := a.Notify("register", map[string]string{"instance_id": "x"}); err != nil {
		t.Fatalf("Notify: %v", err)
	}

	select {
	case m := <-received:
		if m != "register" {
			t.Errorf("got method %q, want register", m)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for notification")
	}
}

func TestConnCloseUnblocksReadLoop(t *testing.T) {
	c1, c2 := net.Pipe()
	a := NewConn(c1)
	done := make(chan error, 1)
	go func() { done <- a.Serve() }()

	if err := a.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}
	c2.Close()

	select {
	case err := <-done:
		if err == nil {
			t.Fatalf("Serve should return an error (closed) not nil")
		}
	case <-time.After(2 * time.Second):
		t.Fatal("Serve did not return after Close")
	}
}

func TestConnCallAfterCloseErrors(t *testing.T) {
	c1, _ := net.Pipe()
	a := NewConn(c1)
	go a.Serve()
	a.Close()

	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	_, _, err := a.Call(ctx, "ping", nil)
	if err == nil {
		t.Fatalf("expected error calling on a closed Conn")
	}
	if err != io.EOF && err != ErrClosed {
		// either is acceptable depending on where in the pipeline it's detected
		t.Logf("Call after close returned: %v", err)
	}
}
