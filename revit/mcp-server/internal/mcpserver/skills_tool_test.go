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

func TestSkillFileIsEmbeddedAndStartsWithAHeading(t *testing.T) {
	if len(skillFile) == 0 {
		t.Fatal("skillFile is empty: the go:embed target resolved to nothing")
	}
	if !strings.HasPrefix(skillFile, "#") {
		t.Errorf("skillFile should start with a markdown heading, got %.40q", skillFile)
	}
}

func TestSkillFileStaysWithinItsLightweightBudget(t *testing.T) {
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
		"multi-version":          "revit_version",
		"error interpretation":   "remedy",
		"ambiguous version path": "ambiguous_instance_version",
		"file exchange out":      "exports",
		"file exchange in":       "imports",
		// Connection mechanics and debugging: an agent that can't tell "not
		// connected yet" from "broken" either reports a false failure or waits
		// forever, and list_instances alone cannot distinguish them.
		//
		// These markers are deliberately TOPIC-level, not wording-level. An
		// earlier version pinned the literal strings "broker.json" and
		// "backoff", which pinned prose rather than substance: a review
		// recommended tightening exactly that paragraph, and those assertions
		// would have failed a change that kept every fact intact. A test that
		// blocks good edits without protecting meaning is worse than no test.
		"self-healing retry":     "re-check",
		"human status entry":     "Status",
		"unrecoverable handling": "unrecoverable",
	}
	for topic, marker := range topics {
		if !strings.Contains(skillFile, marker) {
			t.Errorf("skill file appears not to cover %s (no mention of %q)", topic, marker)
		}
	}
}

// The single most important correctness property of this document: it must
// accurately describe what execute_script can and cannot reach, in both
// directions. The sanctioned Document global is a narrow seam (Title only),
// and passing it where a real Revit API type is expected fails to compile
// live with CS1503 (confirmed live against a real instance -- a draft of
// this file claimed CS0122, which is wrong: that's a protection-level error,
// not the type-mismatch this actually produces). But the API IS reachable
// via reflection into the document adapter's private field, and real writes
// through it succeed (also confirmed live). An agent that read an earlier
// version of this file believing the API was flatly unreachable would burn
// turns avoiding a technique that actually works -- as costly a failure mode
// as the original "don't bother, it won't compile" caveat existed to prevent
// in the first place. Both halves are pinned so neither regresses silently.
func TestSkillFileAccuratelyDescribesRevitApiReachability(t *testing.T) {
	for _, marker := range []string{
		"FilteredElementCollector", // named as the concrete thing that fails through the sanctioned seam
		"CS1503",                   // the actual compiler error that produces, not CS0122
		"GetField",                 // the reflection technique that actually reaches the real API
	} {
		if !strings.Contains(skillFile, marker) {
			t.Errorf("skill file no longer mentions %q: both halves of the capability story "+
				"(what's blocked, what actually works) must survive future edits", marker)
		}
	}
	for _, forbidden := range []string{"not callable from a script", "API is not reachable", "CS0122"} {
		if strings.Contains(skillFile, forbidden) {
			t.Errorf("skill file contains %q: this claim is false as of this test (Revit API IS reachable "+
				"via reflection, and the real compile error is CS1503 not CS0122) -- fix the prose, not this test",
				forbidden)
		}
	}
}

func TestSkillFileDoesNotHardcodeTheWorkspacePath(t *testing.T) {
	// The workspace root has already moved once relative to what PRD §09
	// describes (RevitMCPExchange live, not the documented %LOCALAPPDATA%
	// layout). Naming a literal workspace path invites an agent to build paths
	// by hand instead of reading ExportsDirectory/ImportsDirectory, which is
	// the only thing guaranteed to stay correct.
	//
	// Scoped to WORKSPACE paths on purpose. An earlier version of this test
	// banned the substring "LOCALAPPDATA" outright, which also caught the
	// diagnostic-file locations (connection.log, broker.json) added for the
	// debugging section -- those are read by a human, have no global to read
	// them from, and are not what this guard is about. Narrowed rather than
	// deleted: the hazard is agents constructing workspace paths, not the doc
	// ever naming a path at all.
	for _, forbidden := range []string{"RevitMCPExchange", `workspaces\`, "workspaces/"} {
		if strings.Contains(skillFile, forbidden) {
			t.Errorf("skill file hard-codes workspace path %q: point at ExportsDirectory/ImportsDirectory instead", forbidden)
		}
	}
	// The globals it should point at instead must actually be named.
	for _, required := range []string{"ExportsDirectory", "ImportsDirectory"} {
		if !strings.Contains(skillFile, required) {
			t.Errorf("skill file should tell the reader to use %s", required)
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
