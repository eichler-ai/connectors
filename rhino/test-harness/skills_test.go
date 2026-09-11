//go:build harness

package harness_test

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// get_skills needs no connected Rhino, so this runs whether or not a Rhino is up. It pins the round
// trip an agent actually makes: the guide comes back whole, as markdown text with the provenance
// footer, and the reported content hash matches the skill.md in the repo (issue #116's check that a
// reader can tell "the guide is wrong" from "the broker is old").
func TestGetSkillsReturnsTheGuideWithProvenance(t *testing.T) {
	c := startServer(t)
	raw, err := c.CallTool("get_skills", map[string]any{}, 10*time.Second)
	if err != nil {
		t.Fatalf("get_skills: %v", err)
	}
	var tr toolResult
	if err := json.Unmarshal(raw, &tr); err != nil {
		t.Fatalf("decode envelope: %v\n%s", err, raw)
	}
	if tr.IsError {
		t.Fatalf("get_skills reported an error: %s", raw)
	}

	var out struct {
		Format string `json:"format"`
		Skill  string `json:"skill"`
		Build  struct {
			Version   string `json:"version"`
			Revision  string `json:"revision"`
			SkillHash string `json:"skill_sha256_prefix"`
			Note      string `json:"note"`
		} `json:"build"`
	}
	if err := json.Unmarshal(tr.StructuredContent, &out); err != nil {
		t.Fatalf("decode structuredContent: %v\n%s", err, tr.StructuredContent)
	}
	if out.Format != "markdown" || !strings.HasPrefix(out.Skill, "# Working with Rhino") {
		t.Fatalf("unexpected skill payload: format=%q head=%.40q", out.Format, out.Skill)
	}
	// The text content (what a text-only host shows) is the guide plus the footer.
	if len(tr.Content) == 0 || !strings.Contains(tr.Content[0].Text, "**Provenance.** Served by rhino-mcp-server") {
		t.Fatalf("text content missing the provenance footer: %.200q", firstText(tr))
	}

	// The reported hash must match the file this checkout ships. A mismatch is exactly issue #116:
	// the running broker is not this source.
	root, err := filepath.Abs("../mcp-server/internal/mcpserver/skill.md")
	if err != nil {
		t.Fatal(err)
	}
	onDisk, err := os.ReadFile(root)
	if err != nil {
		t.Skipf("skill.md not readable from the harness dir (%v); hash cross-check skipped", err)
	}
	sum := sha256.Sum256(onDisk)
	want := hex.EncodeToString(sum[:])[:12]
	if out.Build.SkillHash != want {
		t.Fatalf("get_skills reports skill_sha256_prefix %q but the repo's skill.md hashes to %q -- the running broker is not this source (rebuild it)", out.Build.SkillHash, want)
	}
	if string(onDisk) != out.Skill {
		t.Fatal("the served guide differs from the repo's skill.md byte-for-byte despite matching hashes (impossible unless the hash is faked)")
	}
}

func firstText(tr toolResult) string {
	if len(tr.Content) > 0 {
		return tr.Content[0].Text
	}
	return ""
}
