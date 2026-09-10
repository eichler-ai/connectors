//go:build !windows

package instancefile

import (
	"errors"
	"os"
	"syscall"
)

// ProcessAlive: kill(pid, 0) succeeds (or fails with EPERM, which still means
// the process exists) for a live process; ESRCH means it is gone. A zombie
// still "exists" to kill(2); the plug-in's file is reclaimed once the parent
// reaps it, which is the same outcome one scan later.
func ProcessAlive(pid int) bool {
	p, err := os.FindProcess(pid)
	if err != nil {
		return false
	}
	err = p.Signal(syscall.Signal(0))
	if err == nil {
		return true
	}
	return errors.Is(err, syscall.EPERM)
}
