package mcpserver

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"strings"
	"testing"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/buildinfo"
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
	// Raised again, 30% -> 35%, deliberately and on request rather than as a side effect (2026-08-31).
	// The 30% figure had been consumed to ~1 token of headroom by two same-day PRs -- #121's
	// created-document lifecycle correction (#114/#118) and #123's return_value split (#117) -- and a
	// one-token margin is not a forcing function, it is a tripwire: the next edit fails CI on arrival
	// regardless of merit, and the reflex under that pressure is to bump the number silently or to cut
	// good content to fit. #123 already paid that tax, dropping the re-listing of timeout_ms /
	// max_duration_ms / overwrite_output_files / confirm_lifecycle_actions to make room. That cut was
	// independently right (the MCP tool schema hands an agent all four verbatim), but it was decided by
	// the budget rather than on its merits, which is the failure this raise exists to stop repeating.
	//
	// 35% of the host cap. Still anchored to that cap rather than to the file's current size, and
	// still "orientation, not reference" by a wide margin -- but the honest framing is that the cap
	// bounds the choice rather than determining it: 32% would have fit this change too. What picks
	// 35% is the size of the margin wanted (~950 tokens, about three troubleshooting rows), not
	// arithmetic. The 3-bytes/token measure above stays pessimistic on top of it.
	const budgetTokens = ceilingTokens * 35 / 100

	// The soft line sits ABOVE the file's current size, deliberately, and this is a correction to a
	// first draft of this raise (independent PR review). That draft promoted the previous CEILING to
	// the soft line, following this file's own convention -- but the file is now ~7.8k, i.e. already
	// past that mark, so the warning would have fired on every CI run from the merge onward, for
	// everyone, forever. A warning that is unconditionally on is indistinguishable from no warning:
	// the reviewer seeing it on an unrelated PR learns to scroll past it, and it is then worth less
	// than nothing, because it also masks the real crossing later. The convention's PURPOSE is that
	// crossing the line is information, which requires the file to start below it. Nothing plans to
	// bring skill.md back under 7.5k, so the line goes where crossing it still means something.
	//
	// t.Logf alone would be dead code here: `go test` DISCARDS a passing package's output entirely --
	// t.Logf, stdout and stderr alike -- so nothing below is visible under either the CI command or
	// the `go test ./...` this repo's SKILL.md documents. The ci.yml step "skill.md budget headroom"
	// re-runs this one test with -v for the sole purpose of surfacing it. If that step is ever
	// removed, this branch goes silent and should be deleted rather than left as decoration.
	const softBudgetTokens = ceilingTokens * 33 / 100

	// The footer get_skills appends at runtime is charged to the same reader's
	// context as the file itself, so the budget measures what a caller
	// actually receives, not what is on disk. Measured against the longest
	// note the product produces rather than a nominal figure.
	longestFooter := 0
	for _, b := range []SkillBuild{
		buildSkillsOut("dev", stampedInfo).Build,
		buildSkillsOut("dev", buildinfo.Info{Revision: "34af007ca7daf0d4bce77ebb68d5041df17b9339"}).Build,
		buildSkillsOut("dev", buildinfo.Info{}).Build,
		buildSkillsOut("v1.2.3", stampedInfo).Build,
	} {
		if n := len(skillFooter(b)); n > longestFooter {
			longestFooter = n
		}
	}

	approxTokens := (len(skillFile) + longestFooter) / pessimisticBytesPerToken
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
		// Issue #118 problem 2. Not connector behaviour but a Revit trap that
		// silently produces a wrong model: a script-created Level leaves its
		// room computation plane at the level's own elevation, so NewRoom
		// returns zero boundary loops against walls that have any base offset.
		// An agent that does not know the parameter's name cannot find the fix
		// from the symptom, which is only a generic "not properly enclosed"
		// warning.
		"room computation height": "LEVEL_ROOM_COMPUTATION_HEIGHT",
		// Issue #113, same shape as the row above and pinned for the same reason: a Revit trap the
		// compiler cannot catch, where the fix is a member name an agent cannot guess from the
		// symptom. ChangeTypeId is the marker rather than SheetTitleBlockId because the property
		// name would still be present in a row that only said "don't touch this" -- it is the
		// REMEDY that has to survive.
		"title block retype": "ChangeTypeId",
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

// Issue #114. This section is the one place in the file whose being wrong
// actively CAUSED the bug it was blamed for: it told agents that a
// connector-created document could never be closed, so the agent that read it
// stopped trying and left five scratch documents open in a live Revit session
// until Revit warned about memory. Verified against Revit 2027: a same-run
// Close is refused by Revit ("Close is not allowed when there is any open
// sub-transaction, transaction or transaction group"), and the very next
// execute_script call closes the same document successfully with
// confirm_lifecycle_actions. So the guidance has to carry BOTH halves; either
// half alone is what produced the leak.
//
// Topics, not wording, per this file's own history. The visibility half
// (#118 problem 1) is checked against a set of alternatives for the same
// reason -- what must survive is that an agent is told the created document is
// not something a person can see, not the adjective used to say it.
func TestSkillFileDescribesCreatedDocumentLifecycleHonestly(t *testing.T) {
	const start = "**Creating documents"
	i := strings.Index(skillFile, start)
	if i < 0 {
		t.Fatalf("skill file no longer has a %q section: created-document lifecycle is the "+
			"subject of issue #114 and must be documented somewhere findable", start)
	}
	section := skillFile[i:]
	if j := strings.Index(section, "\n### "); j > 0 {
		section = section[:j]
	}

	// Both halves of the close story, plus the fact that a created document is
	// addressable at all (the old text denied it, and an agent that believes
	// that cannot even find the document again to close it).
	//
	// Honest about their strength: these three passed against the OLD, wrong
	// text too, because it named the same members while telling the reader they
	// were useless. They guard against the section being gutted, not against it
	// being wrong. The forbidden-claims list below is what actually caught the
	// old text, and it is the assertion to extend if a new false claim appears.
	for _, marker := range []string{"Close", "confirm_lifecycle_actions", "list_instances"} {
		if !strings.Contains(section, marker) {
			t.Errorf("the created-documents section never mentions %q: an agent that creates a scratch "+
				"document must be told how to close it again, or it will leak documents into a live "+
				"Revit session (#114)", marker)
		}
	}
	// The close RECIPE itself, which is the part with consequences. Scoped to the fenced block so a
	// stray "later" elsewhere in the section cannot satisfy it -- an earlier version of this check
	// looked for "next"/"later" anywhere in the section and passed on an unrelated sentence three
	// paragraphs up, guarding nothing while claiming to guard the whole close story.
	// Located by the Close call it must contain, then widened to its fence, rather than by the
	// block's first line -- anchoring on that made an ordinary edit to the snippet's first line
	// read as "the recipe is gone".
	recipe := ""
	if c := strings.Index(section, "scratch.Close("); c >= 0 {
		if a := strings.LastIndex(section[:c], "```csharp\n"); a >= 0 {
			if b := strings.Index(section[c:], "```"); b > 0 {
				recipe = section[a : c+b]
			}
		}
	}
	if recipe == "" {
		t.Error("the created-documents section no longer carries a runnable Close recipe: an agent told " +
			"only that cleanup is possible, without the four lines that do it, is where #114 started")
	}
	// PathName is the load-bearing one. Title alone does not identify a scratch document -- Revit
	// auto-names unsaved documents Project1, Project2..., and a SAVED model at ...\Project1.rvt has
	// Title == "Project1" too -- so a recipe matching on Title alone hands an agent a Close(false),
	// which discards without prompting, aimed at a real user file. Verified live that both collide.
	if recipe != "" && !strings.Contains(recipe, "PathName") {
		t.Error("the Close recipe does not filter on PathName: matching a scratch document by Title alone " +
			"can resolve to a person's own unsaved document, or to a saved model of the same name, and " +
			"Close(false) then discards their work without a prompt")
	}
	visibility := []string{"headless", "no window", "never the active document", "not visible"}
	found := false
	for _, marker := range visibility {
		if strings.Contains(section, marker) {
			found = true
			break
		}
	}
	if !found {
		t.Errorf("the created-documents section says nothing about the document being invisible to the "+
			"person at the screen (looked for any of %v). 'Writable immediately' alone reads as "+
			"'usable in the ordinary sense', which it is not (#118)", visibility)
	}

	// Claims verified false against Revit 2027. Kept as an explicit denylist
	// because each one was in this file at some point and each one, believed,
	// leads an agent to abandon cleanup.
	//
	// Deliberately scanning the WHOLE file, not just section -- these claims are
	// wrong wherever they appear, and one of them migrating into the quick
	// reference or the troubleshooting table should still fail. Note the honest
	// limit: exact substrings catch these four regressions, not a newly-invented
	// false claim. Nothing automatic can do the latter; that is what verifying
	// against a running Revit before writing is for.
	forbidden := map[string]string{
		"There is no cleanup path":          "a created document CAN be closed, from any run after the one that created it",
		"has no `document_id`":              "an unsaved created document gets a tmp-<guid> id and appears in list_instances",
		"never appears in `list_instances`": "it does appear there",
		"until they restart Revit":          "restarting Revit is not the only recovery; Close works",
		// Issue #113's own filed hypothesis, disproven live: the defect is a TYPE id in an
		// INSTANCE-typed property, and cross-family is incidental to it. Forbidden because the
		// plausible-sounding version would send a reader looking for a same-family workaround that
		// does not exist, and because ChangeTypeId was verified to cross families cleanly.
		"same title block family": "ChangeTypeId retypes a placed title block across families; the id KIND is the defect, not the family",
	}
	for claim, why := range forbidden {
		if strings.Contains(skillFile, claim) {
			t.Errorf("skill file contains the claim %q, which is false: %s. Fix the prose, not this test", claim, why)
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

// stampedInfo is the provenance shape a REAL build produces. Tests must inject
// it: a test binary carries no VCS stamps of its own, so a suite that let
// buildSkillsOut read its own would only ever exercise the degraded path and
// would pass with the production shape completely broken.
var stampedInfo = buildinfo.Info{
	Revision:     "34af007ca7daf0d4bce77ebb68d5041df17b9339",
	RevisionTime: "2026-08-31T13:32:07Z",
	Stamped:      true,
}

func TestGetSkillsReturnsTheEmbeddedFile(t *testing.T) {
	out := buildSkillsOut("dev", stampedInfo)
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

	// The hash is computed here from the document itself, not read back from
	// the field it is meant to verify -- asserting the field against itself
	// would pass under any implementation, including one that hashed the
	// wrong thing.
	sum := sha256.Sum256([]byte(skillFile))
	want := hex.EncodeToString(sum[:])[:12]
	if out.Build.SkillHash != want {
		t.Errorf("Build.SkillHash = %q, want %q (sha256 of the embedded document)", out.Build.SkillHash, want)
	}
	if !strings.Contains(out.Build.Note, want) {
		t.Error("the note omits the document hash: it is the only check that stays valid when the " +
			"revision is misattributed (git worktree) or absent")
	}
	if !strings.Contains(out.Build.Note, "shasum") {
		t.Error("the note names a hash but no way to compute one to compare against")
	}
}

// An unstamped revision is the toolchain's inference from whatever checkout it
// found, which is the ENCLOSING one for a build made inside a git worktree.
// Telling that reader to compare it against `git rev-parse HEAD` produces a
// MATCH on a mismatched broker -- a confidently wrong answer, worse than none.
func TestGetSkillsDoesNotOfferARevisionComparisonItCannotStandBehind(t *testing.T) {
	inferred := buildinfo.Info{Revision: "34af007ca7daf0d4bce77ebb68d5041df17b9339", Stamped: false}
	note := buildSkillsOut("dev", inferred).Build.Note

	if strings.Contains(note, "git rev-parse HEAD") {
		t.Errorf("note = %q: an inferred revision must not be presented as a comparison a reader can trust", note)
	}
	if !strings.Contains(note, "worktree") {
		t.Errorf("note = %q: it must say why the revision is only a hint", note)
	}
	// The check that still works must still be offered.
	if !strings.Contains(note, "shasum") {
		t.Errorf("note = %q: the document-hash check is valid regardless of the revision and must survive", note)
	}
}

// A release install has no repo, no checkout and no Go toolchain -- advice to
// run git and rebuild from source is advice that reader cannot follow, and the
// connector already tracks the answer they can use (broker.json's
// latest_available_version, written by internal/updatecheck).
func TestGetSkillsGivesAReleaseBuildAdviceItsReaderCanFollow(t *testing.T) {
	note := buildSkillsOut("v1.2.3", stampedInfo).Build.Note

	if !strings.Contains(note, "v1.2.3") {
		t.Errorf("note = %q, want it to name the release the reader is running", note)
	}
	if !strings.Contains(note, "latest_available_version") {
		t.Errorf("note = %q, want it to point at the freshness answer this reader actually has", note)
	}
	for _, forbidden := range []string{"git rev-parse", "shasum", "go build"} {
		if strings.Contains(note, forbidden) {
			t.Errorf("note = %q tells a release install to run %q, which needs a source checkout it does not have", note, forbidden)
		}
	}
}

// The response an agent actually receives, end to end. The structured Build
// field is invisible to a host that surfaces only text content -- which is how
// the agent in issue #116 read this document -- so the markdown half must
// carry the provenance too, and the document itself must still arrive intact.
func TestGetSkillsCallResultCarriesTheDocumentAndItsProvenance(t *testing.T) {
	res, out := skillsCallResult("v9.9.9", stampedInfo)

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
	// Deliberately distinctive values: "unknown" or a short hex string could
	// be satisfied by skill.md's own prose, making the assertion pass while
	// the footer carried nothing.
	for _, want := range []string{"34af007ca7da", out.Build.Note, "v9.9.9"} {
		if !strings.Contains(text.Text, want) {
			t.Errorf("text content omits %q: a host that shows only text content would leave a reader "+
				"unable to tell a stale broker from a wrong document", want)
		}
	}
}

// Through the registered tool, over a real MCP session -- the only thing that
// proves the handler passes the broker's own version and provenance in, rather
// than something the tests supply.
func TestRegisteredGetSkillsToolServesTheDocumentAndAFooter(t *testing.T) {
	server := mcp.NewServer(&mcp.Implementation{Name: "revit-mcp-server-test", Version: "0.0.0"}, nil)
	RegisterSkills(server, "v4.5.6")

	clientTransport, serverTransport := mcp.NewInMemoryTransports()
	ctx := context.Background()
	if _, err := server.Connect(ctx, serverTransport, nil); err != nil {
		t.Fatalf("server.Connect: %v", err)
	}
	client := mcp.NewClient(&mcp.Implementation{Name: "test-client", Version: "0.0.0"}, nil)
	cs, err := client.Connect(ctx, clientTransport, nil)
	if err != nil {
		t.Fatalf("client.Connect: %v", err)
	}
	defer cs.Close()

	res, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "get_skills", Arguments: map[string]any{}})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	raw, err := json.Marshal(res.StructuredContent)
	if err != nil {
		t.Fatalf("marshal StructuredContent: %v", err)
	}
	var out GetSkillsOut
	if err := json.Unmarshal(raw, &out); err != nil {
		t.Fatalf("unmarshal into GetSkillsOut: %v", err)
	}

	if out.Build.Version != "v4.5.6" {
		t.Errorf("Build.Version = %q: the registration is not passing the broker's own version to the handler", out.Build.Version)
	}
	if out.Skill != skillFile {
		t.Error("the registered tool did not serve the embedded document verbatim")
	}
	sum := sha256.Sum256([]byte(skillFile))
	if want := hex.EncodeToString(sum[:])[:12]; out.Build.SkillHash != want {
		t.Errorf("Build.SkillHash = %q, want %q", out.Build.SkillHash, want)
	}
	text, ok := res.Content[0].(*mcp.TextContent)
	if !ok {
		t.Fatalf("content is %T, want *mcp.TextContent", res.Content[0])
	}
	if !strings.Contains(text.Text, "**Provenance.**") {
		t.Error("the registered tool served the document without its provenance footer")
	}
	if !strings.Contains(text.Text, "v4.5.6") {
		t.Error("the footer does not name the version the handler was registered with")
	}
}

// The footer's own contract, measured against the notes the product actually
// produces rather than a synthetic short one -- a cap fed a 27-byte note
// proves nothing about a 570-byte one.
func TestSkillFooterStaysAFooterForEveryRealNote(t *testing.T) {
	cases := map[string]SkillBuild{
		"dev, stamped":  buildSkillsOut("dev", stampedInfo).Build,
		"dev, inferred": buildSkillsOut("dev", buildinfo.Info{Revision: "34af007ca7daf0d4bce77ebb68d5041df17b9339"}).Build,
		"dev, unknown":  buildSkillsOut("dev", buildinfo.Info{}).Build,
		"release":       buildSkillsOut("v1.2.3", stampedInfo).Build,
	}
	// It is appended to a document already within ~1% of its token budget and
	// is charged to the same reader's context, so it must stay a footer
	// rather than grow into a section. The real notes run ~300-600 bytes.
	const maxFooterBytes = 800
	for name, b := range cases {
		footer := skillFooter(b)
		if len(footer) > maxFooterBytes {
			t.Errorf("%s: footer is %d bytes, over the %d-byte cap -- it is charged to the same context "+
				"budget as skill.md itself", name, len(footer), maxFooterBytes)
		}
		for _, want := range []string{b.Version, b.Revision, b.Note} {
			if !strings.Contains(footer, want) {
				t.Errorf("%s: footer = %q, missing %q", name, footer, want)
			}
		}
		if strings.Contains(footer, "tree not clean") {
			t.Errorf("%s: footer = %q warns about the tree for a clean build", name, footer)
		}
	}

	// A text-content reader never sees the structured `modified` field, so the
	// footer has to carry it: a revision built from a dirty tree does not
	// identify what is running and must not be presented as if it did.
	dirty := stampedInfo
	dirty.Modified = true
	if footer := skillFooter(buildSkillsOut("dev", dirty).Build); !strings.Contains(footer, "tree not clean") {
		t.Errorf("footer = %q: a build from an unclean tree must say so in the markdown too", footer)
	}
}
