package mcpserver

import (
	"strings"
	"testing"

	"github.com/modelcontextprotocol/go-sdk/mcp"
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

	// Raised from ceiling/4 (6250). The file has sat within a few tokens of that figure for a long
	// time -- long enough that any addition now arrives at a hard wall -- while the connector grew its
	// own script API (#91/#92) and file exchange (§09). A budget calibrated to a smaller surface
	// eventually evicts content an agent genuinely needs, and an agent that does not know about the
	// confirmation-gated tier costs far more than the 1250 tokens saved by not telling it.
	//
	// Still derived from the host cap rather than picked: 30% of it, and still "orientation, not
	// reference" by a wide margin. The 3-bytes/token measure above stays pessimistic on top of that.
	const budgetTokens = ceilingTokens * 3 / 10

	// The old figure is kept as a SOFT line. Raising a ceiling removes the thing that made it useful
	// -- the prompt to ask whether a paragraph earns its space -- and "we'll refactor it back down
	// later" has no forcing function once the pressure is off.
	//
	// t.Logf alone would be dead code here: `go test` DISCARDS a passing package's output entirely --
	// t.Logf, stdout and stderr alike -- so nothing below is visible under either the CI command or
	// the `go test ./...` this repo's SKILL.md documents. The ci.yml step "skill.md budget headroom"
	// re-runs this one test with -v for the sole purpose of surfacing it. If that step is ever
	// removed, this branch goes silent and should be deleted rather than left as decoration.
	const softBudgetTokens = ceilingTokens / 4

	approxTokens := len(skillFile) / pessimisticBytesPerToken
	if approxTokens > softBudgetTokens && approxTokens <= budgetTokens {
		// Phrased as state, not as a change: this fires on every run of the package while the file
		// sits in the window, including for someone who never opened skill.md.
		t.Logf("skill.md is ~%d tokens, past the ~%d-token soft line, with ~%d tokens of headroom "+
			"before the %d-token limit. Not a failure. If you are adding to this file, it is worth "+
			"asking whether the addition is orientation an agent cannot get from describe_function.",
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
	out := buildSkillsOut("dev")
	if out.Skill != skillFile {
		t.Error("get_skills returned something other than the embedded skill file verbatim")
	}
	if out.Format != "markdown" {
		t.Errorf("format = %q, want %q", out.Format, "markdown")
	}
}

// Issue #116: the served document was right in the repo and wrong in the
// running binary, and nothing in the response let the reading agent tell those
// apart. Every field below is what makes that distinguishable, so each is
// pinned rather than left to prose.
func TestGetSkillsReportsTheBuildThatServedIt(t *testing.T) {
	out := buildSkillsOut("v1.2.3")

	if out.Build.Version != "v1.2.3" {
		t.Errorf("Build.Version = %q, want the broker's own version string passed through", out.Build.Version)
	}
	if out.Build.Revision == "" {
		t.Error("Build.Revision is empty: an absent revision must read as \"unknown\", never as nothing at all")
	}
	if out.Build.Note == "" {
		t.Error("Build.Note is empty: a revision a reader cannot compare against anything is decoration")
	}
	// Under `go test` the toolchain stamps no vcs info, so this is the
	// degraded path -- and the degraded path must be honest.
	if out.Build.Revision != "unknown" && len(out.Build.Revision) < 7 {
		t.Errorf("Build.Revision = %q: neither a real revision nor an honest \"unknown\"", out.Build.Revision)
	}
}

// The response an agent actually receives, end to end. The structured Build
// field is invisible to a host that surfaces only text content -- which is how
// the agent in issue #116 read this document -- so the markdown half must
// carry the provenance too, and the document itself must still arrive intact.
func TestGetSkillsCallResultCarriesTheDocumentAndItsProvenance(t *testing.T) {
	res, out := skillsCallResult("v1.2.3")

	if len(res.Content) != 1 {
		t.Fatalf("got %d content items, want exactly the document", len(res.Content))
	}
	text, ok := res.Content[0].(*mcp.TextContent)
	if !ok {
		t.Fatalf("content is %T, want *mcp.TextContent", res.Content[0])
	}
	if !strings.HasPrefix(text.Text, skillFile) {
		t.Error("text content no longer starts with the embedded document verbatim")
	}
	if !strings.Contains(text.Text, out.Build.Revision) {
		t.Errorf("text content omits the build revision %q: a host that shows only text content "+
			"would leave a reader unable to tell a stale broker from a wrong document", out.Build.Revision)
	}
	if !strings.Contains(text.Text, out.Build.Note) {
		t.Error("text content omits the staleness check: the revision alone is decoration")
	}
	if !strings.Contains(text.Text, "v1.2.3") {
		t.Error("text content omits the broker version it was served by")
	}
}

// The footer's own contract, separate from the wiring above.
func TestSkillFooterCarriesProvenanceIntoTheMarkdown(t *testing.T) {
	footer := skillFooter(SkillBuild{Version: "v1.2.3", Revision: "34af007ca7da", Note: "rebuild and restart the broker"})

	for _, want := range []string{"v1.2.3", "34af007ca7da", "rebuild and restart the broker"} {
		if !strings.Contains(footer, want) {
			t.Errorf("footer = %q, missing %q", footer, want)
		}
	}
	// It is appended to a document that already sits within ~1% of its token
	// budget and is charged to the same reader's context, so it must stay a
	// footer rather than grow into a section.
	const maxFooterBytes = 800
	if len(footer) > maxFooterBytes {
		t.Errorf("footer is %d bytes, over the %d-byte cap: it is charged to the same context budget as skill.md itself", len(footer), maxFooterBytes)
	}
}
