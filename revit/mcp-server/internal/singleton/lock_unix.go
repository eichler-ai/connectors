//go:build unix

package singleton

import (
	"errors"
	"fmt"
	"os"
	"syscall"
)

// Lock is a handle on the exclusive OS-level lock file described in PRD §05.
// Only the process that successfully acquires it (AcquireLock returning
// primary=true) holds a non-nil *Lock.
type Lock struct {
	f *os.File
}

// AcquireLock attempts to take an exclusive, non-blocking lock on path,
// creating it if necessary. primary=true means this process won the lock
// and should become the broker's primary; primary=false means another
// process already holds it and this process should become secondary
// (proxying through the primary instead of binding a port).
func AcquireLock(path string) (lock *Lock, primary bool, err error) {
	f, err := os.OpenFile(path, os.O_CREATE|os.O_RDWR, 0o644)
	if err != nil {
		return nil, false, fmt.Errorf("singleton: opening lock file %q: %w", path, err)
	}

	if err := syscall.Flock(int(f.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); err != nil {
		f.Close()
		if errors.Is(err, syscall.EWOULDBLOCK) {
			return nil, false, nil
		}
		return nil, false, fmt.Errorf("singleton: locking %q: %w", path, err)
	}

	return &Lock{f: f}, true, nil
}

// Release releases the lock and closes the underlying file handle.
func (l *Lock) Release() error {
	if l == nil || l.f == nil {
		return nil
	}
	if err := syscall.Flock(int(l.f.Fd()), syscall.LOCK_UN); err != nil {
		l.f.Close()
		return fmt.Errorf("singleton: unlocking: %w", err)
	}
	return l.f.Close()
}
