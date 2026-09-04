package singleton

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
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
//   - broker.lock            generation 0, the lock every process tries first
//   - broker.lock.1 ... .N   generations a process falls back to, in order,
//                            when every lower one is held by an exited process
//   - broker.election.lock   the election mutex: held for the microseconds it
//                            takes to try a lock and record the holder's pid
//
// The invariant that makes skipping a held lock safe: a lock is only ever
// acquired under the election mutex, and its holder's pid is written into the
// lock file before the mutex is released. So while a process holds the mutex,
// every held lock's recorded pid is authoritative -- a live holder has always
// finished recording itself -- and a lock whose recorded pid has exited can
// never belong to a live process. broker.json plays no part in the decision;
// it stays what it was, the primary's advertisement of its port and token.
//
// A lock file written by a build that predates this scheme records no pid and
// is treated as held by a live process (never skipped), so mixed versions stay
// safe; the price is that a corpse from such a build is not recovered.
//
// Discovery is unchanged: whoever is primary writes broker.json with its own
// pid and port, and secondaries and the add-in follow that file. A primary
// that wins generation 0 removes the higher generation files it can lock (the
// corpse that forced them has been rebooted away), so the set never grows
// past what one episode left.

const (
	baseLockFile     = "broker.lock"
	electionLockFile = "broker.election.lock"

	// MaxLockGenerations bounds the fallback: more than this many dead holders
	// at once is not a state worth handling automatically.
	MaxLockGenerations = 8

	// holderOffset is where a lock's holder records its pid. LockFileEx locks
	// byte 0 (see lock_windows.go); a read that overlaps a byte another handle
	// holds exclusively fails on Windows, so the pid lives past it.
	holderOffset = 1
	holderWidth  = 20
)

// electionMutexWait bounds how long Elect keeps retrying the election mutex
// before concluding an election is in progress elsewhere. The mutex is held
// for microseconds by a live process, so contention clears at once; a mutex
// that stays held this long belongs to something that will not release it.
// A variable so tests can shorten it.
var electionMutexWait = 2 * time.Second

var (
	// ErrElectionInProgress: the election mutex could not be acquired within
	// electionMutexWait. Either many processes are starting at once (proceed
	// as secondary; the primary's broker.json is or will be there) or a
	// process died holding the mutex (then no election can ever complete and
	// the existing primary-unresponsive path is the fallback).
	ErrElectionInProgress = errors.New("singleton: the election mutex is held by another process")
	// ErrNoFreeGeneration: generation 0 and every generation up to
	// MaxLockGenerations are held by processes that have exited. Only a
	// reboot releases them.
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

// Election is the outcome of one Elect call.
type Election struct {
	// Lock is held only when Primary is true.
	Lock *Lock
	// Generation is the lock generation this process holds (0 = broker.lock);
	// meaningful only when Primary is true.
	Generation int
	// Primary is true when this process won a lock and must become the primary.
	Primary bool
	// SkippedDeadHolders lists the pids whose exited processes still held the
	// lower generations this election stepped over (issue #212's corpses), for
	// the log line. Empty for an ordinary win.
	SkippedDeadHolders []int
	// CleanedGenerations is how many stale generation files a generation-0
	// winner removed.
	CleanedGenerations int
}

// Elect runs one election turn for this process: under the election mutex it
// tries generation 0, 1, ... in order and takes the first free one, stepping
// over a held generation only when the pid recorded in it has exited (alive
// says whether a pid still runs; ProcessAlive in production). A held
// generation with a live -- or unrecorded -- holder makes this process a
// secondary. The only errors are ErrElectionInProgress, ErrNoFreeGeneration
// and I/O failures; ctx bounds the wait for the mutex.
func Elect(ctx context.Context, dir string, alive func(pid int) bool) (Election, error) {
	mutex, err := acquireElectionMutex(ctx, dir)
	if err != nil {
		return Election{}, err
	}
	defer mutex.Release()

	var skipped []int
	for n := 0; n <= MaxLockGenerations; n++ {
		path := GenerationLockPath(dir, n)
		l, got, err := AcquireLock(path)
		if err != nil {
			return Election{}, fmt.Errorf("singleton: trying lock generation %d: %w", n, err)
		}
		if got {
			if err := l.recordHolder(os.Getpid()); err != nil {
				l.Release()
				return Election{}, fmt.Errorf("singleton: recording holder of %s: %w", filepath.Base(path), err)
			}
			e := Election{Lock: l, Generation: n, Primary: true, SkippedDeadHolders: skipped}
			if n == 0 {
				// Holding generation 0 means whatever corpse forced the higher
				// generations is gone; nothing needs them any more. Still under
				// the mutex, so nobody is acquiring one concurrently.
				e.CleanedGenerations = removeStaleGenerations(dir)
			}
			return e, nil
		}
		holder, recorded := readHolder(path)
		if !recorded || alive(holder) {
			return Election{}, nil // a live process holds this generation: we proxy through it
		}
		skipped = append(skipped, holder)
	}
	return Election{}, ErrNoFreeGeneration
}

// acquireElectionMutex retries the (non-blocking) mutex lock for up to
// electionMutexWait, or until ctx ends.
func acquireElectionMutex(ctx context.Context, dir string) (*Lock, error) {
	path := filepath.Join(dir, electionLockFile)
	deadline := time.Now().Add(electionMutexWait)
	for {
		l, got, err := AcquireLock(path)
		if err != nil {
			return nil, fmt.Errorf("singleton: election mutex: %w", err)
		}
		if got {
			return l, nil
		}
		if time.Now().After(deadline) {
			return nil, ErrElectionInProgress
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(10 * time.Millisecond):
		}
	}
}

// recordHolder writes pid into the lock file this process holds, fixed-width
// so a shorter pid fully overwrites a longer predecessor's.
func (l *Lock) recordHolder(pid int) error {
	rec := fmt.Sprintf("%-*d\n", holderWidth-1, pid)
	if _, err := l.f.WriteAt([]byte(rec), holderOffset); err != nil {
		return err
	}
	return l.f.Sync()
}

// readHolder reads the pid recorded in the lock file at path. recorded is
// false when the file has no (parseable) record -- a lock held by a build
// that predates the record, or one whose holder has not written yet.
func readHolder(path string) (pid int, recorded bool) {
	f, err := os.Open(path)
	if err != nil {
		return 0, false
	}
	defer f.Close()
	buf := make([]byte, holderWidth)
	n, err := f.ReadAt(buf, holderOffset)
	if n == 0 && err != nil {
		return 0, false
	}
	s := strings.TrimSpace(strings.TrimRight(string(buf[:n]), "\x00"))
	pid, perr := strconv.Atoi(s)
	if perr != nil || pid <= 0 {
		return 0, false
	}
	return pid, true
}

// removeStaleGenerations deletes every generation lock file above 0 that is
// not held. Called under the election mutex by the generation-0 winner, so no
// other process is acquiring a generation meanwhile -- that mutex, not the
// order of remove and release, is what keeps a removal from racing an
// acquirer. The order differs per OS anyway: unix unlinks a file that is
// still open (and would, outside the mutex, hand two processes one name on
// different inodes -- so remove first there), while Windows refuses to delete
// a file this process still has open (Go opens without FILE_SHARE_DELETE) and
// needs the handle closed first. Held files fail to lock and stay.
func removeStaleGenerations(dir string) int {
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
		err = os.Remove(path) // unix: succeeds while held
		l.Release()
		if err != nil {
			err = os.Remove(path) // Windows: only after the handle is closed
		}
		if err == nil {
			removed++
		}
	}
	return removed
}
