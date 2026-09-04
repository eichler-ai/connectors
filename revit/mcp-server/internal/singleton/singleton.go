// Package singleton implements the broker singleton lock-or-proxy pattern
// from PRD §05 ("Broker singleton & port contention") and the token minting
// it relies on from PRD §10 ("Security model"): every broker process
// attempts an exclusive OS-level lock; the winner becomes primary (binds the
// port, mints a token, writes broker.json); everyone else becomes secondary
// and proxies through the primary's TCP port, presenting that same token.
package singleton

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"time"
)

// BrokerInfo is the content of broker.json, per PRD §05/§10: the
// discoverable source of truth for "where am I" and "what's the current
// token," written by whichever process becomes primary.
type BrokerInfo struct {
	Host      string    `json:"host"`
	Port      int       `json:"port"`
	PID       int       `json:"pid"`
	StartedAt time.Time `json:"started_at"`
	Token     string    `json:"token"`

	// Version is the running broker's own version, mirroring
	// cmd/mcp-server/main.go's build-time -ldflags -X main.version=... var.
	Version string `json:"version,omitempty"`
	// Revision is the source revision the primary was built from
	// (buildinfo.Read().Revision), for offline diagnosis of which build holds
	// the singleton. Unlike Version -- "dev" for every local build -- this
	// distinguishes two dev builds from different checkouts.
	Revision string `json:"revision,omitempty"`
	// SchemaFingerprint is the primary's tool-contract hash
	// (mcpserver.ToolSchemaFingerprint): a secondary compares it to its own
	// before proxying, and refuses on a mismatch rather than serving the
	// primary's stale tool schema (issue #197). Empty in a broker.json written
	// by a primary that predates this field, which a newer secondary treats as
	// an incompatible (unknown) contract.
	SchemaFingerprint string `json:"schema_fingerprint,omitempty"`
	// LatestAvailableVersion is kept current by internal/updatecheck's background goroutine, which
	// periodically checks GitHub's latest release and caches the result here. Empty until that
	// goroutine's first successful check completes, and left at its previous value (never cleared)
	// on any check failure -- see internal/updatecheck's own doc comment for why.
	LatestAvailableVersion string `json:"latest_available_version,omitempty"`
	// Hosted is true when the primary is the long-lived hosted server
	// (mcp-server -hosted; self-update-architecture.md §5), the one process an
	// installer or a person can restart by name. A non-hosted primary steps down
	// for a hosted server that asks (see hosted.go); a hosted primary never
	// yields, and a second hosted server exits on seeing a live one here.
	Hosted bool `json:"hosted,omitempty"`
}

const brokerJSONFile = "broker.json"

// AppDataDir returns the platform-appropriate per-connector app-data root,
// per PRD §09's directory convention and CONVENTIONS.md's "App-data layout"
// (`.../Connectors/Revit/`). Written generically since the broker itself may
// run on macOS in remote mode (PRD §04): Windows uses %LOCALAPPDATA%, since
// that's the convention the PRD names explicitly; every other OS falls back
// to os.UserConfigDir(), the stdlib's own per-platform app-config location.
func AppDataDir() (string, error) {
	var base string
	if runtime.GOOS == "windows" {
		base = os.Getenv("LOCALAPPDATA")
	}
	if base == "" {
		dir, err := os.UserConfigDir()
		if err != nil {
			return "", fmt.Errorf("singleton: resolving app-data directory: %w", err)
		}
		base = dir
	}
	return filepath.Join(base, "Connectors", "Revit"), nil
}

// GenerateToken mints a fresh random auth token, per PRD §10: "the broker —
// not the add-in — generates a random token whenever it wins the singleton
// lock and becomes primary."
func GenerateToken() (string, error) {
	buf := make([]byte, 32)
	if _, err := rand.Read(buf); err != nil {
		return "", fmt.Errorf("singleton: generating auth token: %w", err)
	}
	return hex.EncodeToString(buf), nil
}

// ValidateToken reports whether presented matches expected, using a
// constant-time comparison. An empty expected token never validates —
// there's no legitimate state where the primary has no token of its own.
func ValidateToken(expected, presented string) bool {
	if expected == "" || presented == "" {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(expected), []byte(presented)) == 1
}

// WriteBrokerJSON writes info to <dir>/broker.json, creating dir if needed.
// The write is atomic (write to a temp file, then rename) so a concurrent
// reader never observes a partial file.
func WriteBrokerJSON(dir string, info BrokerInfo) error {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return fmt.Errorf("singleton: creating app-data directory %q: %w", dir, err)
	}
	b, err := json.MarshalIndent(info, "", "  ")
	if err != nil {
		return fmt.Errorf("singleton: encoding broker.json: %w", err)
	}
	final := filepath.Join(dir, brokerJSONFile)
	tmp := final + ".tmp"
	if err := os.WriteFile(tmp, b, 0o600); err != nil {
		return fmt.Errorf("singleton: writing %q: %w", tmp, err)
	}
	if err := os.Rename(tmp, final); err != nil {
		return fmt.Errorf("singleton: renaming %q to %q: %w", tmp, final, err)
	}
	return nil
}

// ReadBrokerJSON reads and decodes <dir>/broker.json.
func ReadBrokerJSON(dir string) (BrokerInfo, error) {
	path := filepath.Join(dir, brokerJSONFile)
	b, err := os.ReadFile(path)
	if err != nil {
		return BrokerInfo{}, fmt.Errorf("singleton: reading %q: %w", path, err)
	}
	var info BrokerInfo
	if err := json.Unmarshal(b, &info); err != nil {
		return BrokerInfo{}, fmt.Errorf("singleton: decoding %q: %w", path, err)
	}
	return info, nil
}
