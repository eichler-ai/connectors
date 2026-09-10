// Package instancefile reads the instances/<pid>.json files the Rhino MCP
// Bridge writes (PRD §05, §13) and decides which of them are live. A file whose
// pid is not a running process is stale -- the plug-in deletes its own file on a
// clean unload, so a stale file means a crashed or killed Rhino -- and the
// scanner deletes it, so nothing accumulates across crashes.
package instancefile

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"
)

// SchemaVersion is the instance-file schema this server understands. A newer
// file is still read (unknown fields are ignored); an older one is refused.
const SchemaVersion = 1

// File is one instances/<pid>.json.
type File struct {
	Schema            int       `json:"schema"`
	InstanceID        string    `json:"instance_id"`
	PID               int       `json:"pid"`
	Port              int       `json:"port"`
	Token             string    `json:"token"`
	RhinoVersion      string    `json:"rhino_version"`
	Platform          string    `json:"platform"`
	BridgeVersion     string    `json:"bridge_version"`
	SchemaFingerprint string    `json:"schema_fingerprint"`
	StartedAt         time.Time `json:"started_at"`

	// Path is where it was read from; set by Scan, not part of the file.
	Path string `json:"-"`
}

// Validate reports the first structural problem, or nil.
func (f *File) Validate() error {
	switch {
	case f.Schema < SchemaVersion:
		return fmt.Errorf("schema %d is older than this server understands (%d)", f.Schema, SchemaVersion)
	case f.InstanceID == "":
		return errors.New("instance_id is empty")
	case f.PID <= 0:
		return fmt.Errorf("pid %d is not a process id", f.PID)
	case f.Port <= 0 || f.Port > 65535:
		return fmt.Errorf("port %d is not a TCP port", f.Port)
	case f.Token == "":
		return errors.New("token is empty")
	}
	return nil
}

// Read parses one file. The pid in the file name and the pid in the body must
// agree; a mismatch is a hand-edited or misplaced file and is refused.
func Read(path string) (*File, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var f File
	if err := json.Unmarshal(b, &f); err != nil {
		return nil, fmt.Errorf("parsing %s: %w", path, err)
	}
	if err := f.Validate(); err != nil {
		return nil, fmt.Errorf("%s: %w", path, err)
	}
	if want, ok := pidFromName(path); ok && want != f.PID {
		return nil, fmt.Errorf("%s: file is named for pid %d but declares pid %d", path, want, f.PID)
	}
	f.Path = path
	return &f, nil
}

func pidFromName(path string) (int, bool) {
	base := strings.TrimSuffix(filepath.Base(path), ".json")
	pid, err := strconv.Atoi(base)
	return pid, err == nil
}

// Alive reports whether pid is a running process. Injected so the scanner is
// testable with a fake; ProcessAlive is the real one.
type Alive func(pid int) bool

// ScanResult is one pass over the directory.
type ScanResult struct {
	Live    []*File
	Removed []string // stale files deleted this pass
	Skipped []string // unreadable or invalid files, left in place (with the reason)
}

// Scan lists the live instance files, deleting the ones whose process has
// exited. A missing directory is an empty result, not an error: no Rhino has
// ever run with the plug-in on this machine.
func Scan(dir string, alive Alive) (ScanResult, error) {
	var res ScanResult
	entries, err := os.ReadDir(dir)
	if errors.Is(err, os.ErrNotExist) {
		return res, nil
	}
	if err != nil {
		return res, err
	}
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		path := filepath.Join(dir, e.Name())
		pid, ok := pidFromName(path)
		if !ok || pid <= 0 {
			// Validated BEFORE the liveness probe: kill(0, 0) / kill(-1, 0) address a
			// process group or every process, not a pid (review of #281).
			res.Skipped = append(res.Skipped, path+": not named <pid>.json")
			continue
		}
		if !alive(pid) {
			if err := os.Remove(path); err == nil {
				res.Removed = append(res.Removed, path)
			} else {
				res.Skipped = append(res.Skipped, path+": stale but could not be removed: "+err.Error())
			}
			continue
		}
		f, err := Read(path)
		if err != nil {
			res.Skipped = append(res.Skipped, err.Error())
			continue
		}
		res.Live = append(res.Live, f)
	}
	sort.Slice(res.Live, func(i, j int) bool { return res.Live[i].PID < res.Live[j].PID })
	return res, nil
}
