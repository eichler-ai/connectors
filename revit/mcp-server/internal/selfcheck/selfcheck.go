// Package selfcheck decides when a running server process should step aside
// because the install on disk has moved on without it (issue #201).
//
// The installer cannot replace a running image; it parks the new exe and
// records the new release in installed-version.json. Until every old process
// exits, clients keep proxying through an old primary, and "restart your MCP
// client" turned out to be unreliable advice (a window close does not end the
// servers; the primary may belong to a different client). So the processes
// themselves notice: a server that is not the release the marker names exits
// cleanly, its client respawns the exe on disk, and whoever wins the lock next
// is by construction current.
package selfcheck

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// Marker is the subset of install.ps1's installed-version.json this package
// reads.
type Marker struct {
	Version string `json:"version"`
}

// ReadMarkerVersion returns the release tag installed-version.json names, read
// from the directory of this executable (install.ps1 keeps the marker beside
// mcp-server.exe; a process running from a parked .old-* image is in that same
// directory). "" when there is no marker or it cannot be read — which is what a
// dev checkout, a remote-mode Mac broker, or an install by hand looks like, and
// means "no opinion", never "evict".
func ReadMarkerVersion() string {
	exe, err := os.Executable()
	if err != nil {
		return ""
	}
	return readMarkerVersion(filepath.Join(filepath.Dir(exe), "installed-version.json"))
}

func readMarkerVersion(path string) string {
	b, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	// Windows PowerShell's Out-File -Encoding utf8 writes a BOM (found live, #200).
	b = bytes.TrimPrefix(b, []byte{0xEF, 0xBB, 0xBF})
	var m Marker
	if err := json.Unmarshal(b, &m); err != nil {
		return ""
	}
	return m.Version
}

// ShouldEvict reports whether a process built as ownVersion should exit
// because the install on disk is markerVersion. Both must be release tags:
// a "dev" build never evicts (it is not a release and is usually being
// tested against exactly this install), and an empty marker is no opinion.
// Any DIFFERENCE evicts, not just "older": following the disk is the point,
// and a deliberate downgrade install should take effect the same way.
func ShouldEvict(ownVersion, markerVersion string) bool {
	own := normalize(ownVersion)
	disk := normalize(markerVersion)
	if own == "" || disk == "" || own == "dev" || disk == "dev" {
		return false
	}
	return own != disk
}

func normalize(tag string) string {
	return strings.TrimPrefix(strings.ToLower(strings.TrimSpace(tag)), "v")
}

// ImageStamp identifies the executable file this process was started from, as
// it was at start: size and modification time of the file at os.Executable().
type ImageStamp struct {
	Path    string
	Size    int64
	ModTime time.Time
	ok      bool
}

// StampExecutable records the executable as it is right now. Call once at
// process start; ExecutableReplaced compares against it later.
func StampExecutable() ImageStamp {
	exe, err := os.Executable()
	if err != nil {
		return ImageStamp{}
	}
	fi, err := os.Stat(exe)
	if err != nil {
		return ImageStamp{Path: exe}
	}
	return ImageStamp{Path: exe, Size: fi.Size(), ModTime: fi.ModTime(), ok: true}
}

// ExecutableReplaced reports whether the file at the stamped path is no longer
// the one this process started from (size or mtime differ). This is the second
// half of the eviction test: the marker can name a newer release while the exe
// on disk is still the old one -- install.ps1 bumps the marker when the add-ins
// deploy even if the server swap is 'pending' behind a locked .old -- and
// evicting then would respawn the same old exe into an immediate re-eviction,
// forever, with no server at all. Only a replaced file means a fresh start
// would run something different. A process started from a parked .old-* image
// (its path never changes) therefore never self-evicts; the client that owns it
// restarts it eventually, and every fresh process is the installed exe.
func ExecutableReplaced(at ImageStamp) bool {
	if !at.ok {
		return false
	}
	fi, err := os.Stat(at.Path)
	if err != nil {
		return false // missing mid-swap: nothing to respawn into yet
	}
	return fi.Size() != at.Size || !fi.ModTime().Equal(at.ModTime)
}
