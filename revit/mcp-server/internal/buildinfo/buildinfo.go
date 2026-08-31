// Package buildinfo reports which source revision the running broker binary
// was actually built from.
//
// Why this exists (issue #116): every agent-facing thing the broker serves --
// skill.md via get_skills, the tool schemas and their descriptions, the
// routing logic behind them -- is compiled INTO this binary. There is
// therefore exactly one drift a reader can suffer: the BINARY is older than
// the SOURCE TREE the reader is looking at. (Drift between an embedded
// resource and its own source file is impossible: go:embed is resolved at
// compile time, so the embedded copy always matches the file the binary was
// built from.) A broker left running from a build that predates a merge will
// happily teach an agent an API surface that no longer compiles, with nothing
// anywhere saying so.
//
// The revision is read from the Go toolchain's own automatic VCS stamping
// (runtime/debug.ReadBuildInfo's vcs.* settings), deliberately NOT from
// -ldflags: plain `go build` stamps it with no flags at all, so a developer's
// locally-built broker -- the exact artifact in issue #116 -- carries the same
// provenance as a release build, and no build invocation anywhere has to be
// kept in sync for this to work.
//
// Everything degrades to an explicit "unknown": a binary built without VCS
// info (a source tarball, -buildvcs=false) and a test binary (the toolchain
// omits vcs stamps there) must read as unknown, never as a plausible-looking
// fake value.
//
// One toolchain behaviour to know about, measured rather than assumed: the Go
// toolchain locates the repository by walking up for a `.git` DIRECTORY, so
// building inside a git WORKTREE (whose `.git` is a file) stamps the revision
// of the enclosing checkout when the worktree sits inside one, and stamps
// nothing at all when it sits outside one. A broker built from a worktree
// therefore reports a revision that is real but not the one it was built from.
// The artifact this matters for -- the broker a session actually runs -- is
// built from the main checkout by install-mac.sh and by
// dev-tooling/redeploy-and-verify.sh, where the stamp is correct; and that
// script deliberately rebuilds rather than comparing revisions, so it is
// unaffected either way.
package buildinfo

import (
	"fmt"
	"runtime/debug"
)

// shortRevisionLen is how many hex characters of the revision go into
// human-facing summaries -- git's own conventional short form, long enough to
// paste into `git merge-base --is-ancestor` and have it resolve.
const shortRevisionLen = 12

// Info is what this binary can honestly say about its own provenance.
type Info struct {
	// Revision is the full VCS revision the binary was built from, or ""
	// when the build carried no VCS information at all.
	Revision string
	// RevisionTime is that revision's commit time (RFC 3339, UTC as the
	// toolchain records it), or "" when unknown.
	RevisionTime string
	// Modified reports that the tree had uncommitted changes at build time,
	// which means Revision alone does NOT identify what is running.
	Modified bool
}

// Known reports whether the binary carries any VCS provenance at all.
func (i Info) Known() bool { return i.Revision != "" }

// ShortRevision is Revision truncated to shortRevisionLen, or "unknown".
func (i Info) ShortRevision() string {
	if !i.Known() {
		return "unknown"
	}
	if len(i.Revision) > shortRevisionLen {
		return i.Revision[:shortRevisionLen]
	}
	return i.Revision
}

// Summary is the one-line human form used in the startup log, the -version
// output and the MCP server's advertised version.
func (i Info) Summary() string {
	if !i.Known() {
		return "revision unknown (built without VCS information)"
	}
	s := "revision " + i.ShortRevision()
	if i.RevisionTime != "" {
		s += " committed " + i.RevisionTime
	}
	if i.Modified {
		s += ", built from a MODIFIED tree"
	}
	return s
}

// StalenessCheck is the sentence a reader can actually act on. A revision an
// agent cannot compare against anything is decoration, so this names the
// concrete command that answers "is the running broker behind my checkout?"
// and says what to do when it is.
func (i Info) StalenessCheck() string {
	if !i.Known() {
		return "This broker binary carries no VCS information, so its age cannot be checked. " +
			"If what it serves disagrees with the connector source you have, rebuild and restart " +
			"the broker before assuming the source is wrong."
	}
	s := fmt.Sprintf("This document, the tool schemas and the broker's behaviour are compiled in: "+
		"they describe revision %s, not your checkout. In the connector repo, if `git rev-parse HEAD` "+
		"is not that revision, the running broker is not your checkout -- rebuild and restart it "+
		"(revit/install-mac.sh, or `go build ./cmd/mcp-server`) before treating any of it as current.",
		i.ShortRevision())
	if i.Modified {
		s += " This binary was built from a tree with uncommitted changes, so its revision does not " +
			"fully identify what is running."
	}
	return s
}

// Read returns the provenance of the running binary.
//
// It cannot fail and makes no I/O, syscalls or network calls -- the data is
// baked into the executable image. That matters for its main caller: get_skills
// answers with zero Revit instances connected, before Revit has ever been
// launched, and nothing added here may change that.
func Read() Info {
	bi, ok := debug.ReadBuildInfo()
	return readFrom(bi, ok)
}

// readFrom is Read with the toolchain's answer injected, so the unknown and
// partial cases can be tested directly -- a test binary has no vcs stamps of
// its own, so Read() alone can only ever exercise one of them.
func readFrom(bi *debug.BuildInfo, ok bool) Info {
	var info Info
	if !ok || bi == nil {
		return info
	}
	for _, s := range bi.Settings {
		switch s.Key {
		case "vcs.revision":
			info.Revision = s.Value
		case "vcs.time":
			info.RevisionTime = s.Value
		case "vcs.modified":
			// Only the literal "true" means modified. Anything else --
			// "false", or a value a future toolchain spells differently --
			// must not be reported as a clean tree OR as a dirty one by
			// accident; "false" is the honest default for an unrecognised
			// value, since claiming "modified" of a clean release build
			// would make the loud signal meaningless.
			info.Modified = s.Value == "true"
		}
	}
	return info
}
