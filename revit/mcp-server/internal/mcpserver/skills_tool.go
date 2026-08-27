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
)

//go:embed skill.md
var skillFile string

// GetSkillsIn is the input schema for get_skills -- no arguments. The
// document is deliberately small enough to return whole (see the size test),
// so there is no section selector or pagination to reason about: an agent
// asking "how do I use this connector" should get one answer, not a cursor.
type GetSkillsIn struct{}

// GetSkillsOut carries the document plus its format, so a caller doesn't have
// to infer that it's markdown from the content.
type GetSkillsOut struct {
	Format string `json:"format"`
	Skill  string `json:"skill"`
}

// buildSkillsOut exists so the response can be unit-tested without standing up
// an mcp.Server.
func buildSkillsOut() GetSkillsOut {
	return GetSkillsOut{Format: "markdown", Skill: skillFile}
}

// RegisterSkills adds get_skills to s. It takes no dependencies, which is the
// point: it cannot fail, and it works with zero Revit instances connected.
func RegisterSkills(s *mcp.Server) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "get_skills",
		Description: "Read the built-in guide to driving Revit through this connector: architecture, " +
			"addressing instances and documents across Revit versions, how to use each tool with examples, " +
			"how to read errors, how to exchange files with Revit in both directions, and how to discover the " +
			"Revit API. Needs no connected Revit instance, so it can be called first. Start here if you " +
			"haven't used this connector before.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in GetSkillsIn) (*mcp.CallToolResult, GetSkillsOut, error) {
		out := buildSkillsOut()
		// Also returned as text content: the document is written to be read by
		// a model, and some hosts surface text content more readily than
		// structured output.
		return &mcp.CallToolResult{
			Content: []mcp.Content{&mcp.TextContent{Text: out.Skill}},
		}, out, nil
	})
}
