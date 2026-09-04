//go:build windows

package singleton

import (
	"errors"

	"golang.org/x/sys/windows"
)

// stillActive is GetExitCodeProcess's "the process has not exited" sentinel
// (STILL_ACTIVE, 259).
const stillActive = 259

// ProcessAlive reports whether pid names a process that is still running.
//
// It is deliberately conservative: only a pid that provably does not exist or
// has provably exited returns false; any other answer (access denied, an
// unexpected error) returns true so a caller never treats a live primary as
// dead because it could not inspect it.
//
// The case this exists for (issue #212): a process Windows has terminated but
// not torn down -- one thread stuck in a kernel wait -- still holds its handles
// (the singleton lock, its listening socket) and its pid. OpenProcess still
// succeeds on it, and GetExitCodeProcess reports the exit code rather than
// STILL_ACTIVE: exactly what .NET's Process.HasExited returns true for. Such
// a process can never answer again, so the lock it holds is dead weight.
func ProcessAlive(pid int) bool {
	if pid <= 0 {
		return false
	}
	h, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, uint32(pid))
	if err != nil {
		// ERROR_INVALID_PARAMETER is what OpenProcess returns for a pid that
		// does not exist at all. Anything else (ERROR_ACCESS_DENIED for another
		// user's process, for instance) is "unknown" -> assume alive.
		return !errors.Is(err, windows.ERROR_INVALID_PARAMETER)
	}
	defer windows.CloseHandle(h)
	var code uint32
	if err := windows.GetExitCodeProcess(h, &code); err != nil {
		return true
	}
	return code == stillActive
}
