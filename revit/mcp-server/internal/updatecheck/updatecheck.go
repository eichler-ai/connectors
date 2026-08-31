// Package updatecheck implements the broker's own periodic GitHub
// latest-release check, per PRD §12: the broker (long-running, never
// file-locked) checks for a newer release in the background and caches the
// result in broker.json's LatestAvailableVersion field; the add-in never
// calls GitHub directly, it only reads that existing broker.json poll.
//
// This is the first outbound HTTP call either component of this project
// makes, so every failure mode here — no network, DNS failure, GitHub rate
// limiting, a malformed response — must degrade to "no update shown," never
// crash or block broker startup.
package updatecheck

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/singleton"
)

// CheckInterval is how often Run re-checks GitHub after its initial check.
const CheckInterval = 6 * time.Hour

// initialCheckDelay is how long Run waits after starting before its very
// first check — short, so a fresh broker doesn't wait a full CheckInterval
// to learn about an available update, but nonzero so the goroutine's own
// startup never does a synchronous network call inline in the caller.
const initialCheckDelay = 5 * time.Second

// githubAPIBase is the real GitHub API host. It's a var, not a const, only
// so tests can point CheckLatestRelease/CheckAndUpdateBrokerJSON at an
// httptest.Server instead — production code never changes it.
var githubAPIBase = "https://api.github.com"

// RepoSlug is the GitHub repository this broker checks for releases.
const RepoSlug = "eichler-ai/connectors"

// releaseResponse mirrors only the field of GitHub's release JSON this
// package cares about; deliberately not a full parse of the API shape.
type releaseResponse struct {
	TagName string `json:"tag_name"`
}

// CheckLatestRelease makes one GET request to GitHub's latest-release API
// for repoSlug (e.g. "eichler-ai/connectors") and returns the release's tag
// name. runningVersion is embedded in the User-Agent header — GitHub's API
// rejects requests with no User-Agent at all.
//
// Any non-200 response, a network error, malformed JSON, or a response
// missing tag_name is returned as an error; there is no partial-success
// case, since an empty or garbage tag written into broker.json would read
// to the add-in as either "no update" or a bogus version string.
func CheckLatestRelease(ctx context.Context, client *http.Client, repoSlug string, runningVersion string) (string, error) {
	return checkLatestReleaseAt(ctx, client, githubAPIBase, repoSlug, runningVersion)
}

// checkLatestReleaseAt is CheckLatestRelease with the API base URL as a
// parameter, so tests can point it at an httptest.Server; CheckLatestRelease
// is the only production caller, always passing githubAPIBase.
func checkLatestReleaseAt(ctx context.Context, client *http.Client, apiBase, repoSlug, runningVersion string) (string, error) {
	url := fmt.Sprintf("%s/repos/%s/releases/latest", apiBase, repoSlug)
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return "", fmt.Errorf("updatecheck: building request for %q: %w", url, err)
	}
	req.Header.Set("User-Agent", "eichler-connectors-revit-mcp-server/"+runningVersion)
	req.Header.Set("Accept", "application/vnd.github+json")

	resp, err := client.Do(req)
	if err != nil {
		return "", fmt.Errorf("updatecheck: requesting %q: %w", url, err)
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return "", fmt.Errorf("updatecheck: reading response body from %q: %w", url, err)
	}

	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("updatecheck: %q returned status %d: %s", url, resp.StatusCode, truncate(body, 200))
	}

	var release releaseResponse
	if err := json.Unmarshal(body, &release); err != nil {
		return "", fmt.Errorf("updatecheck: decoding response from %q: %w", url, err)
	}
	if release.TagName == "" {
		return "", fmt.Errorf("updatecheck: response from %q had no tag_name field", url)
	}
	return release.TagName, nil
}

func truncate(b []byte, n int) string {
	if len(b) <= n {
		return string(b)
	}
	return string(b[:n]) + "..."
}

// CheckAndUpdateBrokerJSON performs one GitHub latest-release check (for
// RepoSlug) and, on success, read-modify-writes broker.json at dataDir so
// its LatestAvailableVersion field reflects the result — leaving every
// other field (Host, Port, PID, StartedAt, Token, Version) untouched. On
// any failure it logs at Printf level (this is routine/expected on an
// offline dev machine or when GitHub rate-limits) and returns without
// touching broker.json, so a failed check never clobbers a
// previously-known-good value.
//
// This never panics: it is safe to call directly (as tests do) or from the
// periodic loop in Run.
func CheckAndUpdateBrokerJSON(ctx context.Context, client *http.Client, dataDir, version string, logger *log.Logger) {
	checkAndUpdateBrokerJSONAt(ctx, client, githubAPIBase, dataDir, version, logger)
}

// checkAndUpdateBrokerJSONAt is CheckAndUpdateBrokerJSON with the GitHub API
// base URL as a parameter, so tests can point the whole check-and-update
// step at an httptest.Server (or a guaranteed-closed port, to exercise the
// connection-refused path) without touching production code's real target.
func checkAndUpdateBrokerJSONAt(ctx context.Context, client *http.Client, apiBase, dataDir, version string, logger *log.Logger) {
	defer func() {
		if r := recover(); r != nil {
			logger.Printf("updatecheck: recovered from panic during update check: %v", r)
		}
	}()

	tag, err := checkLatestReleaseAt(ctx, client, apiBase, RepoSlug, version)
	if err != nil {
		logger.Printf("updatecheck: latest-release check failed (leaving broker.json unchanged): %v", err)
		return
	}

	info, err := singleton.ReadBrokerJSON(dataDir)
	if err != nil {
		logger.Printf("updatecheck: reading broker.json from %q to record latest release %q: %v", dataDir, tag, err)
		return
	}
	info.LatestAvailableVersion = tag
	if err := singleton.WriteBrokerJSON(dataDir, info); err != nil {
		logger.Printf("updatecheck: writing broker.json at %q with latest release %q: %v", dataDir, tag, err)
		return
	}
	logger.Printf("updatecheck: latest available release is %s (running %s)", tag, version)
}

// Run periodically checks GitHub's latest-release API and keeps
// broker.json's LatestAvailableVersion current until ctx is cancelled. It
// runs one check shortly after starting (initialCheckDelay), then one every
// CheckInterval — mirroring the shape of main.go's heartbeat-prune-sweep
// goroutine (ticker + select on ctx.Done()/ticker.C).
//
// Run is meant to be started as its own goroutine (`go updatecheck.Run(...)`)
// from runPrimary; it never blocks the caller and never panics out of the
// goroutine.
func Run(ctx context.Context, client *http.Client, dataDir, version string, logger *log.Logger) {
	initialTimer := time.NewTimer(initialCheckDelay)
	defer initialTimer.Stop()

	select {
	case <-ctx.Done():
		return
	case <-initialTimer.C:
		CheckAndUpdateBrokerJSON(ctx, client, dataDir, version, logger)
	}

	ticker := time.NewTicker(CheckInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			CheckAndUpdateBrokerJSON(ctx, client, dataDir, version, logger)
		}
	}
}
