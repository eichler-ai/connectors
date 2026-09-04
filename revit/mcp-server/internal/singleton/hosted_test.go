package singleton

import (
	"bytes"
	"errors"
	"os"
	"testing"
	"time"
)

func alwaysAlive(int) bool { return true }
func neverAlive(int) bool  { return false }

func TestHostedRequestRoundTrip(t *testing.T) {
	dir := t.TempDir()
	now := time.Now()
	if err := WriteHostedRequest(dir, 4242, now); err != nil {
		t.Fatal(err)
	}
	r, err := ReadHostedRequest(dir)
	if err != nil {
		t.Fatal(err)
	}
	if r.PID != 4242 || r.RequestedAt != now.UnixNano() {
		t.Fatalf("got %+v, want pid 4242 at %d", r, now.UnixNano())
	}
	// No temp file left beside it.
	entries, _ := os.ReadDir(dir)
	if len(entries) != 1 {
		t.Fatalf("expected only the request file, got %d entries", len(entries))
	}

	// A rewrite replaces, never appends.
	later := now.Add(time.Second)
	if err := WriteHostedRequest(dir, 4242, later); err != nil {
		t.Fatal(err)
	}
	r, _ = ReadHostedRequest(dir)
	if r.RequestedAt != later.UnixNano() {
		t.Fatalf("rewrite did not refresh the timestamp: %+v", r)
	}

	if err := RemoveHostedRequest(dir); err != nil {
		t.Fatal(err)
	}
	if _, err := ReadHostedRequest(dir); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("after remove, expected not-exist, got %v", err)
	}
	// Removing an absent file is fine: the common case after a hosted primary
	// already cleaned up.
	if err := RemoveHostedRequest(dir); err != nil {
		t.Fatalf("second remove: %v", err)
	}
}

func TestHostedRequestValidity(t *testing.T) {
	now := time.Now()
	cases := []struct {
		name  string
		req   HostedRequest
		alive func(int) bool
		want  bool
	}{
		{"fresh and alive", HostedRequest{PID: 1, RequestedAt: now.Add(-time.Second).UnixNano()}, alwaysAlive, true},
		{"just under the age bound", HostedRequest{PID: 1, RequestedAt: now.Add(-HostedRequestMaxAge + time.Second).UnixNano()}, alwaysAlive, true},
		{"stale", HostedRequest{PID: 1, RequestedAt: now.Add(-HostedRequestMaxAge - time.Second).UnixNano()}, alwaysAlive, false},
		{"dead pid", HostedRequest{PID: 1, RequestedAt: now.UnixNano()}, neverAlive, false},
		{"from the future (clock stepped back)", HostedRequest{PID: 1, RequestedAt: now.Add(time.Minute).UnixNano()}, alwaysAlive, false},
		{"no pid", HostedRequest{PID: 0, RequestedAt: now.UnixNano()}, alwaysAlive, false},
		{"zero value", HostedRequest{}, alwaysAlive, false},
	}
	for _, c := range cases {
		if got := c.req.Valid(now, c.alive); got != c.want {
			t.Errorf("%s: Valid = %v, want %v", c.name, got, c.want)
		}
	}
}

func TestPendingHostedRequestFiltersEveryNonObligation(t *testing.T) {
	dir := t.TempDir()
	now := time.Now()

	// Missing file: nobody waiting.
	if _, ok := PendingHostedRequest(dir, 1, now, alwaysAlive); ok {
		t.Fatal("missing file reported as pending")
	}
	// Garbage file: nobody waiting (never "step down" on a decode failure).
	if err := os.WriteFile(HostedRequestPath(dir), []byte("{not json"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, ok := PendingHostedRequest(dir, 1, now, alwaysAlive); ok {
		t.Fatal("garbage file reported as pending")
	}
	// Fresh, alive, other process: pending.
	if err := WriteHostedRequest(dir, 77, now); err != nil {
		t.Fatal(err)
	}
	r, ok := PendingHostedRequest(dir, 1, now, alwaysAlive)
	if !ok || r.PID != 77 {
		t.Fatalf("fresh request not pending: %+v %v", r, ok)
	}
	// Our own pid: not an obligation.
	if _, ok := PendingHostedRequest(dir, 77, now, alwaysAlive); ok {
		t.Fatal("own request reported as pending")
	}
	// Dead pid: not pending.
	if _, ok := PendingHostedRequest(dir, 1, now, neverAlive); ok {
		t.Fatal("dead-pid request reported as pending")
	}
	// Aged out: not pending.
	if _, ok := PendingHostedRequest(dir, 1, now.Add(HostedRequestMaxAge+time.Minute), alwaysAlive); ok {
		t.Fatal("stale request reported as pending")
	}
}

func TestBrokerInfoHostedRoundTrip(t *testing.T) {
	dir := t.TempDir()
	if err := WriteBrokerJSON(dir, BrokerInfo{Host: "127.0.0.1", Port: 1, PID: 9, Token: "t", Hosted: true}); err != nil {
		t.Fatal(err)
	}
	got, err := ReadBrokerJSON(dir)
	if err != nil {
		t.Fatal(err)
	}
	if !got.Hosted {
		t.Fatal("Hosted did not round-trip")
	}
	// Omitted when false, so an older reader sees the file it always did.
	if err := WriteBrokerJSON(dir, BrokerInfo{Host: "127.0.0.1", Port: 1, PID: 9, Token: "t"}); err != nil {
		t.Fatal(err)
	}
	b, _ := os.ReadFile(dir + "/broker.json")
	if string(b) == "" || containsHosted(b) {
		t.Fatalf("a non-hosted broker.json must not carry the field:\n%s", b)
	}
	got, _ = ReadBrokerJSON(dir)
	if got.Hosted {
		t.Fatal("Hosted read back true from a file without it")
	}
}

func containsHosted(b []byte) bool { return bytes.Contains(b, []byte(`"hosted"`)) }
