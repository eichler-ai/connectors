package buildinfo

import (
	"crypto/sha256"
	"encoding/hex"
	"runtime/debug"
	"strings"
	"testing"
)

// readFromVCS is readFrom with no explicit ldflags stamp -- the ordinary
// checkout build, and the case most of these tests are about.
func readFromVCS(bi *debug.BuildInfo, ok bool) Info { return readFrom(bi, ok, "", "", "") }

func settings(kv ...string) *debug.BuildInfo {
	bi := &debug.BuildInfo{}
	for i := 0; i+1 < len(kv); i += 2 {
		bi.Settings = append(bi.Settings, debug.BuildSetting{Key: kv[i], Value: kv[i+1]})
	}
	return bi
}

func TestReadFromExtractsTheVCSStamps(t *testing.T) {
	got := readFromVCS(settings(
		"-buildmode", "exe",
		"vcs", "git",
		"vcs.revision", "34af007ca7daf0d4bce77ebb68d5041df17b9339",
		"vcs.time", "2026-08-31T13:32:07Z",
		"vcs.modified", "true",
	), true)

	if got.Revision != "34af007ca7daf0d4bce77ebb68d5041df17b9339" {
		t.Errorf("Revision = %q", got.Revision)
	}
	if got.RevisionTime != "2026-08-31T13:32:07Z" {
		t.Errorf("RevisionTime = %q", got.RevisionTime)
	}
	if !got.Modified {
		t.Error("Modified = false, want true for vcs.modified=true")
	}
	if !got.Known() {
		t.Error("Known() = false despite a stamped revision")
	}
}

// The whole mechanism is worthless if a clean release build reports itself as
// modified -- the loud signal has to stay rare enough to mean something.
func TestReadFromTreatsOnlyLiteralTrueAsModified(t *testing.T) {
	for _, value := range []string{"false", "", "TRUE", "1", "yes"} {
		got := readFromVCS(settings("vcs.revision", "abc123", "vcs.modified", value), true)
		if got.Modified {
			t.Errorf("vcs.modified=%q reported as Modified; only the literal \"true\" may", value)
		}
	}
}

// A binary built without VCS information (source tarball, -buildvcs=false) and
// a test binary (the toolchain omits vcs stamps there) must both read as
// unknown. Reporting a plausible-looking value instead is the failure mode
// this package exists to prevent, one level down.
func TestReadFromDegradesToUnknownWithoutVCSInfo(t *testing.T) {
	cases := map[string]Info{
		"toolchain returned nothing": readFromVCS(nil, false),
		"build info but no vcs keys": readFromVCS(settings("-buildmode", "exe", "GOARCH", "arm64"), true),
		"nil build info, ok=true":    readFromVCS(nil, true),
	}
	for name, got := range cases {
		if got.Known() {
			t.Errorf("%s: Known() = true, want false", name)
		}
		if got.Revision != "" {
			t.Errorf("%s: Revision = %q, want empty", name, got.Revision)
		}
		if got.ShortRevision() != "unknown" {
			t.Errorf("%s: ShortRevision() = %q, want %q", name, got.ShortRevision(), "unknown")
		}
		if !strings.Contains(got.Summary(), "unknown") {
			t.Errorf("%s: Summary() = %q, must say the revision is unknown rather than imply one", name, got.Summary())
		}
		if strings.Contains(got.StalenessCheck("dev"), "rev-parse") {
			t.Errorf("%s: StalenessCheck() = %q, must not tell a reader to compare against a revision it does not have", name, got.StalenessCheck("dev"))
		}
		if got.StalenessCheck("dev") == "" {
			t.Errorf("%s: StalenessCheck() is empty; the unknown case still needs to tell the reader what to do", name)
		}
	}
}

func TestShortRevisionTruncatesButNeverPads(t *testing.T) {
	long := Info{Revision: "34af007ca7daf0d4bce77ebb68d5041df17b9339"}
	if got, want := long.ShortRevision(), "34af007ca7da"; got != want {
		t.Errorf("ShortRevision() = %q, want %q", got, want)
	}
	// A revision shorter than the short length (another VCS, or a truncated
	// stamp) must come back verbatim, not sliced out of range.
	short := Info{Revision: "abc123"}
	if got := short.ShortRevision(); got != "abc123" {
		t.Errorf("ShortRevision() = %q, want the whole short value back", got)
	}
}

func TestSummaryCarriesRevisionTimeAndTheStateOfTheTree(t *testing.T) {
	clean := Info{Revision: "34af007ca7daf0d4bce77ebb68d5041df17b9339", RevisionTime: "2026-08-31T13:32:07Z", Stamped: true}
	s := clean.Summary()
	if !strings.Contains(s, "34af007ca7da") {
		t.Errorf("Summary() = %q, want it to name the revision", s)
	}
	if !strings.Contains(s, "2026-08-31T13:32:07Z") {
		t.Errorf("Summary() = %q, want it to carry the commit time -- the part a reader can compare to a merge date", s)
	}
	if strings.Contains(s, "not clean") {
		t.Errorf("Summary() = %q, must not warn about the tree for a clean build", s)
	}
	if strings.Contains(s, "inferred") {
		t.Errorf("Summary() = %q, must not hedge an explicitly stamped revision", s)
	}

	dirty := clean
	dirty.Modified = true
	// Naming the cause matters: the toolchain counts untracked files as
	// modified, so a scratch file in the checkout turns this on. A warning
	// that is permanently on and unexplained stops being read.
	if !strings.Contains(dirty.Summary(), "uncommitted or untracked") {
		t.Errorf("Summary() = %q, a dirty build must say so AND say what counts", dirty.Summary())
	}

	inferred := clean
	inferred.Stamped = false
	if !strings.Contains(inferred.Summary(), "inferred") {
		t.Errorf("Summary() = %q, a revision the toolchain guessed must be marked as a guess", inferred.Summary())
	}
}

// The staleness sentence is the only part of this package a reader can act on,
// and each build shape can support a different check. Offering one that is not
// valid for the build in hand is the failure mode that matters: it produces a
// confident wrong answer rather than no answer.
func TestStalenessCheckOffersOnlyTheCheckThatIsValid(t *testing.T) {
	full := "34af007ca7daf0d4bce77ebb68d5041df17b9339"

	stamped := Info{Revision: full, RevisionTime: "2026-08-31T13:32:07Z", Stamped: true}.StalenessCheck("dev")
	for _, want := range []string{"34af007ca7da", "git rev-parse HEAD"} {
		if !strings.Contains(stamped, want) {
			t.Errorf("stamped: StalenessCheck() = %q, missing %q", stamped, want)
		}
	}

	// The BLOCKER case. The Go toolchain resolves a repository by walking up
	// for a `.git` DIRECTORY, and a worktree's `.git` is a file, so a build
	// made inside a worktree carries the ENCLOSING checkout's revision: the
	// binary holds the worktree's code and names the enclosing checkout's
	// commit. A reader told to compare that against `git rev-parse HEAD` in
	// the enclosing checkout gets a MATCH, and concludes a mismatched broker
	// is current. See internal/buildinfo's package comment for the measured
	// run; the revision below is a fixture, not that measurement.
	inferred := Info{Revision: full}.StalenessCheck("dev")
	if strings.Contains(inferred, "git rev-parse HEAD") {
		t.Errorf("inferred: StalenessCheck() = %q offers a comparison that returns a false MATCH for a "+
			"worktree build -- worse than offering none", inferred)
	}
	if !strings.Contains(inferred, "worktree") || !strings.Contains(inferred, "hint") {
		t.Errorf("inferred: StalenessCheck() = %q must say the revision is a hint and why", inferred)
	}

	// A release install has no repo, no checkout, and no Go toolchain.
	release := Info{Revision: full, Stamped: true}.StalenessCheck("v1.2.3")
	if !strings.Contains(release, "v1.2.3") {
		t.Errorf("release: StalenessCheck() = %q must name the release the reader is running", release)
	}
	if !strings.Contains(release, "latest_available_version") {
		t.Errorf("release: StalenessCheck() = %q must point at the freshness answer that reader has "+
			"(internal/updatecheck already writes it)", release)
	}
	for _, forbidden := range []string{"git ", "go build", "rebuild"} {
		if strings.Contains(release, forbidden) {
			t.Errorf("release: StalenessCheck() = %q tells an installed user to run %q, which needs a "+
				"source checkout they do not have", release, forbidden)
		}
	}

	unknown := Info{}.StalenessCheck("dev")
	if strings.Contains(unknown, "git rev-parse") {
		t.Errorf("unknown: StalenessCheck() = %q compares against a revision it does not have", unknown)
	}
	if unknown == "" {
		t.Error("unknown: even a build with no provenance must say that, rather than nothing")
	}
}

// An explicit -ldflags stamp is the only revision that can be right inside a
// git worktree, so it must win over the toolchain's guess -- and must mark
// itself as trustworthy, since that is what unlocks the rev-parse advice.
func TestExplicitStampWinsOverTheToolchainsGuess(t *testing.T) {
	vcs := settings("vcs.revision", "1111111111111111111111111111111111111111", "vcs.time", "2020-01-01T00:00:00Z", "vcs.modified", "true")

	got := readFrom(vcs, true, "2222222222222222222222222222222222222222", "2026-08-31T13:32:07Z", "false")
	if got.Revision != "2222222222222222222222222222222222222222" {
		t.Errorf("Revision = %q, want the explicitly stamped one: it is the only source that can be right in a worktree", got.Revision)
	}
	if got.RevisionTime != "2026-08-31T13:32:07Z" {
		t.Errorf("RevisionTime = %q, want the stamped one, not the toolchain's", got.RevisionTime)
	}
	if got.Modified {
		t.Error("Modified = true: the stamp said clean, so the toolchain's own dirty flag must not leak through")
	}
	if !got.Stamped {
		t.Error("Stamped = false for an explicitly stamped build")
	}

	// And the toolchain's answer is used, unmarked, when there is no stamp.
	fallback := readFrom(vcs, true, "", "", "")
	if fallback.Revision != "1111111111111111111111111111111111111111" || fallback.Stamped {
		t.Errorf("without a stamp, got Revision=%q Stamped=%v; want the toolchain's revision, marked as not stamped", fallback.Revision, fallback.Stamped)
	}
	if !fallback.Modified {
		t.Error("without a stamp, the toolchain's vcs.modified=true must survive")
	}

	// A stamped build with no toolchain info at all still works: this is the
	// worktree-outside-a-checkout case, where the scripts stamp and the
	// toolchain has nothing.
	stampedOnly := readFrom(nil, false, "3333333333333333333333333333333333333333", "", "true")
	if !stampedOnly.Known() || !stampedOnly.Stamped || !stampedOnly.Modified {
		t.Errorf("stamped-only build read as %+v, want a known, stamped, modified build", stampedOnly)
	}
}

// The content hash is what stays valid when the revision cannot be trusted, so
// it has to be a real hash of the real content, not an identifier that merely
// changes sometimes.
func TestContentHashIdentifiesContent(t *testing.T) {
	sum := sha256.Sum256([]byte("hello"))
	want := hex.EncodeToString(sum[:])[:12]
	if got := ContentHash("hello"); got != want {
		t.Errorf("ContentHash(%q) = %q, want the first 12 hex characters of its sha256 (%q)", "hello", got, want)
	}
	if ContentHash("hello") == ContentHash("hello ") {
		t.Error("ContentHash collides on a one-character difference: it cannot answer \"is this the document in my repo\"")
	}
	if len(ContentHash("")) != 12 {
		t.Errorf("ContentHash(\"\") = %q, want 12 characters even for empty content", ContentHash(""))
	}
}

// Read() itself, against the real toolchain. Inside `go test` there are no vcs
// stamps, so this cannot assert a revision -- what it can assert is the
// property the whole design rests on: Read never invents one, and never
// panics, so a plain `go build`/`go test` is honest rather than broken.
func TestReadNeverFabricatesAndNeverPanics(t *testing.T) {
	got := Read()
	if got.Known() && len(got.Revision) < 7 {
		t.Errorf("Read() reported a revision %q too short to be real", got.Revision)
	}
	if !got.Known() && got.ShortRevision() != "unknown" {
		t.Errorf("Read() with no VCS info reported %q instead of unknown", got.ShortRevision())
	}
	if got.Summary() == "" || got.StalenessCheck("dev") == "" {
		t.Error("Read() produced empty human-facing text; every build must be able to describe itself")
	}
}
