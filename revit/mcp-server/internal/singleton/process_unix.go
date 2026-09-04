//go:build unix

package singleton

import (
	"errors"
	"syscall"
)

// ProcessAlive reports whether pid names a process that is still running.
// kill(pid, 0) delivers nothing and only checks existence: ESRCH means no such
// process; EPERM means it exists but belongs to someone else, which counts as
// alive. Conservative like the Windows variant: only a provable "gone" is false.
//
// On unix an exited process releases its flock with its file descriptors, so
// the dead-holder case this backs (issue #212) is a Windows phenomenon; the
// probe exists here so the takeover logic is portable and testable.
func ProcessAlive(pid int) bool {
	if pid <= 0 {
		return false
	}
	err := syscall.Kill(pid, 0)
	if err == nil {
		return true
	}
	return !errors.Is(err, syscall.ESRCH)
}
