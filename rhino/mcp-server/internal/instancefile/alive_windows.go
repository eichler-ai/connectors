//go:build windows

package instancefile

import (
	"golang.org/x/sys/windows"
)

// ProcessAlive: an OpenProcess that succeeds and reports STILL_ACTIVE. The
// same check the Revit server's singleton uses for a dead lock holder (issue #212).
func ProcessAlive(pid int) bool {
	h, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, uint32(pid))
	if err != nil {
		return false
	}
	defer windows.CloseHandle(h)
	var code uint32
	if err := windows.GetExitCodeProcess(h, &code); err != nil {
		return false
	}
	return code == 259 // STILL_ACTIVE
}
