package main

import (
	"errors"
	"io"
	"log"
	"strings"
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

// readWithTimeout runs r.Read(buf) and returns its result, or fails the test
// if it doesn't return within the timeout — used below so a reintroduced
// stealing bug (the next turnReader hanging forever waiting for data a
// stopped reader silently consumed) fails cleanly instead of just hanging
// until the test binary's own timeout.
func readWithTimeout(t *testing.T, r *turnReader, timeout time.Duration) (int, []byte, error) {
	t.Helper()
	type result struct {
		n   int
		buf []byte
		err error
	}
	done := make(chan result, 1)
	go func() {
		buf := make([]byte, 16)
		n, err := r.Read(buf)
		done <- result{n, append([]byte(nil), buf[:n]...), err}
	}()
	select {
	case res := <-done:
		return res.n, res.buf, res.err
	case <-time.After(timeout):
		t.Fatal("Read did not return within timeout")
		return 0, nil, nil
	}
}

// TestTurnReaderLeftoverNotServedAfterStop is the regression test for
// finding (c): a turnReader that has been told to stop must never hand out
// its buffered leftover to a caller — that data must instead be donated
// back to the relay so the NEXT turnReader constructed over the same relay
// (the newly-promoted role, in a real run() role transition) picks up
// exactly where the stopped one left off.
func TestTurnReaderLeftoverNotServedAfterStop(t *testing.T) {
	relay := &stdinRelay{chunks: make(chan []byte, 1)}
	relay.chunks <- []byte("hello world")
	stop := make(chan struct{})
	r := &turnReader{relay: relay, stop: stop}

	buf := make([]byte, 5)
	n, err := r.Read(buf) // consumes "hello", leaves " world" buffered as leftover
	if err != nil || string(buf[:n]) != "hello" {
		t.Fatalf("first Read = %q, err=%v", buf[:n], err)
	}

	close(stop)

	n, err = r.Read(buf)
	if !errors.Is(err, io.EOF) {
		t.Fatalf("Read after stop: err = %v, want io.EOF", err)
	}
	if n != 0 {
		t.Fatalf("Read after stop returned %d bytes, want 0 — a stopped reader must never hand out its leftover", n)
	}

	// The next turnReader over the same relay must receive exactly the
	// undelivered tail — not lose it, not see it duplicated.
	stop2 := make(chan struct{})
	r2 := &turnReader{relay: relay, stop: stop2}
	n2, got, err2 := readWithTimeout(t, r2, 2*time.Second)
	if err2 != nil {
		t.Fatalf("r2.Read: %v", err2)
	}
	if string(got[:n2]) != " world" {
		t.Errorf("r2 got %q, want the donated leftover %q", got[:n2], " world")
	}
}

// TestTurnReaderDeliverOrDonate_StopAlreadyClosed_DonatesRatherThanDelivers is
// the deterministic unit test for finding (b): the specific case where a
// reader has already won a chunk off relay.chunks but stop is (by then)
// closed. This is the exact code path TestTurnReaderStopDoesNotStealFromNextReader
// originally tried to exercise end-to-end via real goroutine scheduling —
// which turned out to be unreliable under host CPU contention (see that
// test's own comment). Calling deliverOrDonate directly sidesteps needing to
// actually win the real select race: it tests "given a chunk was won and
// stop is closed, what happens" in isolation, with no dependency on
// scheduling timing at all.
func TestTurnReaderDeliverOrDonate_StopAlreadyClosed_DonatesRatherThanDelivers(t *testing.T) {
	relay := &stdinRelay{chunks: make(chan []byte)}
	stop := make(chan struct{})
	close(stop)
	r := &turnReader{relay: relay, stop: stop}

	buf := make([]byte, 16)
	n, err := r.deliverOrDonate(buf, []byte{42, 43})

	if n != 0 || err != io.EOF {
		t.Fatalf("deliverOrDonate with stop closed = (%d, %v), want (0, io.EOF) — the chunk must never reach this reader's caller once it's been told to stop", n, err)
	}
	pending := relay.takePending()
	if string(pending) != string([]byte{42, 43}) {
		t.Fatalf("relay.pending after a stopped deliverOrDonate = %v, want the donated chunk %v so the next turnReader over this relay picks it up", pending, []byte{42, 43})
	}
}

// TestTurnReaderDeliverOrDonate_StopStillOpen_DeliversNormally is the
// complementary deterministic case: a reader that hasn't been told to stop
// must deliver a won chunk normally, including the leftover-carryover
// behavior when the chunk is larger than the caller's buffer.
func TestTurnReaderDeliverOrDonate_StopStillOpen_DeliversNormally(t *testing.T) {
	relay := &stdinRelay{chunks: make(chan []byte)}
	r := &turnReader{relay: relay, stop: make(chan struct{})}

	buf := make([]byte, 2)
	n, err := r.deliverOrDonate(buf, []byte{1, 2, 3})

	if err != nil {
		t.Fatalf("deliverOrDonate with stop open: err = %v, want nil", err)
	}
	if n != 2 || string(buf[:n]) != string([]byte{1, 2}) {
		t.Fatalf("deliverOrDonate = (%d, %v), want to fill the 2-byte buffer with the chunk's first 2 bytes", n, buf[:n])
	}
	if string(r.leftover) != string([]byte{3}) {
		t.Fatalf("r.leftover after a short buffer = %v, want the chunk's remaining byte %v carried over", r.leftover, []byte{3})
	}
}

// TestTurnReaderStopDoesNotStealFromNextReader is the regression test for
// findings (a)/(b): two turnReaders constructed over the SAME stdinRelay,
// simulating a real run() role transition. r1 is parked genuinely blocked in
// its own select (stop1 still open, nothing on chunks yet, exactly like the
// still-live orphaned copy-goroutine described in finding (a)); stop1's
// close is then raced, by the Go scheduler and not by test sequencing,
// against a chunk arriving on the shared relay — exactly finding (b)'s
// claim that select has no priority between its cases, so a stopped reader
// can otherwise still win the chunk case roughly half the time.
//
// It is legitimate — not a bug — for r1 to occasionally win this race and
// receive the chunk as real data: if the send-to-r1 rendezvous genuinely
// completed before stop1's close was even observed anywhere, r1 was still
// the active role at that real-world instant, and no code can retroactively
// un-deliver data that arrived before the decision to stop was made. What
// must NEVER happen, on ANY round, is for that byte to be silently lost:
// either r1 legitimately won and consumed it (and, per the fix, no one else
// must also see it — the duplicate-delivery check below), or r1 was stopped
// without consuming it and the byte must show up, undiminished, on r2 — the
// newly-promoted role's reader — instead (checked every round below).
//
// On top of that per-round invariant, this also LOGS how often r1 wins the
// race at all, purely as informational context — it is NOT a pass/fail
// gate. It was originally one (asserting the rate stays under an
// empirically-calibrated threshold), but that turned out not to hold up.
//
// The per-round invariant above genuinely does still catch real regressions
// in deliverOrDonate -- verified by mutating it to still return (0, io.EOF)
// on the stop-closed path but skip the actual r.relay.donate(chunk) call: the
// byte is then truly lost (neither r1 nor r2 ever sees it), and the r2
// readWithTimeout call below correctly times out and fails the test. What
// the per-round invariant can NOT catch is deliverOrDonate's stop-recheck
// being missing ENTIRELY (i.e. this file reverted to the pre-fix behavior of
// unconditionally delivering whatever chunk was won) -- verified by deleting
// that whole select block: r1 then simply receives the chunk as normal data
// (n &gt; 0, err == nil), which is exactly the "r1 legitimately won" case this
// test already treats as legitimate, so every per-round check still passes.
// (The precise, deterministic unit tests for that specific code path --
// TestTurnReaderDeliverOrDonate_StopAlreadyClosed_DonatesRatherThanDelivers
// and its sibling above -- are what actually cover that gap; this test's
// real remaining value is exercising the donate/takePending handoff under
// genuine concurrent scheduling with -race, which those deterministic tests
// don't.) The rate itself also turned out to be far more sensitive to host
// CPU contention than its original calibration assumed, on this
// machine ranging anywhere from ~45% to ~50% under load regardless of
// whether the mitigation is present. The mitigation's actual correctness is
// instead covered deterministically, independent of scheduling, by
// TestTurnReaderDeliverOrDonate_StopAlreadyClosed_DonatesRatherThanDelivers
// and its sibling above.
func TestTurnReaderStopDoesNotStealFromNextReader(t *testing.T) {
	// 100 rounds under -short (the routine dev loop), 300 for the full run:
	// the rate check below is informational-only (see the comment above), so
	// the statistical margin extra rounds buy is not worth ~2s of every
	// -short cycle (test-quality pass).
	rounds := 300
	if testing.Short() {
		rounds = 100
	}
	r1Wins := 0
	for i := 0; i < rounds; i++ {
		relay := &stdinRelay{chunks: make(chan []byte)}
		stop1 := make(chan struct{})
		r1 := &turnReader{relay: relay, stop: stop1}

		want := []byte{byte(i)}

		type r1Result struct {
			n   int
			err error
		}
		r1Done := make(chan r1Result, 1)
		go func() {
			buf := make([]byte, 16)
			n, err := r1.Read(buf)
			r1Done <- r1Result{n, err}
		}()
		// Give r1 a moment to actually enter its blocking select (stop1
		// open, chunks empty) before racing its close against the send —
		// otherwise this would just be testing the (already-covered)
		// trivial case of stop already closed at Read-call time. 1ms
		// turned out not to be enough headroom under real scheduler
		// contention (observed: r1's goroutine not yet scheduled into its
		// select by the time the race fires, so both cases end up ready
		// simultaneously and select's 50/50 tie-break dominates the
		// measured rate instead of the actual fix behavior this test
		// exists to measure) — 10ms gives comfortably more margin without
		// materially slowing the suite (300 rounds ~= 3s).
		time.Sleep(10 * time.Millisecond)

		// Release both racers from a single shared gate instead of two
		// independent `go` statements immediately after each other —
		// spawn order alone turned out to bias the outcome (Go's
		// scheduler tends to run the most-recently-readied goroutine on a
		// P first), which would silently make this test never actually
		// exercise the tie. Alternate which racer is spawned (and so
		// woken) first across rounds to cancel out that bias and
		// genuinely exercise both orderings.
		closeFn := func() { close(stop1) }
		sendFn := func() { relay.chunks <- want }
		first, second := closeFn, sendFn
		if i%2 == 1 {
			first, second = sendFn, closeFn
		}
		gate := make(chan struct{})
		go func() {
			<-gate
			first()
		}()
		go func() {
			<-gate
			second()
		}()
		close(gate)

		res := <-r1Done
		if res.n > 0 {
			r1Wins++
			// r1 legitimately won the race before observing stop1
			// closed — per the fix's own reasoning this is only correct
			// if it happened before the close was visible, in which case
			// nothing else must ALSO receive `want`. Confirm r2 sees
			// nothing pending (no duplicate delivery) rather than
			// asserting anything about who "should" have gotten it.
			stop2 := make(chan struct{})
			r2 := &turnReader{relay: relay, stop: stop2}
			done := make(chan struct{})
			go func() {
				buf := make([]byte, 16)
				n2, _ := r2.Read(buf)
				if n2 > 0 {
					t.Errorf("round %d: byte %v delivered to BOTH r1 and r2 — duplicate delivery", i, want)
				}
				close(done)
			}()
			select {
			case <-done:
			case <-time.After(5 * time.Millisecond):
				// Expected: r2 has nothing to read and stays blocked —
				// close stop2 to unblock the probe goroutine cleanly.
				close(stop2)
				<-done
			}
			continue
		}
		if res.err != io.EOF {
			t.Fatalf("round %d: r1.Read = (0, %v), want io.EOF when it didn't consume the chunk", i, res.err)
		}

		// r1 was stopped without consuming the chunk: it must not have
		// been lost — the next turnReader over the same relay must
		// receive it.
		stop2 := make(chan struct{})
		r2 := &turnReader{relay: relay, stop: stop2}
		n, got, err := readWithTimeout(t, r2, 2*time.Second)
		if err != nil {
			t.Fatalf("round %d: r2.Read: %v", i, err)
		}
		if string(got[:n]) != string(want) {
			t.Fatalf("round %d: r2 got %v, want the byte %v that r1 did not consume", i, got[:n], want)
		}
	}

	// This rate is informational only now, not a pass/fail gate -- see the function-level doc comment
	// above for the full story (what the per-round checks above this DO and don't catch, and why the
	// rate itself turned out to be too sensitive to host CPU contention to threshold reliably: it's
	// been observed anywhere from ~13% to ~50% on this same machine alone across different runs,
	// depending on unrelated background load at the time). Kept as a logged data point since it's
	// still useful context when investigating a real, per-round failure above.
	rate := float64(r1Wins) / float64(rounds)
	t.Logf("stopped reader r1 won the chunk race in %d/%d (%.0f%%) rounds (informational; see comment above)", r1Wins, rounds, rate*100)
}

// TestRunRejectsUnparseableBindAddr is the regression test for finding 3: a
// -bind value net.ParseIP can't parse at all (not a literal IP address —
// e.g. a hostname, or a malformed literal like "0" or "0x0.0x0.0x0.0x0")
// must be rejected outright, not passed straight through to net.Listen
// where it can still resolve to something that binds every interface. Only
// the -bind == unspecified-IP case (0.0.0.0 etc.) was being rejected before
// this fix; a string net.ParseIP returns nil for skipped validation
// entirely.
func TestRunRejectsUnparseableBindAddr(t *testing.T) {
	logger := log.New(io.Discard, "", 0)
	cases := []string{
		"0",                // not a valid IP literal at all
		"0x0.0x0.0x0.0x0",  // malformed literal some resolvers still accept
		"not-a-host-or-ip", // arbitrary hostname
		"",                 // empty string
	}
	for _, bindAddr := range cases {
		t.Run(bindAddr, func(t *testing.T) {
			err := run("remote", bindAddr, 0, t.TempDir(), logger)
			if err == nil {
				t.Fatalf("run(mode=remote, bind=%q) = nil error, want it rejected as an unparseable -bind value", bindAddr)
			}
			if bindAddr != "" && !strings.Contains(err.Error(), "not a valid IP address literal") {
				t.Errorf("run(mode=remote, bind=%q) error = %q, want it to name the unparseable-IP reason", bindAddr, err)
			}
		})
	}
}

// TestRunRemoteModeRequiresAppDataDir is the regression test for the fix
// where -mode=remote with no explicit -app-data-dir used to silently fall
// back to singleton.AppDataDir() (the local platform app-data directory)
// instead of failing fast. Per PRD §05, remote mode must write broker.json
// to the shared drive's agreed root, not the local app-data directory, so
// omitting -app-data-dir in remote mode must be a loud, actionable error
// rather than a silent misconfiguration the add-in side can never discover.
func TestRunRemoteModeRequiresAppDataDir(t *testing.T) {
	logger := log.New(io.Discard, "", 0)
	// A valid non-loopback -bind, so the error under test is the
	// dataDir one, not an earlier -bind validation failure.
	err := run("remote", "192.0.2.1", 0, "", logger)
	if err == nil {
		t.Fatal("run(mode=remote, app-data-dir=\"\") = nil error, want it rejected for missing -app-data-dir")
	}
	if !strings.Contains(err.Error(), "-app-data-dir is required in remote mode") {
		t.Errorf("run(mode=remote, app-data-dir=\"\") error = %q, want it to name the missing -app-data-dir reason", err.Error())
	}
}

// TestRunRejectsUnspecifiedBindAddr confirms the pre-existing
// every-interface guard (0.0.0.0, ::, ::0 — all parseable IPs that are still
// not allowed) still works alongside the new unparseable-literal guard.
func TestRunRejectsUnspecifiedBindAddr(t *testing.T) {
	logger := log.New(io.Discard, "", 0)
	for _, bindAddr := range []string{"0.0.0.0", "::", "::0"} {
		t.Run(bindAddr, func(t *testing.T) {
			err := run("remote", bindAddr, 0, t.TempDir(), logger)
			if err == nil {
				t.Fatalf("run(mode=remote, bind=%q) = nil error, want it rejected as an every-interface address", bindAddr)
			}
			if !strings.Contains(err.Error(), "not allowed") {
				t.Errorf("run(mode=remote, bind=%q) error = %q, want the every-interface rejection message", bindAddr, err)
			}
		})
	}
}

// TestStdinRelayExhausted pins the terminal-state signal the re-election
// loop uses to exit once the MCP client has closed stdin (v1 integrated
// review: without it, a secondary whose upstream dropped after stdin EOF
// re-ran the election forever, redialing the primary about twice a second).
// Built by hand rather than via newStdinRelay, which is hard-wired to the
// real os.Stdin.
func TestStdinRelayExhausted(t *testing.T) {
	r := &stdinRelay{chunks: make(chan []byte)}
	if r.exhausted() {
		t.Fatal("a relay with stdin still open must not report exhausted")
	}

	r.closed.Store(true)
	if !r.exhausted() {
		t.Fatal("stdin closed with nothing pending must report exhausted")
	}

	// Donated leftover keeps the relay non-exhausted — a successor role
	// still has input to consume even though stdin itself is closed.
	r.donate([]byte("tail"))
	if r.exhausted() {
		t.Fatal("stdin closed but with donated data pending must not report exhausted")
	}
	if got := string(r.takePending()); got != "tail" {
		t.Fatalf("takePending = %q, want the donated tail", got)
	}
	if !r.exhausted() {
		t.Fatal("once pending is drained and stdin is closed, exhausted must be true")
	}
}
