package singleton

import (
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
