//go:build windows

package singleton

import (
	"fmt"
	"os"

	"golang.org/x/sys/windows"
)

// Lock is a handle on the exclusive OS-level lock file described in PRD §05
// (Windows: LockFileEx). Only the process that successfully acquires it
// (AcquireLock returning primary=true) holds a non-nil *Lock.
type Lock struct {
	f *os.File
}

// AcquireLock attempts to take an exclusive, non-blocking lock on path via
// LockFileEx, creating the file if necessary. primary=true means this
// process won the lock and should become the broker's primary; primary=false
// means another process already holds it and this process should become
// secondary.
func AcquireLock(path string) (lock *Lock, primary bool, err error) {
	f, err := os.OpenFile(path, os.O_CREATE|os.O_RDWR, 0o644)
	if err != nil {
		return nil, false, fmt.Errorf("singleton: opening lock file %q: %w", path, err)
	}

	h := windows.Handle(f.Fd())
	var overlapped windows.Overlapped
	err = windows.LockFileEx(h, windows.LOCKFILE_EXCLUSIVE_LOCK|windows.LOCKFILE_FAIL_IMMEDIATELY, 0, 1, 0, &overlapped)
	if err != nil {
		f.Close()
		if err == windows.ERROR_LOCK_VIOLATION || err == windows.ERROR_IO_PENDING {
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
	h := windows.Handle(l.f.Fd())
	var overlapped windows.Overlapped
	if err := windows.UnlockFileEx(h, 0, 1, 0, &overlapped); err != nil {
		l.f.Close()
		return fmt.Errorf("singleton: unlocking: %w", err)
	}
	return l.f.Close()
}
