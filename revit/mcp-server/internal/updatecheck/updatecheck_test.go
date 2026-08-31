package updatecheck

import (
	"bytes"
	"context"
	"log"
	"net"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/singleton"
)

func testLogger() *log.Logger {
	return log.New(&bytes.Buffer{}, "", 0)
}

func TestCheckLatestRelease(t *testing.T) {
	cases := []struct {
		name    string
		status  int
		body    string
		wantTag string
		wantErr bool
	}{
		{
			name:    "success",
			status:  http.StatusOK,
			body:    `{"tag_name": "v1.2.3", "name": "Release 1.2.3", "draft": false}`,
			wantTag: "v1.2.3",
		},
		{
			name:    "rate limited 403",
			status:  http.StatusForbidden,
			body:    `{"message": "API rate limit exceeded"}`,
			wantErr: true,
		},
		{
			name:    "not found 404",
			status:  http.StatusNotFound,
			body:    `{"message": "Not Found"}`,
			wantErr: true,
		},
		{
			// A non-200 response whose body nonetheless has a well-formed
			// tag_name field (some proxies/error pages could plausibly
			// produce this) must still be rejected on status alone — proves
			// the status check is independent of, not masked by, the
			// missing-tag_name check below.
			name:    "non-200 with well-formed tag_name in body",
			status:  http.StatusForbidden,
			body:    `{"tag_name": "v1.2.3"}`,
			wantErr: true,
		},
		{
			name:    "malformed json",
			status:  http.StatusOK,
			body:    `not json`,
			wantErr: true,
		},
		{
			name:    "missing tag_name field",
			status:  http.StatusOK,
			body:    `{"name": "Release without a tag", "draft": false}`,
			wantErr: true,
		},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.WriteHeader(c.status)
				w.Write([]byte(c.body))
			}))
			defer server.Close()

			client := server.Client()
			tag, err := checkLatestReleaseAt(context.Background(), client, server.URL, "eichler-ai/connectors", "1.0.0")
			if c.wantErr {
				if err == nil {
					t.Fatalf("checkLatestReleaseAt() = %q, nil; want an error", tag)
				}
				return
			}
			if err != nil {
				t.Fatalf("checkLatestReleaseAt() unexpected error: %v", err)
			}
			if tag != c.wantTag {
				t.Errorf("checkLatestReleaseAt() = %q, want %q", tag, c.wantTag)
			}
		})
	}
}

func TestCheckLatestReleaseSendsNonEmptyUserAgent(t *testing.T) {
	var gotUA string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotUA = r.Header.Get("User-Agent")
		w.WriteHeader(http.StatusOK)
		w.Write([]byte(`{"tag_name": "v1.2.3"}`))
	}))
	defer server.Close()

	client := server.Client()
	if _, err := checkLatestReleaseAt(context.Background(), client, server.URL, "eichler-ai/connectors", "1.0.0"); err != nil {
		t.Fatalf("checkLatestReleaseAt: %v", err)
	}
	if gotUA == "" {
		t.Fatalf("request reached the server with an empty User-Agent header; GitHub's real API rejects this")
	}
}

func TestCheckLatestReleaseHitsExpectedPath(t *testing.T) {
	var gotPath string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotPath = r.URL.Path
		w.WriteHeader(http.StatusOK)
		w.Write([]byte(`{"tag_name": "v9.9.9"}`))
	}))
	defer server.Close()

	client := server.Client()
	if _, err := checkLatestReleaseAt(context.Background(), client, server.URL, "eichler-ai/connectors", "1.0.0"); err != nil {
		t.Fatalf("checkLatestReleaseAt: %v", err)
	}
	want := "/repos/eichler-ai/connectors/releases/latest"
	if gotPath != want {
		t.Errorf("path = %q, want %q", gotPath, want)
	}
}

// TestCheckLatestReleasePublicWrapperUsesRealGitHubHost pins that the
// exported CheckLatestRelease (unlike the test-only checkLatestReleaseAt
// seam) always targets the real githubAPIBase — a production call must
// never be redirectable.
func TestCheckLatestReleasePublicWrapperUsesRealGitHubHost(t *testing.T) {
	if githubAPIBase != "https://api.github.com" {
		t.Fatalf("githubAPIBase = %q, want the real GitHub API host", githubAPIBase)
	}
}

func writeBrokerJSON(t *testing.T, dir string, info singleton.BrokerInfo) {
	t.Helper()
	if err := singleton.WriteBrokerJSON(dir, info); err != nil {
		t.Fatalf("WriteBrokerJSON: %v", err)
	}
}

func TestCheckAndUpdateBrokerJSONUpdatesOnlyLatestAvailableVersion(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		w.Write([]byte(`{"tag_name": "v2.0.0"}`))
	}))
	defer server.Close()

	dir := t.TempDir()
	started := time.Now().UTC().Truncate(time.Second)
	original := singleton.BrokerInfo{
		Host:      "127.0.0.1",
		Port:      54321,
		PID:       4242,
		StartedAt: started,
		Token:     "s3cr3t-token-value",
		Version:   "1.0.0",
	}
	writeBrokerJSON(t, dir, original)

	checkAndUpdateBrokerJSONAt(context.Background(), server.Client(), server.URL, dir, "1.0.0", testLogger())

	got, err := singleton.ReadBrokerJSON(dir)
	if err != nil {
		t.Fatalf("ReadBrokerJSON: %v", err)
	}
	if got.LatestAvailableVersion != "v2.0.0" {
		t.Errorf("LatestAvailableVersion = %q, want %q", got.LatestAvailableVersion, "v2.0.0")
	}
	if got.Host != original.Host || got.Port != original.Port || got.PID != original.PID ||
		got.Token != original.Token || got.Version != original.Version {
		t.Errorf("check-and-update clobbered an unrelated field: got %+v, want fields to match %+v (except LatestAvailableVersion)", got, original)
	}
	if !got.StartedAt.Equal(original.StartedAt) {
		t.Errorf("StartedAt = %v, want %v", got.StartedAt, original.StartedAt)
	}
}

func TestCheckAndUpdateBrokerJSONLeavesExistingValueOnFailure(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusInternalServerError)
		w.Write([]byte(`{"message": "internal error"}`))
	}))
	defer server.Close()

	dir := t.TempDir()
	original := singleton.BrokerInfo{
		Host:                   "127.0.0.1",
		Port:                   54321,
		PID:                    4242,
		StartedAt:              time.Now().UTC().Truncate(time.Second),
		Token:                  "s3cr3t-token-value",
		Version:                "1.0.0",
		LatestAvailableVersion: "v1.5.0",
	}
	writeBrokerJSON(t, dir, original)

	checkAndUpdateBrokerJSONAt(context.Background(), server.Client(), server.URL, dir, "1.0.0", testLogger())

	got, err := singleton.ReadBrokerJSON(dir)
	if err != nil {
		t.Fatalf("ReadBrokerJSON: %v", err)
	}
	if got.LatestAvailableVersion != "v1.5.0" {
		t.Errorf("a failed check changed LatestAvailableVersion: got %q, want unchanged %q", got.LatestAvailableVersion, "v1.5.0")
	}
}

func TestCheckAndUpdateBrokerJSONNeverPanicsOnMalformedJSON(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		w.Write([]byte(`not json`))
	}))
	defer server.Close()

	dir := t.TempDir()
	writeBrokerJSON(t, dir, singleton.BrokerInfo{Host: "h", Port: 1, PID: 1, StartedAt: time.Now().UTC(), Token: "t"})

	assertNoPanic(t, func() {
		checkAndUpdateBrokerJSONAt(context.Background(), server.Client(), server.URL, dir, "1.0.0", testLogger())
	})
}

func TestCheckAndUpdateBrokerJSONNeverPanicsOnConnectionRefused(t *testing.T) {
	// Bind a listener and immediately close it: the resulting address
	// refuses connections deterministically, standing in for a broken
	// proxy/no-network condition without relying on external DNS.
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("net.Listen: %v", err)
	}
	closedAddr := ln.Addr().String()
	ln.Close()

	dir := t.TempDir()
	writeBrokerJSON(t, dir, singleton.BrokerInfo{Host: "h", Port: 1, PID: 1, StartedAt: time.Now().UTC(), Token: "t"})

	client := &http.Client{Timeout: 2 * time.Second}
	assertNoPanic(t, func() {
		checkAndUpdateBrokerJSONAt(context.Background(), client, "http://"+closedAddr, dir, "1.0.0", testLogger())
	})
}

func assertNoPanic(t *testing.T, f func()) {
	t.Helper()
	defer func() {
		if r := recover(); r != nil {
			t.Fatalf("panicked: %v", r)
		}
	}()
	f()
}
