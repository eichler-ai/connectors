package mcpserver

import (
	"crypto/sha256"
	"encoding/hex"
	"strings"
	"testing"

	"github.com/eichler-ai/connectors/internal/servercore/buildinfo"
)

// The skill file is shipped content, not code, so these tests pin the
// properties an agent actually depends on rather than its prose: that it is
// embedded and non-empty, that it stays inside the MCP output ceiling, that it
// documents every tool the server registers, and that it does not carry claims
// that are false for this connector. The failure mode for a hand-written skill
// file is silent drift as tools change, and drift is exactly what a doc read as
// ground truth cannot afford.

func TestSkillFileIsEmbeddedAndStartsWithAHeading(t *testing.T) {
	if len(skillFile) == 0 {
		t.Fatal("skillFile is empty: the go:embed target resolved to nothing")
	}
	if !strings.HasPrefix(skillFile, "#") {
		t.Errorf("skillFile should start with a markdown heading, got %.40q", skillFile)
	}
}

func TestSkillFileStaysWithinItsBudget(t *testing.T) {
	// Claude Code caps MCP output at 25,000 tokens by default. A deliberately
	// pessimistic 3 bytes/token errs toward failing early rather than shipping
	// something that truncates in the host. skill.md is loaded as ORIENTATION
	// and competes with the caller's own context, so this is a real constraint.
	const pessimisticBytesPerToken = 3
	const ceilingTokens = 25000
	// The Rhino guide covers phase 1 (execution, discovery, capture, undo) plus
	// phase 4 (Grasshopper drive/read, plug-in management, restart) and sits near
	// 6.8k tokens. 30% of the host cap leaves ~700 tokens of room before the number
	// is revisited on its merits.
	const budgetTokens = ceilingTokens * 30 / 100
	// The soft line sits ABOVE the file's current size so crossing it is
	// information, not a warning that fires forever. t.Logf alone is dead code
	// under `go test` (a passing package's output is discarded); the ci.yml
	// "skill.md budget headroom" step re-runs this one test with -v to surface
	// it. If that step is removed, delete this branch rather than leave it.
	// Raised 26%->29% when Phase 4 landed (Grasshopper drive/read + plug-in
	// management + restart_rhino sections), to sit back above the file's size.
	const softBudgetTokens = ceilingTokens * 29 / 100

	// The footer get_skills appends at runtime is charged to the same reader's
	// context, so measure what a caller receives, not what is on disk. Use the
	// longest note the product produces.
	longestFooter := 0
	for _, b := range []SkillBuild{
		buildSkillsOut("dev", stampedInfo).Build,
		buildSkillsOut("dev", buildinfo.Info{Revision: "34af007ca7daf0d4bce77ebb68d5041df17b9339", Modified: true}).Build,
		buildSkillsOut("dev", buildinfo.Info{}).Build,
		buildSkillsOut("v1.2.3", stampedInfo).Build,
	} {
		if n := len(skillFooter(b)); n > longestFooter {
			longestFooter = n
		}
	}

	approxTokens := (len(skillFile) + longestFooter) / pessimisticBytesPerToken
	if approxTokens > softBudgetTokens && approxTokens <= budgetTokens {
		t.Logf("skill.md is ~%d tokens, past the ~%d-token soft line, with ~%d tokens of headroom "+
			"before the %d-token limit. Not a failure. If you are adding to this file, ask whether the "+
			"addition is orientation an agent cannot get from a tool schema or (once it lands) describe_function.",
			approxTokens, softBudgetTokens, budgetTokens-approxTokens, budgetTokens)
	}
	if approxTokens > budgetTokens {
		t.Errorf("skill file is ~%d tokens (%d bytes), over the %d-token budget by ~%d tokens.\n"+
			"This budget is a real constraint, not a formality: skill.md is loaded as ORIENTATION and "+
			"competes with the caller's own context.\n"+
			"Prefer moving content out over trimming prose. Reference material -- signatures, parameters, "+
			"per-member behaviour -- belongs in the tool schemas (and, once discovery lands, in XML doc "+
			"comments served on demand). Keep here only what those cannot express: the undo model, the "+
			"refused/gated tiers, the two-language contract, and worked examples.",
			approxTokens, len(skillFile), budgetTokens, approxTokens-budgetTokens)
	}
}

// registeredToolNames is every tool this server exposes. Kept as a literal
// list a human must update because the SDK gives no exported way to enumerate
// registered tools; that is acceptable precisely because this test is what
// forces the skill file to be updated alongside a tool change.
var registeredToolNames = []string{
	"list_instances",
	"execute_script",
	"poll_execution",
	"cancel_execution",
	"search_functions",
	"list_functions",
	"describe_function",
	"capture_view",
	"undo",
	"redo",
	"search_plugins",
	"list_plugins",
	"install_plugin",
	"uninstall_plugin",
	"restart_rhino",
	"inspect_gh_definition",
	"frame_canvas",
	"get_skills",
}

func TestSkillFileDocumentsEveryRegisteredTool(t *testing.T) {
	for _, name := range registeredToolNames {
		// As a code span: the bare words "undo"/"redo" appear in ordinary prose
		// about Rhino's Undo history, so a substring match would pass without
		// the TOOLS being documented.
		if !strings.Contains(skillFile, "`"+name+"`") {
			t.Errorf("skill file never mentions `%s` as a tool: add it, or an agent reading this file will not know the tool exists", name)
		}
	}
}

func TestSkillFileCoversTheBriefedTopics(t *testing.T) {
	// One marker per topic the file was commissioned to cover, chosen to be
	// things the prose cannot plausibly lose without also losing the topic.
	topics := map[string]string{
		"architecture overview":       "MCP Bridge",
		"two languages":               "CPython 3",
		"platform difference":         "platform",
		"cross-session observability": "last_run",
		"cooperative cancellation":    "cancel.Check",
		"error interpretation":        "remedy",
		"unrecoverable handling":      "unrecoverable",
		"human status entry":          "MCPBridgeStatus",
		// The discovery surface (list/search/describe) and, specifically, that describe returns BOTH call
		// shapes -- the marker is the Python-form field name, which the prose cannot lose without losing the
		// dual-call-shape story an agent depends on to write either language.
		"api discovery": "python_call",
	}
	for topic, marker := range topics {
		if !strings.Contains(skillFile, marker) {
			t.Errorf("skill file appears not to cover %s (no mention of %q)", topic, marker)
		}
	}
}

// The single most important correctness property: the file must accurately
// describe what a script can and cannot do, in both directions. The globals ARE
// the real RhinoCommon types, the whole run is one undo entry, and the genuinely
// restricted set is the undo/exit members, the interactive getters, and the
// lifecycle members -- split into flatly-refused and confirmation-gated.
func TestSkillFileAccuratelyDescribesScriptReachability(t *testing.T) {
	for _, marker := range []string{
		"Rhino.RhinoDoc",    // the doc/Document global's real type, stated as such
		"rhinoscriptsyntax", // the Python convenience layer, available
		"script-api-denied", // the refusal code an agent will see and must recognise
	} {
		if !strings.Contains(skillFile, marker) {
			t.Errorf("skill file no longer mentions %q: the capability story (what works, what's denied) must survive future edits", marker)
		}
	}
	// The denylist half must name what is actually restricted, both tiers, so
	// an agent that hits one can tell it apart from the other and knows which
	// (if either) a flag lifts.
	for _, marker := range []string{
		"RhinoDoc.Undo",                          // the undo members, flatly refused
		"GetObject",                              // the interactive getters, flatly refused
		"script-lifecycle-confirmation-required", // the gated tier's code
		"confirm_lifecycle_actions",              // the argument that lifts it
	} {
		if !strings.Contains(skillFile, marker) {
			t.Errorf("skill file no longer names %q among what a script may not do freely / how to lift the gate", marker)
		}
	}
	// Claims that would be false for THIS connector. The Python host is CPython,
	// not IronPython; a script never opens a Rhino transaction (Rhino has none);
	// and the undo tools are not the way to fix a mistake inside a run.
	for _, forbidden := range []string{
		"IronPython",      // Rhino.Runtime.Code is real CPython 3; the incumbent's IronPython is a different thing
		"WithTransaction", // Revit's model; Rhino has no transactions
	} {
		if strings.Contains(skillFile, forbidden) {
			t.Errorf("skill file contains %q, which is false for the Rhino connector: fix the prose, not this test", forbidden)
		}
	}
}

// The undo model is the load-bearing correctness claim unique to Rhino: one run
// is one undo entry, a failed run is reverted automatically, and a script must
// never touch the undo stack. All three must survive edits.
func TestSkillFileDescribesTheUndoModel(t *testing.T) {
	for _, marker := range []string{
		"one entry",              // one run is one undo entry
		"reverted automatically", // a failed run is undone for you
	} {
		if !strings.Contains(skillFile, marker) {
			t.Errorf("skill file no longer states the undo model claim %q: it is the connector's central behaviour", marker)
		}
	}
}

// stampedInfo is the provenance shape a REAL build produces. Tests must inject
// it: a test binary carries no VCS stamps of its own, so a suite that let
// buildSkillsOut read its own would only exercise the degraded path and would
// pass with the production shape completely broken.
var stampedInfo = buildinfo.Info{
	Revision:     "34af007ca7daf0d4bce77ebb68d5041df17b9339",
	RevisionTime: "2026-09-10T13:32:07Z",
	Stamped:      true,
}

func TestGetSkillsReturnsTheEmbeddedFile(t *testing.T) {
	out := buildSkillsOut("dev", stampedInfo)
	if out.Skill != skillFile {
		t.Error("get_skills returned something other than the embedded skill file verbatim")
	}
	if out.Format != "markdown" {
		t.Errorf("format = %q, want markdown", out.Format)
	}
}

// Issue #116 (Revit): the served document was right in the repo and wrong in
// the running binary, and nothing in the response let the reader tell those
// apart. Every field below is what makes that distinguishable.
func TestGetSkillsReportsTheBuildThatServedIt(t *testing.T) {
	out := buildSkillsOut("dev", stampedInfo)
	if out.Build.Version != "dev" {
		t.Errorf("Build.Version = %q, want the broker's own version string passed through", out.Build.Version)
	}
	if out.Build.Revision != "34af007ca7da" {
		t.Errorf("Build.Revision = %q, want the injected build's short revision", out.Build.Revision)
	}
	if out.Build.RevisionTime != stampedInfo.RevisionTime {
		t.Errorf("Build.RevisionTime = %q, want %q", out.Build.RevisionTime, stampedInfo.RevisionTime)
	}
	if out.Build.Note == "" {
		t.Error("Build.Note is empty: a revision a reader cannot compare against anything is decoration")
	}
	// The hash is computed here from the document itself, not read back from the
	// field it verifies -- asserting the field against itself would pass under
	// any implementation, including one that hashed the wrong thing.
	sum := sha256.Sum256([]byte(skillFile))
	want := hex.EncodeToString(sum[:])[:12]
	if out.Build.SkillHash != want {
		t.Errorf("Build.SkillHash = %q, want %q (the content hash of skill.md)", out.Build.SkillHash, want)
	}
	// A dev build's note offers the content-hash check with the real hash in it.
	if !strings.Contains(out.Build.Note, want) {
		t.Error("a dev build's note should carry the skill.md content hash so a reader can verify the running binary")
	}
}

// The footer a text-only host sees must carry the same provenance as the
// structured field, and must be honest about a dirty tree.
func TestSkillFooterCarriesProvenance_AndMarksADirtyTree(t *testing.T) {
	clean := skillFooter(buildSkillsOut("v0.2.0", stampedInfo).Build)
	if !strings.Contains(clean, "rhino-mcp-server v0.2.0") || !strings.Contains(clean, "34af007ca7da") {
		t.Errorf("footer missing version/revision: %q", clean)
	}
	dirty := skillFooter(SkillBuild{Version: "dev", Revision: "abc123", Modified: true, Note: "n"})
	if !strings.Contains(dirty, "tree not clean") {
		t.Errorf("footer does not mark a dirty-tree build: %q", dirty)
	}
}
