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
// One toolchain behaviour to know about. The Go toolchain locates the
// repository by walking up for a `.git` DIRECTORY; a git worktree's `.git` is
// a FILE, so the walk continues past it, and a build made inside a worktree
// nested in another checkout is stamped with the ENCLOSING checkout's
// revision. The binary contains the WORKTREE's code and reports the ENCLOSING
// checkout's revision -- that direction is the whole point, and it is the
// opposite of harmless.
//
// Measured here, in this repo, on 2026-08-31 (`go build` in the worktree, then
// `go version -m` on the result -- run it yourself the same way):
//
//	worktree   .claude/worktrees/agent-a18034fd0  HEAD 1b0d96c  (a branch)  .git is a FILE
//	enclosing  the repo root                      HEAD 34af007  (main)      .git is a DIRECTORY
//	stamped into the binary built from the worktree: vcs.revision=34af007  <- the enclosing one
//	vcs.modified=true, describing the ENCLOSING tree's stray untracked file, while the worktree
//	itself was clean -- the stamp reports another tree's state entirely, not merely another commit
//
// That is worse than no signal at all: a reader who runs the comparison this
// package recommends, in the enclosing checkout, gets a MATCH, and concludes a
// mismatched broker is current. This project runs agents in nested worktrees
// (.gitignore's .claude/worktrees/), so it is a live path, not a hypothetical.
//
// Two things answer it. Any build that KNOWS which checkout it came from
// stamps that explicitly via -ldflags (install-mac.sh and
// dev-tooling/redeploy-and-verify.sh both do, from their own $REPO_ROOT), and
// an explicit stamp always wins over the toolchain's guess. And the document's
// own content hash (SkillHash) is checkable with no revision semantics at all,
// so "is the guide it is serving the guide in my repo" stays answerable even
// when the revision is misattributed or absent.
package buildinfo

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"runtime/debug"
)

// Set by -ldflags at build time by a builder that knows which checkout it is
// building from -- see this package's own doc comment for why the toolchain's
// automatic stamp cannot be trusted to answer that inside a git worktree.
// Empty (every plain `go build`) means "fall back to the automatic stamp",
// which is correct for an ordinary checkout.
//
//	-ldflags "-X <this package>.stampedRevision=$(git -C <root> rev-parse HEAD) ..."
var (
	stampedRevision     string
	stampedRevisionTime string
	stampedModified     string // "true" only; anything else is a clean tree
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
	// Modified reports that the build tree was not clean -- uncommitted
	// changes to tracked files, and, for the toolchain's own stamp,
	// untracked files too -- so Revision alone does NOT identify what is
	// running.
	Modified bool
	// Stamped records that the revision was declared explicitly by the
	// builder rather than guessed by the toolchain. Only an explicit stamp
	// is trustworthy inside a git worktree (see the package comment).
	Stamped bool
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
// output and the MCP server's advertised version. Phrased to nest inside
// "<version> (...)" without a second bracket.
func (i Info) Summary() string {
	if !i.Known() {
		return "revision unknown, built without VCS information"
	}
	s := "revision " + i.ShortRevision()
	if i.RevisionTime != "" {
		s += " committed " + i.RevisionTime
	}
	if !i.Stamped {
		// Not decoration: an unstamped revision is the toolchain's inference
		// from whatever checkout it found walking up, which is the ENCLOSING
		// one for a build made inside a git worktree.
		s += ", inferred"
	}
	if i.Modified {
		// Names the cause, because the toolchain's own notion of "modified"
		// counts untracked files too -- a stray scratch file in the checkout
		// flips this on, and a warning that is always on stops being read.
		s += ", tree not clean (uncommitted or untracked files)"
	}
	return s
}

// StalenessCheck is the sentence a reader can act on about the BINARY. A
// revision an agent cannot compare against anything is decoration, so each
// case names the check that is actually valid for it -- and, just as
// important, declines to offer one that is not.
//
// version is the broker's own release version ("dev" for every local and CI
// build); a release build's reader generally has no repo and no Go toolchain,
// so telling them to compare git revisions would be advice they cannot follow.
func (i Info) StalenessCheck(version string) string {
	if isRelease(version) {
		return fmt.Sprintf("This broker is release %s, and everything it serves is compiled into "+
			"that release. If the connector you installed is newer, upgrade the broker: "+
			"broker.json's latest_available_version records the newest release it has seen.", version)
	}
	switch {
	case !i.Known():
		return "This broker carries no VCS information, so which source it was built from cannot be " +
			"checked from the revision."
	case i.Stamped:
		return fmt.Sprintf("This broker was built from revision %s of the connector repo; if "+
			"`git rev-parse HEAD` there names a different revision, it is not your checkout.",
			i.ShortRevision())
	default:
		// Deliberately hedged. The toolchain infers this from the enclosing
		// checkout, so inside a git worktree it names a revision that is real
		// and wrong -- and a reader told to compare it would get a MATCH on a
		// mismatched broker, which is worse than no signal at all.
		return fmt.Sprintf("This broker reports revision %s, inferred by the toolchain rather than "+
			"declared by the build -- a build made inside a git worktree names the enclosing "+
			"checkout instead, so treat it as a hint and not as proof.", i.ShortRevision())
	}
}

// isRelease reports whether version identifies a released build rather than
// the "dev" every local and CI build carries.
func isRelease(version string) bool { return version != "" && version != "dev" }

// ContentHash is the short hash form used to identify a compiled-in document
// by its content. Worktree-immune and revision-free: a reader compares it
// against `shasum -a 256 <the file>` and gets a definitive answer about the
// one thing they care about, no VCS semantics involved.
func ContentHash(content string) string {
	sum := sha256.Sum256([]byte(content))
	return hex.EncodeToString(sum[:])[:shortRevisionLen]
}

// Read returns the provenance of the running binary.
//
// It cannot fail and makes no I/O, syscalls or network calls -- the data is
// baked into the executable image. That matters for its main caller: get_skills
// answers with zero Revit instances connected, before Revit has ever been
// launched, and nothing added here may change that.
func Read() Info {
	bi, ok := debug.ReadBuildInfo()
	return readFrom(bi, ok, stampedRevision, stampedRevisionTime, stampedModified)
}

// readFrom is Read with both sources injected, so every case can be tested
// directly -- a test binary carries no vcs stamps and no ldflags of its own,
// so Read() alone can only ever exercise the degraded one.
//
// An explicit stamp WINS over the toolchain's, unconditionally: it is the only
// one that can be right inside a git worktree, and a builder that took the
// trouble to declare which checkout it built from is a better authority than a
// directory walk. Nothing reconciles the two, deliberately -- silently
// preferring whichever "looks better" is how a guard starts lying.
func readFrom(bi *debug.BuildInfo, ok bool, stampedRev, stampedTime, stampedMod string) Info {
	if stampedRev != "" {
		return Info{
			Revision:     stampedRev,
			RevisionTime: stampedTime,
			Modified:     stampedMod == "true",
			Stamped:      true,
		}
	}
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
