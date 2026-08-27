package mcpserver

import (
	"strings"
	"testing"
)

// The skill file is shipped content, not code, so these tests pin the
// properties an agent actually depends on rather than its prose: that it is
// embedded at all (a missing go:embed target fails the build, but an EMPTY
// one wouldn't), that it stays inside the MCP output ceiling, and that it
// still documents every tool the server actually registers. That last one is
// the point of automating this: the failure mode for a hand-written skill
// file is silent drift as tools are added or renamed, and drift is exactly
// what a doc like this cannot afford, since an agent reads it as ground truth.

func TestSkillFileIsEmbeddedAndNonTrivial(t *testing.T) {
	if len(skillFile) == 0 {
		t.Fatal("skillFile is empty: the go:embed target resolved to nothing")
	}
	if !strings.HasPrefix(skillFile, "#") {
		t.Errorf("skillFile should start with a markdown heading, got %.40q", skillFile)
	}
}

func TestSkillFileStaysUnderTheMcpOutputCeiling(t *testing.T) {
	// PRD §09: Claude Code caps MCP output at 25,000 tokens by default. Using
	// a deliberately pessimistic 3 bytes/token so the check errs toward
	// failing early rather than shipping something that truncates in the
	// host. The brief was "relatively lightweight" -- a skill file that eats
	// the caller's context budget defeats its own purpose, so this is a real
	// design constraint, not a formality.
	const pessimisticBytesPerToken = 3
	const ceilingTokens = 25000
	const budgetTokens = ceilingTokens / 4 // stay well inside; this is orientation, not reference

	approxTokens := len(skillFile) / pessimisticBytesPerToken
	if approxTokens > budgetTokens {
		t.Errorf("skill file is ~%d tokens (%d bytes), over the %d-token budget: keep it lightweight",
			approxTokens, len(skillFile), budgetTokens)
	}
}

// registeredToolNames is every tool this server exposes. Kept here rather
// than reflected out of the mcp.Server because the SDK gives no exported way
// to enumerate registered tools; a literal list that a human must update is
// acceptable precisely because this test is what forces the skill file to be
// updated alongside it.
var registeredToolNames = []string{
	"execute_script",
	"poll_execution",
	"cancel_execution",
	"list_instances",
	"list_functions",
	"search_functions",
	"describe_function",
	"get_skills",
}

func TestSkillFileDocumentsEveryRegisteredTool(t *testing.T) {
	for _, name := range registeredToolNames {
		if !strings.Contains(skillFile, name) {
			t.Errorf("skill file never mentions %q: add it, or an agent reading this file will not know the tool exists", name)
		}
	}
}

func TestSkillFileCoversTheBriefedTopics(t *testing.T) {
	// One marker per topic the skill file was commissioned to cover. Markers
	// are chosen to be things the prose cannot plausibly lose without also
	// losing the topic.
	topics := map[string]string{
		"architecture overview":  "MCP Bridge",
		"multi-instance":         "instance_id",
		"multi-version":          "revit_version",
		"error interpretation":   "remedy",
		"ambiguous version path": "ambiguous_instance_version",
		"file exchange out":      "exports",
		"file exchange in":       "imports",
		"discovery usage":        "search_functions",
	}
	for topic, marker := range topics {
		if !strings.Contains(skillFile, marker) {
			t.Errorf("skill file appears not to cover %s (no mention of %q)", topic, marker)
		}
	}
}

// The single most important correctness property of this document: it must not
// promise Revit API access that execute_script does not currently have. The
// script globals are a narrow seam (Document exposes Title and nothing else),
// and `new FilteredElementCollector(Document)` fails to compile live with
// CS0122. A draft of this file used exactly that as its headline example. An
// agent believing it would burn turns writing scripts that cannot compile, so
// the caveat is pinned rather than trusted to survive future edits.
func TestSkillFileWarnsThatTheRevitApiIsNotCallableYet(t *testing.T) {
	for _, marker := range []string{
		"FilteredElementCollector", // named as the concrete thing that fails
		"CS0122",                   // the actual compiler error it produces
	} {
		if !strings.Contains(skillFile, marker) {
			t.Errorf("skill file no longer mentions %q: the current-capability caveat must survive, "+
				"or agents will write scripts against a Revit API they cannot reach", marker)
		}
	}
}

func TestSkillFileDoesNotHardcodeTheWorkspacePath(t *testing.T) {
	// The workspace root has already moved once relative to what the PRD
	// describes (it is RevitMCPExchange live, not the documented
	// %LOCALAPPDATA% layout). Telling an agent a literal path invites it to
	// build paths by hand instead of reading ExportsDirectory/ImportsDirectory.
	for _, forbidden := range []string{"RevitMCPExchange", "LOCALAPPDATA", "%LocalAppData%"} {
		if strings.Contains(skillFile, forbidden) {
			t.Errorf("skill file hard-codes %q: point at ExportsDirectory/ImportsDirectory instead", forbidden)
		}
	}
}

func TestGetSkillsReturnsTheEmbeddedFile(t *testing.T) {
	out := buildSkillsOut()
	if out.Skill != skillFile {
		t.Error("get_skills returned something other than the embedded skill file verbatim")
	}
	if out.Format != "markdown" {
		t.Errorf("format = %q, want %q", out.Format, "markdown")
	}
}
