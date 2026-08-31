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

	// Raised from ceiling/4 (6250) once the connector's own script API and file exchange landed
	// (#91/#92, §09). The file had run flush against the old figure since #91, so every skill.md PR
	// was arriving at a hard wall -- and a budget calibrated to a smaller product surface eventually
	// evicts something an agent genuinely needs. An agent that doesn't know about the
	// confirmation-gated tier costs far more than the 1250 tokens saved by not telling it.
	//
	// Still derived from the host cap rather than picked: 30% of it, and still "orientation, not
	// reference" by a wide margin. The 3-bytes/token measure above stays pessimistic on top of that.
	const budgetTokens = ceilingTokens * 3 / 10

	// The old figure is kept as a SOFT line. Raising a ceiling removes the thing that made it useful
	// -- the prompt to ask whether a paragraph earns its space -- and "we'll refactor it back down
	// later" has no forcing function once the pressure is off. This restores the prompt without
	// blocking: crossing it reports how much headroom is left, so the trend is visible in CI output
	// rather than only discovered by hitting the wall.
	const softBudgetTokens = ceilingTokens / 4

	approxTokens := len(skillFile) / pessimisticBytesPerToken
	if approxTokens > softBudgetTokens && approxTokens <= budgetTokens {
		t.Logf("skill file is ~%d tokens, past the ~%d-token soft line with ~%d tokens of headroom "+
			"before the %d-token limit. Not a failure. Worth asking whether what you just added is "+
			"orientation an agent cannot get from describe_function.",
			approxTokens, softBudgetTokens, budgetTokens-approxTokens, budgetTokens)
	}
	if approxTokens > budgetTokens {
		// The remedy is deliberately spelled out. This file has run within ~1% of the budget since
		// issue #91, so the next person to add a paragraph hits this, and "keep it lightweight" alone
		// reads as an arbitrary blocker rather than a design constraint with a known escape hatch.
		t.Errorf("skill file is ~%d tokens (%d bytes), over the %d-token budget by ~%d tokens.\n"+
			"This budget is a real constraint, not a formality: skill.md is loaded as ORIENTATION and "+
			"competes with the caller's own context.\n"+
			"Prefer moving content out over trimming prose. Reference material -- signatures, "+
			"parameters, per-member behaviour -- belongs in XML doc comments, where describe_function "+
			"serves it on demand and it cannot drift (issue #91). Keep here only what discovery cannot "+
			"express: the transaction model, the confirmation-gated tier, ordering rules, and worked "+
			"examples.",
			approxTokens, len(skillFile), budgetTokens, approxTokens-budgetTokens)
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
		"ambiguous version path": "ambiguous-instance-version",
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
// directions. As of PRD §14 (Phase 3) the Document global IS the real
// Autodesk.Revit.DB.Document, and the one thing genuinely forbidden is a
// script opening its own transaction -- enforced by ScriptApiDenylist at
// compile time.
//
// This file's history is the reason this test exists at all, and it argues
// for pinning TOPICS rather than wording. It once shipped a full session
// claiming the API was "not callable from a script" and naming CS0122; both
// were wrong. The correction (reflection into a private field, CS1503) was
// then pinned here by its literal wording -- and that pinning is exactly what
// this Phase 3 change had to come back and rewrite, because the technique it
// named is now obsolete rather than merely reworded. So: pin that both halves
// of the capability story are PRESENT, keep forbidding the specific claims
// known to be false, and do not pin the mechanism of the day.
func TestSkillFileAccuratelyDescribesRevitApiReachability(t *testing.T) {
	for _, marker := range []string{
		"FilteredElementCollector",   // the headline example -- now one that WORKS, verbatim
		"Autodesk.Revit.DB.Document", // the Document global's real type, stated as such
		"script-api-denied",          // the error code an agent will actually see and must recognise
	} {
		if !strings.Contains(skillFile, marker) {
			t.Errorf("skill file no longer mentions %q: both halves of the capability story "+
				"(what works, what's denied) must survive future edits", marker)
		}
	}
	// The denylist half must name what is actually restricted, not just that a
	// denylist exists -- an agent hitting it needs to recognise the case.
	// Still topics, not wording: both members were named here before the
	// restriction on them split into two kinds (PRD §14) and both are still
	// named now, which is the point -- what changed is that
	// SynchronizeWithCentral is confirmation-gated rather than flatly refused,
	// not whether the guide tells an agent about it.
	for _, marker := range []string{"Transaction", "SynchronizeWithCentral"} {
		if !strings.Contains(skillFile, marker) {
			t.Errorf("skill file no longer names %q among what a script may not do freely", marker)
		}
	}
	// The two restrictions are not interchangeable and an agent that cannot
	// tell them apart fails in one of two bad ways: giving up on a permitted
	// operation, or retrying a structurally impossible one forever. So the
	// guide must carry the gated half's own error code AND the parameter that
	// lifts it -- a rewrite that collapsed the two back into one flat "denied"
	// table would drop these while every other assertion here still passed.
	for _, marker := range []string{
		"script-lifecycle-confirmation-required",
		"confirm_lifecycle_actions",
	} {
		if !strings.Contains(skillFile, marker) {
			t.Errorf("skill file no longer mentions %q: an agent that hits the lifecycle gate needs both "+
				"the code it will see and the argument that lifts it", marker)
		}
	}
	for _, forbidden := range []string{
		"not callable from a script", // false since before Phase 3
		"API is not reachable",       // ditto
		"CS0122",                     // never the real error
		"GetField",                   // the pre-Phase-3 reflection workaround, now obsolete AND unnecessary
		"Title` only",                // the old narrow-seam description of the Document global
	} {
		if strings.Contains(skillFile, forbidden) {
			t.Errorf("skill file contains %q: that claim is false as of Phase 3 (PRD §14) -- the Document "+
				"global is the real Autodesk.Revit.DB.Document and needs no reflection. Fix the prose, not this test",
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
