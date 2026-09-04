package singleton

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"time"
)

// Issue #212: a primary that Windows terminated but could not tear down (one
// thread stuck in a kernel wait) keeps broker.lock, its listening port and
// its pid until the machine reboots. The live case: Claude Desktop spawns a
// server at startup and kills it ~1 s later; if that process had just won the
// lock, every later server finds the lock held and the primary unresponsive,
// and the connector is dead until a reboot with nothing in the client to say
// why. The lock file cannot be renamed, deleted or unlocked from outside while
// the corpse holds its handle, so recovery means locking a DIFFERENT file:
// lock generations.
//
//   - broker.lock            generation 0, the base lock every process races
//   - broker.lock.1 ... .N   generations a takeover falls back to, in order
//   - broker.takeover.lock   held only for the milliseconds a takeover runs, so
//                            two candidates never both proceed
//
// Discovery is unchanged: whoever is primary writes broker.json with its own
// pid and port, and secondaries and the add-in follow that file. A base-lock
// primary (after the reboot that finally freed it) removes the generation
// files it can lock, so the set never grows past what one zombie episode left.

const (
	baseLockFile     = "broker.lock"
	takeoverLockFile = "broker.takeover.lock"

	// MaxLockGenerations bounds the fallback: more than this many dead holders
	// at once is not a state worth handling automatically.
	MaxLockGenerations = 8
)

var (
	// ErrTakeoverInProgress: another process holds broker.takeover.lock right
	// now. It will write broker.json within milliseconds; re-read it and proxy.
	ErrTakeoverInProgress = errors.New("singleton: another process is taking over the primary role")
	// ErrHolderAlive: by the time the takeover lock was held, broker.json named
	// a running process -- a live primary won a lock in the meantime. Proxy to it.
	ErrHolderAlive = errors.New("singleton: broker.json now names a running primary")
	// ErrNoFreeGeneration: the base lock and every generation up to
	// MaxLockGenerations are held by processes that have exited. Only a reboot
	// releases them.
	ErrNoFreeGeneration = errors.New("singleton: every lock generation is held by a process that has exited")
)

// LockPath is the base (generation 0) lock file under dir.
func LockPath(dir string) string { return filepath.Join(dir, baseLockFile) }

// GenerationLockPath is the lock file for generation n under dir; n == 0 is
// the base lock.
func GenerationLockPath(dir string, n int) string {
	if n == 0 {
		return LockPath(dir)
	}
	return filepath.Join(dir, baseLockFile+"."+strconv.Itoa(n))
}

// TakeOver claims the primary role when the process broker.json names has
// exited yet the base lock is still held. It returns the lock this process
// now holds and its generation (0 if the base lock turned out to be free after
// all, so the caller treats that exactly like an ordinary win).
//
// deadPID is the pid the caller found in broker.json and judged dead; alive is
// the liveness probe (ProcessAlive in production, a stub in tests); settle is
// how long to wait under the takeover lock before re-reading broker.json -- a
// live primary that has just won a lock writes broker.json within
// milliseconds, so this closes the window in which a stale file would be
// mistaken for a dead primary. The three sentinel errors above are the
// "do not take over" outcomes; anything else is an I/O failure.
func TakeOver(dir string, deadPID int, alive func(pid int) bool, settle time.Duration) (*Lock, int, error) {
	tl, got, err := AcquireLock(filepath.Join(dir, takeoverLockFile))
	if err != nil {
		return nil, 0, err
	}
	if !got {
		return nil, 0, ErrTakeoverInProgress
	}
	defer tl.Release()

	if settle > 0 {
		time.Sleep(settle)
	}
	if info, err := ReadBrokerJSON(dir); err == nil && info.PID != 0 && alive(info.PID) {
		return nil, 0, ErrHolderAlive
	}
	if alive(deadPID) {
		// The caller's judgement is re-checked under the lock so a probe that
		// was wrong once (the pid was mid-exit) cannot seed a second primary.
		return nil, 0, ErrHolderAlive
	}

	// Under the takeover lock a held generation can only belong to a process
	// that finished its own takeover -- and such a process wrote broker.json
	// with its pid, which the check above found not alive -- or to the
	// original corpse. Either way: dead, move on to the next generation.
	for n := 0; n <= MaxLockGenerations; n++ {
		l, got, err := AcquireLock(GenerationLockPath(dir, n))
		if err != nil {
			return nil, 0, fmt.Errorf("singleton: trying lock generation %d: %w", n, err)
		}
		if got {
			return l, n, nil
		}
	}
	return nil, 0, ErrNoFreeGeneration
}

// ReleaseStaleGenerations removes every generation lock file under dir that
// is no longer held, and reports how many it removed. Meant for a primary
// that holds the BASE lock: the base lock being free means the corpse that
// forced the generations is gone (rebooted away), so nothing needs them. A
// file that is still held (or that a concurrent process has open) is left
// alone -- the remove simply fails and that is fine.
func ReleaseStaleGenerations(dir string) int {
	removed := 0
	for n := 1; n <= MaxLockGenerations; n++ {
		path := GenerationLockPath(dir, n)
		if _, err := os.Stat(path); err != nil {
			continue
		}
		l, got, err := AcquireLock(path)
		if err != nil || !got {
			continue
		}
		l.Release()
		if err := os.Remove(path); err == nil {
			removed++
		}
	}
	return removed
}
