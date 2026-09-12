// Package restart drives a Rhino restart so a freshly installed plug-in loads
// (PRD §10/§15): Rhino loads new packages only at startup. It quits the running
// Rhino and relaunches it, reopening the documents that were saved. The caller
// (the restart_rhino tool) is responsible for the unsaved-work guard and the
// confirmation gate; this package only performs an already-authorised restart.
package restart

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"runtime"
	"strings"
	"syscall"
	"time"
)

// AppBundle derives the macOS .app bundle from a running executable path, e.g.
// "/Applications/Rhino 8.app/Contents/MacOS/Rhinoceros" -> "/Applications/Rhino 8.app".
// Empty when the path is not inside a .app bundle.
func AppBundle(execPath string) string {
	const marker = ".app/"
	if i := strings.Index(execPath, marker); i >= 0 {
		return execPath[:i+len(".app")]
	}
	if strings.HasSuffix(execPath, ".app") {
		return execPath
	}
	return ""
}

// OpenArgs builds the `open` arguments to relaunch the app bundle, reopening the
// given saved document paths (an empty list just relaunches the app).
func OpenArgs(bundle string, reopenPaths []string) []string {
	args := []string{"-a", bundle}
	return append(args, reopenPaths...)
}

// Runner performs the side effects, swappable in tests.
type Runner struct {
	// ExecutablePath returns the running executable for a pid.
	ExecutablePath func(pid int) (string, error)
	// Kill terminates the process.
	Kill func(pid int) error
	// Alive reports whether the process is still running.
	Alive func(pid int) bool
	// Open relaunches via the platform launcher (`open` on macOS).
	Open func(ctx context.Context, args []string) error
	// Sleep waits (so tests can make it instant).
	Sleep func(time.Duration)
}

// DefaultRunner is the real macOS runner.
func DefaultRunner() Runner {
	return Runner{
		ExecutablePath: macExecutablePath,
		Kill: func(pid int) error {
			p, err := os.FindProcess(pid)
			if err != nil {
				return err
			}
			return p.Signal(syscall.SIGKILL)
		},
		Alive: func(pid int) bool {
			p, err := os.FindProcess(pid)
			if err != nil {
				return false
			}
			return p.Signal(syscall.Signal(0)) == nil
		},
		Open:  func(ctx context.Context, args []string) error { return exec.CommandContext(ctx, "open", args...).Run() },
		Sleep: time.Sleep,
	}
}

// Restart quits the Rhino process pid and relaunches it, reopening reopenPaths.
// macOS only in v1; on other platforms it returns an error telling the caller to
// restart Rhino manually. Returns a human-readable note on success.
func Restart(ctx context.Context, r Runner, pid int, reopenPaths []string) (string, error) {
	if runtime.GOOS != "darwin" {
		return "", fmt.Errorf("automated restart is supported on macOS only in v1; quit and reopen Rhino manually to load the plug-in")
	}
	execPath, err := r.ExecutablePath(pid)
	if err != nil {
		return "", fmt.Errorf("finding Rhino's executable for pid %d: %w", pid, err)
	}
	bundle := AppBundle(execPath)
	if bundle == "" {
		return "", fmt.Errorf("could not derive the Rhino app bundle from %q", execPath)
	}

	if err := r.Kill(pid); err != nil {
		return "", fmt.Errorf("quitting Rhino (pid %d): %w", pid, err)
	}
	// Wait for the process to actually exit before relaunching, so the new instance is not fighting the old.
	for i := 0; i < 50 && r.Alive(pid); i++ {
		r.Sleep(100 * time.Millisecond)
	}
	if r.Alive(pid) {
		return "", fmt.Errorf("Rhino (pid %d) did not exit after being asked to quit", pid)
	}

	if err := r.Open(ctx, OpenArgs(bundle, reopenPaths)); err != nil {
		return "", fmt.Errorf("relaunching %s: %w", bundle, err)
	}
	return fmt.Sprintf("restarted %s (was pid %d); reopening %d saved document(s). The new instance reconnects within a few seconds — call list_instances to see it.", bundle, pid, len(reopenPaths)), nil
}

// macExecutablePath reads the running executable for a pid via `ps`.
func macExecutablePath(pid int) (string, error) {
	out, err := exec.Command("ps", "-p", fmt.Sprint(pid), "-o", "comm=").Output()
	if err != nil {
		return "", err
	}
	path := strings.TrimSpace(string(out))
	if path == "" {
		return "", fmt.Errorf("no process with pid %d", pid)
	}
	return path, nil
}
