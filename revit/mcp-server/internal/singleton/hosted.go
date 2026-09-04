package singleton

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"
)

// Hosted primary (revit/docs/self-update-architecture.md §5, Part B step 2).
//
// PRD §05's singleton makes the primary whoever wins the lock -- usually a
// client-spawned process nobody can name or restart. A "hosted" server
// (mcp-server -hosted, one long-lived process per user) exists so an installer,
// a Status window, or a person has exactly one process to restart. But an OS
// file lock is not preemptible: a hosted server that starts while a client
// already holds the lock cannot take it, so the hand-off is cooperative:
//
//   - the hosted server, finding the lock held, writes broker.hosted-request
//     (its pid and a timestamp) and keeps re-running the election;
//   - a NON-hosted primary polls for that file and, when it names a live
//     process and is fresh, steps down -- releasing the lock it cannot be
//     forced off -- and re-enters the election as a secondary;
//   - the hosted server wins the freed lock, removes the request, and
//     advertises itself in broker.json with Hosted: true.
//
// The request is advisory and self-cleaning: a crashed hosted server's request
// names a dead pid, and an abandoned one ages out, so neither can make clients
// step down forever. The election mutex (#212) still serializes the actual lock
// hand-off; this file only decides who WANTS the lock.

// hostedRequestFile is the yield request's name under the rendezvous root.
const hostedRequestFile = "broker.hosted-request"

// HostedRequestMaxAge is how old a yield request may be and still oblige a
// non-hosted primary to step down. A waiting hosted server rewrites its request
// far more often than this (every election turn, ~500 ms), so a request this
// stale was left by a server that stopped looping -- hung, or killed in a way
// ProcessAlive cannot see through (a pid reused by an unrelated process).
const HostedRequestMaxAge = 30 * time.Second

// HostedRequest is the content of broker.hosted-request.
type HostedRequest struct {
	// PID is the hosted server asking for the lock.
	PID int `json:"pid"`
	// RequestedAt is when it last (re)wrote the request, unix nanoseconds.
	RequestedAt int64 `json:"requested_at"`
}

// Valid reports whether a request still obliges a non-hosted primary to yield:
// it is younger than HostedRequestMaxAge as of now, and alive says its pid is
// still running. A request from the future (clock stepped back) is not fresh.
func (r HostedRequest) Valid(now time.Time, alive func(pid int) bool) bool {
	if r.PID <= 0 {
		return false
	}
	age := now.Sub(time.Unix(0, r.RequestedAt))
	if age < 0 || age > HostedRequestMaxAge {
		return false
	}
	return alive(r.PID)
}

// HostedRequestPath is the request file under dir.
func HostedRequestPath(dir string) string { return filepath.Join(dir, hostedRequestFile) }

// WriteHostedRequest publishes (or refreshes) this process's yield request.
// Atomic like broker.json (temp + rename), so a reader never sees a partial
// file. The temp name carries the pid so two hosted servers on one root
// cannot clobber each other's temp file.
func WriteHostedRequest(dir string, pid int, now time.Time) error {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return fmt.Errorf("singleton: creating app-data directory %q: %w", dir, err)
	}
	b, err := json.Marshal(HostedRequest{PID: pid, RequestedAt: now.UnixNano()})
	if err != nil {
		return fmt.Errorf("singleton: encoding hosted request: %w", err)
	}
	final := HostedRequestPath(dir)
	tmp := fmt.Sprintf("%s.%d.tmp", final, pid)
	if err := os.WriteFile(tmp, b, 0o600); err != nil {
		return fmt.Errorf("singleton: writing %q: %w", tmp, err)
	}
	if err := os.Rename(tmp, final); err != nil {
		_ = os.Remove(tmp)
		return fmt.Errorf("singleton: renaming %q to %q: %w", tmp, final, err)
	}
	return nil
}

// ReadHostedRequest reads the request under dir. A missing file is an error
// wrapping fs.ErrNotExist; use PendingHostedRequest for the yes/no question.
func ReadHostedRequest(dir string) (HostedRequest, error) {
	path := HostedRequestPath(dir)
	b, err := os.ReadFile(path)
	if err != nil {
		return HostedRequest{}, fmt.Errorf("singleton: reading %q: %w", path, err)
	}
	var r HostedRequest
	if err := json.Unmarshal(b, &r); err != nil {
		return HostedRequest{}, fmt.Errorf("singleton: decoding %q: %w", path, err)
	}
	return r, nil
}

// RemoveHostedRequest deletes the request file; a missing file is not an error.
func RemoveHostedRequest(dir string) error {
	err := os.Remove(HostedRequestPath(dir))
	if err != nil && !errors.Is(err, os.ErrNotExist) {
		return fmt.Errorf("singleton: removing hosted request: %w", err)
	}
	return nil
}

// PendingHostedRequest reports whether a VALID yield request (see
// HostedRequest.Valid) from a process other than self is present under dir,
// and returns it. A missing, unreadable, stale or dead-pid request reads as no
// request: every failure mode of the file means "nobody is waiting", never
// "step down".
func PendingHostedRequest(dir string, self int, now time.Time, alive func(pid int) bool) (HostedRequest, bool) {
	r, err := ReadHostedRequest(dir)
	if err != nil || r.PID == self || !r.Valid(now, alive) {
		return HostedRequest{}, false
	}
	return r, true
}
