package buildinfo

import (
	"runtime/debug"
	"strings"
	"testing"
)

func settings(kv ...string) *debug.BuildInfo {
	bi := &debug.BuildInfo{}
	for i := 0; i+1 < len(kv); i += 2 {
		bi.Settings = append(bi.Settings, debug.BuildSetting{Key: kv[i], Value: kv[i+1]})
	}
	return bi
}

func TestReadFromExtractsTheVCSStamps(t *testing.T) {
	got := readFrom(settings(
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
		got := readFrom(settings("vcs.revision", "abc123", "vcs.modified", value), true)
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
		"toolchain returned nothing": readFrom(nil, false),
		"build info but no vcs keys": readFrom(settings("-buildmode", "exe", "GOARCH", "arm64"), true),
		"nil build info, ok=true":    readFrom(nil, true),
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
		if strings.Contains(got.StalenessCheck(), "rev-parse") {
			t.Errorf("%s: StalenessCheck() = %q, must not tell a reader to compare against a revision it does not have", name, got.StalenessCheck())
		}
		if got.StalenessCheck() == "" {
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

func TestSummaryCarriesRevisionTimeAndTheModifiedWarning(t *testing.T) {
	clean := Info{Revision: "34af007ca7daf0d4bce77ebb68d5041df17b9339", RevisionTime: "2026-08-31T13:32:07Z"}
	s := clean.Summary()
	if !strings.Contains(s, "34af007ca7da") {
		t.Errorf("Summary() = %q, want it to name the revision", s)
	}
	if !strings.Contains(s, "2026-08-31T13:32:07Z") {
		t.Errorf("Summary() = %q, want it to carry the commit time -- the part a reader can compare to a merge date", s)
	}
	if strings.Contains(strings.ToUpper(s), "MODIFIED") {
		t.Errorf("Summary() = %q, must not warn about a modified tree for a clean build", s)
	}

	dirty := clean
	dirty.Modified = true
	if !strings.Contains(dirty.Summary(), "MODIFIED") {
		t.Errorf("Summary() = %q, a dirty build must say so: its revision does not identify what is running", dirty.Summary())
	}
}

// The staleness sentence is the only part of this package a reader can act on,
// so pin what makes it actionable: the revision, a command that answers the
// question, and the remedy.
func TestStalenessCheckIsActionable(t *testing.T) {
	i := Info{Revision: "34af007ca7daf0d4bce77ebb68d5041df17b9339", RevisionTime: "2026-08-31T13:32:07Z"}
	s := i.StalenessCheck()
	for _, want := range []string{"34af007ca7da", "git rev-parse HEAD", "rebuild and restart"} {
		if !strings.Contains(s, want) {
			t.Errorf("StalenessCheck() = %q, missing %q", s, want)
		}
	}
	if strings.Contains(s, "uncommitted changes") {
		t.Errorf("StalenessCheck() = %q, must not claim uncommitted changes for a clean build", s)
	}
	dirty := i
	dirty.Modified = true
	if !strings.Contains(dirty.StalenessCheck(), "uncommitted changes") {
		t.Errorf("StalenessCheck() = %q, a dirty build must say the revision does not identify what is running", dirty.StalenessCheck())
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
	if got.Summary() == "" || got.StalenessCheck() == "" {
		t.Error("Read() produced empty human-facing text; every build must be able to describe itself")
	}
}
