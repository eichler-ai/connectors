package main

import (
	"errors"
	"io"
	"testing"
	"time"
)

// TestTurnReaderStopUnblocksReadEvenWithNoData is the regression test for
// the promotion bug: a role's copy-loop must be able to walk away from
// reading stdin promptly, even when no new stdin data is currently
// arriving — otherwise it's exactly as orphan-prone as reading os.Stdin
// directly, defeating the whole point of the relay.
func TestTurnReaderStopUnblocksReadEvenWithNoData(t *testing.T) {
	relay := &stdinRelay{chunks: make(chan []byte)} // no feeder goroutine: simulates "no data arriving"
	stop := make(chan struct{})
	r := &turnReader{relay: relay, stop: stop}

	done := make(chan error, 1)
	go func() {
		buf := make([]byte, 16)
		_, err := r.Read(buf)
		done <- err
	}()

	// Give the goroutine a moment to actually enter the blocking select
	// before we close stop, so this isn't accidentally testing a select
	// that was never blocked in the first place.
	time.Sleep(20 * time.Millisecond)
	close(stop)

	select {
	case err := <-done:
		if !errors.Is(err, io.EOF) {
			t.Errorf("Read error = %v, want io.EOF", err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("Read did not unblock after stop was closed — this is the orphaned-goroutine bug")
	}
}

// TestTurnReaderDeliversQueuedDataBeforeStop confirms turnReader isn't
// just a stop-signal wrapper — it still delivers real relay data normally
// when there is any.
func TestTurnReaderDeliversQueuedDataBeforeStop(t *testing.T) {
	relay := &stdinRelay{chunks: make(chan []byte, 1)}
	relay.chunks <- []byte("hello")
	stop := make(chan struct{})
	r := &turnReader{relay: relay, stop: stop}

	buf := make([]byte, 16)
	n, err := r.Read(buf)
	if err != nil {
		t.Fatalf("Read: %v", err)
	}
	if string(buf[:n]) != "hello" {
		t.Errorf("Read = %q, want %q", buf[:n], "hello")
	}
}

// TestTurnReaderSplitsAcrossSmallBuffers confirms the leftover-tracking
// logic correctly hands out a chunk larger than the caller's buffer across
// multiple Read calls, since io.Copy (the real caller) may use small
// buffers.
func TestTurnReaderSplitsAcrossSmallBuffers(t *testing.T) {
	relay := &stdinRelay{chunks: make(chan []byte, 1)}
	relay.chunks <- []byte("hello world")
	stop := make(chan struct{})
	r := &turnReader{relay: relay, stop: stop}

	var got []byte
	buf := make([]byte, 4)
	for len(got) < len("hello world") {
		n, err := r.Read(buf)
		if err != nil {
			t.Fatalf("Read: %v", err)
		}
		got = append(got, buf[:n]...)
	}
	if string(got) != "hello world" {
		t.Errorf("got %q, want %q", got, "hello world")
	}
}

// TestTurnReaderRelayClosedReturnsEOF confirms that if the underlying
// physical stdin itself closes/EOFs (the relay's chunks channel closes),
// a turnReader that's still active sees a clean EOF too, not a hang.
func TestTurnReaderRelayClosedReturnsEOF(t *testing.T) {
	relay := &stdinRelay{chunks: make(chan []byte)}
	close(relay.chunks)
	stop := make(chan struct{})
	r := &turnReader{relay: relay, stop: stop}

	buf := make([]byte, 16)
	_, err := r.Read(buf)
	if !errors.Is(err, io.EOF) {
		t.Errorf("Read error = %v, want io.EOF", err)
	}
}
