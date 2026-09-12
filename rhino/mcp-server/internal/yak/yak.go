// Package yak drives Rhino's bundled Yak CLI (the first-party package manager,
// rhino/docs/PRD.md §10/§15) to search, list, install and uninstall Grasshopper
// and Rhino plug-ins on the user's behalf. Yak is a standalone CLI that operates
// on the per-user package folder Rhino scans at startup, so these commands need
// no running Rhino instance; installing or removing a package takes effect the
// next time Rhino starts.
package yak

import (
	"bytes"
	"context"
	"fmt"
	"os"
	"os/exec"
	"regexp"
	"runtime"
	"strings"
	"time"
)

// Package is one package as `yak search`/`yak list` reports it.
type Package struct {
	Name    string `json:"name"`
	Version string `json:"version"`
}

// Client runs a located yak executable.
type Client struct {
	exe string
	// run is the command runner, swappable in tests. It returns combined stdout,
	// stderr, and any process error.
	run func(ctx context.Context, exe string, args ...string) (stdout, stderr string, err error)
}

// New wraps a yak executable path.
func New(exe string) *Client { return &Client{exe: exe, run: runCommand} }

// Locate finds the yak CLI: RHINO_YAK_PATH wins when set (and must exist),
// otherwise the platform's default Rhino 8 install location. A non-standard
// install with no override yields a clear error naming where it looked.
func Locate() (string, error) {
	if p := os.Getenv("RHINO_YAK_PATH"); p != "" {
		if fileExists(p) {
			return p, nil
		}
		return "", fmt.Errorf("RHINO_YAK_PATH is set to %q but no file is there", p)
	}
	candidates := defaultYakPaths()
	for _, c := range candidates {
		if fileExists(c) {
			return c, nil
		}
	}
	return "", fmt.Errorf("could not find Rhino's yak CLI (looked in %s); set RHINO_YAK_PATH to its full path", strings.Join(candidates, ", "))
}

func defaultYakPaths() []string {
	switch runtime.GOOS {
	case "darwin":
		return []string{"/Applications/Rhino 8.app/Contents/Resources/bin/yak"}
	case "windows":
		return []string{`C:\Program Files\Rhino 8\System\Yak.exe`}
	default:
		return nil
	}
}

func fileExists(p string) bool {
	info, err := os.Stat(p)
	return err == nil && !info.IsDir()
}

// Search runs `yak search [--prerelease] <query>` and returns the matches.
func (c *Client) Search(ctx context.Context, query string, prerelease bool) ([]Package, error) {
	args := []string{"search"}
	if prerelease {
		args = append(args, "--prerelease")
	}
	if query != "" {
		args = append(args, query)
	}
	stdout, stderr, err := c.exec(ctx, 30*time.Second, args...)
	if err != nil {
		return nil, cliError("search", stdout, stderr, err)
	}
	return parsePackageLines(stdout), nil
}

// List runs `yak list` and returns the package directory and installed packages.
func (c *Client) List(ctx context.Context) (dir string, pkgs []Package, err error) {
	stdout, stderr, err := c.exec(ctx, 30*time.Second, "list")
	if err != nil {
		return "", nil, cliError("list", stdout, stderr, err)
	}
	dir, pkgs = parseList(stdout)
	return dir, pkgs, nil
}

// Install runs `yak install <name> [<version>]`. version is optional (latest when empty).
func (c *Client) Install(ctx context.Context, name, version string) (output string, err error) {
	args := []string{"install", name}
	if version != "" {
		args = append(args, version)
	}
	stdout, stderr, err := c.exec(ctx, 5*time.Minute, args...)
	out := strings.TrimSpace(stdout + stderr)
	if err != nil {
		return out, cliError("install", stdout, stderr, err)
	}
	return out, nil
}

// Uninstall runs `yak uninstall <name>`.
func (c *Client) Uninstall(ctx context.Context, name string) (output string, err error) {
	stdout, stderr, err := c.exec(ctx, 60*time.Second, "uninstall", name)
	out := strings.TrimSpace(stdout + stderr)
	if err != nil {
		return out, cliError("uninstall", stdout, stderr, err)
	}
	return out, nil
}

func (c *Client) exec(ctx context.Context, timeout time.Duration, args ...string) (string, string, error) {
	cctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	return c.run(cctx, c.exe, args...)
}

func runCommand(ctx context.Context, exe string, args ...string) (string, string, error) {
	cmd := exec.CommandContext(ctx, exe, args...)
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	err := cmd.Run()
	return stdout.String(), stderr.String(), err
}

func cliError(command, stdout, stderr string, err error) error {
	msg := strings.TrimSpace(stderr)
	if msg == "" {
		msg = strings.TrimSpace(stdout)
	}
	if msg == "" {
		msg = err.Error()
	}
	return fmt.Errorf("yak %s failed: %s", command, msg)
}

// packageLine matches "Name (version)" as yak prints each result. The version can
// carry build metadata (e.g. "1.2609.10+19753"), so it is anything up to the ")".
var packageLine = regexp.MustCompile(`^(.+?)\s+\(([^)]+)\)\s*$`)

// parsePackageLines turns yak's "Name (version)" lines into packages, skipping blanks.
func parsePackageLines(s string) []Package {
	var out []Package
	for _, line := range strings.Split(s, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		if m := packageLine.FindStringSubmatch(line); m != nil {
			out = append(out, Package{Name: strings.TrimSpace(m[1]), Version: strings.TrimSpace(m[2])})
		}
	}
	return out
}

// parseList reads `yak list` output: a "Package directory: <dir>" header followed
// by "Name (version)" lines.
func parseList(s string) (dir string, pkgs []Package) {
	for _, line := range strings.Split(s, "\n") {
		trimmed := strings.TrimSpace(line)
		if rest, ok := strings.CutPrefix(trimmed, "Package directory:"); ok {
			dir = strings.TrimSpace(rest)
			continue
		}
	}
	return dir, parsePackageLines(s)
}
