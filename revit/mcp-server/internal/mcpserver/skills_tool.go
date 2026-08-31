// get_skills: a built-in orientation document for agents driving this
// connector.
//
// Served entirely broker-side from an embedded file, deliberately. Unlike the
// discovery tools (§08), which reflect over Revit's real assemblies and
// therefore need a connected instance, this content is static -- so it answers
// before Revit has ever been launched, which is exactly the moment an agent
// most needs to know how the connector works. Making it depend on a live
// session would invert that.
//
// Embedded rather than read from disk so the broker stays the single
// self-contained binary §04 requires: no install layout to get wrong, no
// file to go missing, and the document is versioned with the code that
// implements the tools it describes.
package mcpserver

import (
	"context"
	_ "embed"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/buildinfo"
)

//go:embed skill.md
var skillFile string

// GetSkillsIn is the input schema for get_skills -- no arguments. The
// document is deliberately small enough to return whole (see the size test),
// so there is no section selector or pagination to reason about: an agent
// asking "how do I use this connector" should get one answer, not a cursor.
type GetSkillsIn struct{}

// SkillBuild is the provenance of the binary this document came out of.
//
// It rides along with the document because of issue #116: a broker left
// running from a build that predated a merge served a skill.md describing an
// API surface that no longer compiled, and the reading agent -- with no way to
// tell "the guide is wrong" from "the guide is old" -- filed three false
// documentation bugs against a repo whose file on disk was already correct.
// The revision alone would be decoration, so Note carries the comparison a
// reader can actually run and the remedy if it fails.
type SkillBuild struct {
	// Version is the release tag this broker was built as, or "dev" for a
	// build made without one (every local and CI build).
	Version string `json:"version"`
	// Revision is the source revision it was built from, or "unknown" when
	// the build carried no VCS information at all -- never a fake value.
	Revision string `json:"revision"`
	// RevisionTime is that revision's commit time, omitted when unknown.
	RevisionTime string `json:"revision_time,omitempty"`
	// Modified marks a build made from a tree with uncommitted changes, i.e.
	// one Revision does not fully identify.
	Modified bool `json:"modified,omitempty"`
	// Note tells the reader how to check this build against their checkout,
	// and what to do when it is behind.
	Note string `json:"note"`
}

// GetSkillsOut carries the document plus its format, so a caller doesn't have
// to infer that it's markdown from the content, plus the provenance of the
// build that served it.
type GetSkillsOut struct {
	Format string     `json:"format"`
	Skill  string     `json:"skill"`
	Build  SkillBuild `json:"build"`
}

// buildSkillsOut exists so the response can be unit-tested without standing up
// an mcp.Server.
func buildSkillsOut(version string) GetSkillsOut {
	info := buildinfo.Read()
	return GetSkillsOut{
		Format: "markdown",
		Skill:  skillFile,
		Build: SkillBuild{
			Version:      version,
			Revision:     info.ShortRevision(),
			RevisionTime: info.RevisionTime,
			Modified:     info.Modified,
			Note:         info.StalenessCheck(),
		},
	}
}

// skillFooter renders the same provenance as the structured Build field into
// the markdown a model actually reads. Kept short deliberately: skill.md sits
// at ~1% of its token budget, and this is charged to the same reader.
func skillFooter(b SkillBuild) string {
	return "\n\n---\n\n**Provenance.** Served by revit-mcp-server " + b.Version +
		", built from revision " + b.Revision + ". " + b.Note + "\n"
}

// RegisterSkills adds get_skills to s. version is the broker's own release
// version string, the only thing it takes -- no Revit-facing dependency of any
// kind, which is the point: it cannot fail, and it works with zero Revit
// instances connected. The provenance it reports is baked into the executable
// image (see internal/buildinfo), so reading it does no I/O either.
func RegisterSkills(s *mcp.Server, version string) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "get_skills",
		Description: "Read the built-in guide to driving Revit through this connector: architecture, " +
			"addressing instances and documents across Revit versions, how to use each tool with examples, " +
			"how to read errors, how to exchange files with Revit in both directions, and how to discover the " +
			"Revit API. Needs no connected Revit instance, so it can be called first. Start here if you " +
			"haven't used this connector before.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in GetSkillsIn) (*mcp.CallToolResult, GetSkillsOut, error) {
		res, out := skillsCallResult(version)
		return res, out, nil
	})
}

// skillsCallResult builds exactly what a get_skills call returns, split out of
// the handler closure so both halves of the response are unit-testable without
// standing up an mcp.Server.
//
// The document is also returned as text content because it is written to be
// read by a model, and some hosts surface text content more readily than
// structured output. The provenance footer is appended HERE rather than
// written into skill.md, for two reasons: it is per-build data, so it cannot
// live in a static file at all; and a reader who only ever sees the text
// content -- the exact reader issue #116 burned -- would otherwise never see
// the structured Build field beside it.
func skillsCallResult(version string) (*mcp.CallToolResult, GetSkillsOut) {
	out := buildSkillsOut(version)
	return &mcp.CallToolResult{
		Content: []mcp.Content{&mcp.TextContent{Text: out.Skill + skillFooter(out.Build)}},
	}, out
}
