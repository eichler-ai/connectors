package connector

import (
	"strings"
	"testing"
)

// TestSkillFileStaysWithinItsBudget is the Excel counterpart of the Revit
// server's skill.md budget test. get_skills is loaded as orientation at the
// start of a session and competes with everything else for the caller's
// context, so its size is a real constraint. The budget is raised
// deliberately as features land (file exchange, pairing) — never trimmed
// silently to fit, and never bumped just because a paragraph was added.
func TestSkillFileStaysWithinItsBudget(t *testing.T) {
	if len(skill) == 0 {
		t.Fatal("skill.md is empty")
	}
	// Claude Code caps MCP output at 25,000 tokens; 3 bytes/token is
	// pessimistic for English prose and code. The budget is 12% of the
	// ceiling — this document is a contract plus a quirks list, smaller than
	// Revit's by design because Office.js itself needs no explaining (§10).
	const pessimisticBytesPerToken = 3
	const ceilingTokens = 25000
	const budgetTokens = ceilingTokens * 12 / 100
	approx := len(skill) / pessimisticBytesPerToken
	if approx > budgetTokens {
		t.Errorf("skill.md is ~%d tokens (%d bytes), over the %d-token budget by ~%d. "+
			"Raise the budget in this test with a comment saying which feature earned it, or move reference "+
			"material behind a tool; do not trim contract or quirk lines to fit.", approx, len(skill), budgetTokens, approx-budgetTokens)
	}
	t.Logf("skill.md is ~%d tokens (%d bytes); budget %d tokens", approx, len(skill), budgetTokens)
}

// TestSkillFileMatchesTheToolSurface guards the two things the document
// promises that code enforces: the tools it lists exist, and the limits it
// states are the ones the hub applies.
func TestSkillFileMatchesTheToolSurface(t *testing.T) {
	doc := string(skill)
	for _, want := range []string{"`get_skills`", "`list_instances`", "`get_status`", "`execute_script`",
		"`expect: {workbook, sheet}`", "`target-implicit`", "`expect-mismatch`", "16 MiB", "30 s", "600 s"} {
		if !strings.Contains(doc, want) {
			t.Errorf("skill.md does not mention %s", want)
		}
	}
	for _, stale := range []string{"export_file", "import_workbook", "`pair`"} {
		if strings.Contains(doc, stale) {
			t.Errorf("skill.md mentions %s, which is not in this release", stale)
		}
	}
}
