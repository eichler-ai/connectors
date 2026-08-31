package singleton

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestAcquireLockOnlyOneWinner(t *testing.T) {
	dir := t.TempDir()
	lockPath := filepath.Join(dir, "broker.lock")

	l1, primary1, err := AcquireLock(lockPath)
	if err != nil {
		t.Fatalf("AcquireLock 1: %v", err)
	}
	if !primary1 {
		t.Fatalf("first acquirer should be primary")
	}
	defer l1.Release()

	l2, primary2, err := AcquireLock(lockPath)
	if err != nil {
		t.Fatalf("AcquireLock 2: %v", err)
	}
	if primary2 {
		t.Fatalf("second acquirer should be secondary (lock already held)")
	}
	if l2 != nil {
		t.Fatalf("secondary should not receive a lock handle")
	}
}

func TestAcquireLockReleaseAllowsNewPrimary(t *testing.T) {
	dir := t.TempDir()
	lockPath := filepath.Join(dir, "broker.lock")

	l1, primary1, err := AcquireLock(lockPath)
	if err != nil || !primary1 {
		t.Fatalf("AcquireLock 1: primary=%v err=%v", primary1, err)
	}
	if err := l1.Release(); err != nil {
		t.Fatalf("Release: %v", err)
	}

	l2, primary2, err := AcquireLock(lockPath)
	if err != nil {
		t.Fatalf("AcquireLock 2: %v", err)
	}
	if !primary2 {
		t.Fatalf("after release, next acquirer should become primary")
	}
	defer l2.Release()
}

func TestGenerateTokenIsRandomAndNonEmpty(t *testing.T) {
	t1, err := GenerateToken()
	if err != nil {
		t.Fatalf("GenerateToken: %v", err)
	}
	t2, err := GenerateToken()
	if err != nil {
		t.Fatalf("GenerateToken: %v", err)
	}
	if t1 == "" || t2 == "" {
		t.Fatalf("token should not be empty")
	}
	if t1 == t2 {
		t.Fatalf("two calls to GenerateToken produced the same token")
	}
	if len(t1) < 32 {
		t.Fatalf("token %q looks too short to be a real secret", t1)
	}
}

func TestValidateToken(t *testing.T) {
	cases := []struct {
		name      string
		expected  string
		presented string
		want      bool
	}{
		{"match", "abc123", "abc123", true},
		{"mismatch", "abc123", "abc124", false},
		{"empty presented", "abc123", "", false},
		{"empty expected never matches", "", "", false},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := ValidateToken(c.expected, c.presented); got != c.want {
				t.Errorf("ValidateToken(%q, %q) = %v, want %v", c.expected, c.presented, got, c.want)
			}
		})
	}
}

func TestBrokerJSONRoundTrip(t *testing.T) {
	dir := t.TempDir()
	info := BrokerInfo{
		Host:      "127.0.0.1",
		Port:      54321,
		PID:       os.Getpid(),
		StartedAt: time.Now().UTC().Truncate(time.Second),
		Token:     "s3cr3t-token-value",
	}
	if err := WriteBrokerJSON(dir, info); err != nil {
		t.Fatalf("WriteBrokerJSON: %v", err)
	}

	got, err := ReadBrokerJSON(dir)
	if err != nil {
		t.Fatalf("ReadBrokerJSON: %v", err)
	}
	if got.Host != info.Host || got.Port != info.Port || got.PID != info.PID || got.Token != info.Token {
		t.Errorf("round trip mismatch: got %+v, want %+v", got, info)
	}
	if !got.StartedAt.Equal(info.StartedAt) {
		t.Errorf("StartedAt mismatch: got %v, want %v", got.StartedAt, info.StartedAt)
	}
}

func TestBrokerJSONRoundTripWithVersionFields(t *testing.T) {
	dir := t.TempDir()
	info := BrokerInfo{
		Host:                   "127.0.0.1",
		Port:                   54321,
		PID:                    os.Getpid(),
		StartedAt:              time.Now().UTC().Truncate(time.Second),
		Token:                  "s3cr3t-token-value",
		Version:                "1.2.3",
		LatestAvailableVersion: "1.3.0",
	}
	if err := WriteBrokerJSON(dir, info); err != nil {
		t.Fatalf("WriteBrokerJSON: %v", err)
	}

	got, err := ReadBrokerJSON(dir)
	if err != nil {
		t.Fatalf("ReadBrokerJSON: %v", err)
	}
	if got.Version != info.Version {
		t.Errorf("Version mismatch: got %q, want %q", got.Version, info.Version)
	}
	if got.LatestAvailableVersion != info.LatestAvailableVersion {
		t.Errorf("LatestAvailableVersion mismatch: got %q, want %q", got.LatestAvailableVersion, info.LatestAvailableVersion)
	}
}

// TestReadBrokerJSONBackwardCompatible ensures a broker.json written before
// Version/LatestAvailableVersion existed (e.g. by a not-yet-updated broker,
// or a stale marker left on disk) still decodes cleanly, with the new
// fields defaulting to the empty string.
func TestReadBrokerJSONBackwardCompatible(t *testing.T) {
	dir := t.TempDir()
	oldJSON := `{
		"host": "127.0.0.1",
		"port": 54321,
		"pid": 4242,
		"started_at": "2026-01-01T00:00:00Z",
		"token": "s3cr3t-token-value"
	}`
	if err := os.WriteFile(filepath.Join(dir, brokerJSONFile), []byte(oldJSON), 0o600); err != nil {
		t.Fatalf("writing old-shaped broker.json: %v", err)
	}

	got, err := ReadBrokerJSON(dir)
	if err != nil {
		t.Fatalf("ReadBrokerJSON: %v", err)
	}
	if got.Host != "127.0.0.1" || got.Port != 54321 || got.PID != 4242 || got.Token != "s3cr3t-token-value" {
		t.Errorf("decoded old-shaped broker.json unexpectedly: %+v", got)
	}
	if got.Version != "" {
		t.Errorf("Version = %q, want empty string for old-shaped broker.json", got.Version)
	}
	if got.LatestAvailableVersion != "" {
		t.Errorf("LatestAvailableVersion = %q, want empty string for old-shaped broker.json", got.LatestAvailableVersion)
	}
}

// TestWriteBrokerJSONOmitsEmptyVersionFields guards against omitempty
// silently not being respected: when Version/LatestAvailableVersion are
// left at their zero value (as today's un-migrated callers do), the
// on-disk JSON must not contain those keys at all, preserving the
// pre-existing on-disk shape.
func TestWriteBrokerJSONOmitsEmptyVersionFields(t *testing.T) {
	dir := t.TempDir()
	info := BrokerInfo{
		Host:      "127.0.0.1",
		Port:      54321,
		PID:       os.Getpid(),
		StartedAt: time.Now().UTC().Truncate(time.Second),
		Token:     "s3cr3t-token-value",
	}
	if err := WriteBrokerJSON(dir, info); err != nil {
		t.Fatalf("WriteBrokerJSON: %v", err)
	}

	b, err := os.ReadFile(filepath.Join(dir, brokerJSONFile))
	if err != nil {
		t.Fatalf("reading broker.json: %v", err)
	}

	var raw map[string]any
	if err := json.Unmarshal(b, &raw); err != nil {
		t.Fatalf("unmarshaling broker.json into map: %v", err)
	}
	if _, ok := raw["version"]; ok {
		t.Errorf("broker.json unexpectedly contains %q key when Version is empty: %s", "version", b)
	}
	if _, ok := raw["latest_available_version"]; ok {
		t.Errorf("broker.json unexpectedly contains %q key when LatestAvailableVersion is empty: %s", "latest_available_version", b)
	}
}

func TestReadBrokerJSONMissingFile(t *testing.T) {
	dir := t.TempDir()
	if _, err := ReadBrokerJSON(dir); err == nil {
		t.Fatalf("expected error reading missing broker.json")
	}
}

func TestAppDataDirNamespacedUnderConnectorsRevit(t *testing.T) {
	dir, err := AppDataDir()
	if err != nil {
		t.Fatalf("AppDataDir: %v", err)
	}
	want := filepath.Join("Connectors", "Revit")
	if filepath.Base(filepath.Dir(dir)) != "Connectors" || filepath.Base(dir) != "Revit" {
		t.Errorf("AppDataDir() = %q, want to end with %q", dir, want)
	}
}
