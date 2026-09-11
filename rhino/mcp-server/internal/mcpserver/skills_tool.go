// get_skills: a built-in orientation document for agents driving this
// connector.
//
// Served entirely broker-side from an embedded file, deliberately. Unlike a
// tool that reflects over Rhino's real API, this content is static -- so it
// answers before Rhino has ever been launched, which is exactly the moment an
// agent most needs to know how the connector works. Making it depend on a live
// session would invert that.
//
// Embedded rather than read from disk so the broker stays one self-contained
// binary: no install layout to get wrong, no file to go missing, and the
// document is versioned with the code that implements the tools it describes.
package mcpserver

import (
	"context"
	_ "embed"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/internal/servercore/buildinfo"
)

//go:embed skill.md
var skillFile string

// GetSkillsIn is the input schema for get_skills -- no arguments. The document
// is small enough to return whole (see the size test), so there is no section
// selector or pagination: an agent asking "how do I use this connector" should
// get one answer, not a cursor.
type GetSkillsIn struct{}

// SkillBuild is the provenance of the binary this document came out of. It
// rides along because the Revit connector was bitten (issue #116) by a broker
// left running from an old build serving a skill.md that described an API
// surface that no longer compiled, with no way for the reader to tell "the
// guide is wrong" from "the guide is old". The content hash is the field to
// trust: it is exact and cannot be misattributed by the toolchain the way a
// revision can inside a git worktree (see internal/servercore/buildinfo).
type SkillBuild struct {
	Version      string `json:"version"`
	Revision     string `json:"revision"`
	RevisionTime string `json:"revision_time,omitempty"`
	Modified     bool   `json:"modified,omitempty"`
	SkillHash    string `json:"skill_sha256"`
	Note         string `json:"note"`
}

// GetSkillsOut carries the document plus its format and the provenance of the
// build that served it.
type GetSkillsOut struct {
	Format string     `json:"format"`
	Skill  string     `json:"skill"`
	Build  SkillBuild `json:"build"`
}

// buildSkillsOut builds the response. info is a parameter rather than a
// buildinfo.Read() call inside, so tests can exercise the shape a real build
// produces -- a test binary carries no VCS stamps of its own, so a self-reading
// version would only ever cover the degraded path.
func buildSkillsOut(version string, info buildinfo.Info) GetSkillsOut {
	hash := buildinfo.ContentHash(skillFile)
	return GetSkillsOut{
		Format: "markdown",
		Skill:  skillFile,
		Build: SkillBuild{
			Version:      version,
			Revision:     info.ShortRevision(),
			RevisionTime: info.RevisionTime,
			Modified:     info.Modified,
			SkillHash:    hash,
			Note:         skillNote(version, info, hash),
		},
	}
}

// skillNote tells a reader how to check this build against their checkout. The
// content-hash check carries the weight: a revision cannot be trusted to answer
// it (the toolchain misattributes it inside a worktree, and a build can carry
// none), while comparing the served document against the file in the repo has
// neither failure mode. Offered only to a dev build, since a release install
// has no repo to run it against and buildinfo already gives that reader a remedy.
func skillNote(version string, info buildinfo.Info, hash string) string {
	note := info.StalenessCheck(version)
	if isRelease(version) {
		return note
	}
	return note + " Everything it serves -- this document, the tool schemas, its behaviour -- is " +
		"compiled in, so the definitive check is the document itself: " +
		"`shasum -a 256 rhino/mcp-server/internal/mcpserver/skill.md` must print " + hash +
		". If it prints anything else, this broker is not your checkout -- rebuild and restart it " +
		"(`go build ./cmd/mcp-server`)."
}

// isRelease mirrors buildinfo's notion of a released build: "dev" is what every
// local and CI build carries.
func isRelease(version string) bool { return version != "" && version != "dev" }

// skillFooter renders the same provenance as the structured Build field into
// the markdown a model actually reads -- a host that surfaces only text content
// would otherwise show none of it. Kept short: the skill sits well within its
// token budget and this is charged to the same reader.
func skillFooter(b SkillBuild) string {
	rev := b.Revision
	if b.Modified {
		rev += ", tree not clean"
	}
	return "\n\n---\n\n**Provenance.** Served by rhino-mcp-server " + b.Version +
		" (revision " + rev + "). " + b.Note + "\n"
}

// RegisterSkills adds get_skills. version is the broker's own release version
// string, the only thing it takes -- no Rhino-facing dependency of any kind,
// which is the point: it cannot fail and works with zero Rhino instances
// connected. The provenance it reports is baked into the executable image, so
// reading it does no I/O either.
func RegisterSkills(s *mcp.Server, version string) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "get_skills",
		Description: "Read the built-in guide to driving Rhino through this connector: architecture, " +
			"addressing instances and documents across macOS and Windows, running Python or C# with examples, " +
			"what the connector refuses and what it gates, capturing a viewport to see the model, undoing your " +
			"own work, and reading errors. Needs no connected Rhino instance, so it can be called first. Start " +
			"here if you haven't used this connector before. The guide always ends with a Provenance footer " +
			"naming the build that served it; if it is missing, the broker predates that footer and everything " +
			"it serves may be older than your connector.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in GetSkillsIn) (*mcp.CallToolResult, GetSkillsOut, error) {
		res, out := skillsCallResult(version, buildinfo.Read())
		return res, out, nil
	})
}

// skillsCallResult builds exactly what a get_skills call returns, split out of
// the handler closure so both halves of the response are unit-testable without
// standing up an mcp.Server. The document is also returned as text content
// because it is written to be read by a model and some hosts surface text more
// readily than structured output; the provenance footer is appended here rather
// than written into skill.md because it is per-build data.
func skillsCallResult(version string, info buildinfo.Info) (*mcp.CallToolResult, GetSkillsOut) {
	out := buildSkillsOut(version, info)
	return &mcp.CallToolResult{
		Content: []mcp.Content{&mcp.TextContent{Text: out.Skill + skillFooter(out.Build)}},
	}, out
}
