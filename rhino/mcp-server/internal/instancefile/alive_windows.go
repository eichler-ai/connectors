//go:build windows

package instancefile

import (
	"errors"

	"golang.org/x/sys/windows"
)

// ProcessAlive: an OpenProcess that succeeds and reports STILL_ACTIVE. The
// same check the Revit server's singleton uses for a dead lock holder (issue
// #212). ACCESS_DENIED means the process EXISTS and we may not inspect it (an
// elevated Rhino seen from an unelevated server) -- that is alive, never a
// reason to delete its instance file (review of #281); only a provable exit
// counts, the same rule the Unix sibling applies to EPERM.
func ProcessAlive(pid int) bool {
	h, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, uint32(pid))
	if err != nil {
		return errors.Is(err, windows.ERROR_ACCESS_DENIED)
	}
	defer windows.CloseHandle(h)
	var code uint32
	if err := windows.GetExitCodeProcess(h, &code); err != nil {
		return false
	}
	return code == 259 // STILL_ACTIVE
}
