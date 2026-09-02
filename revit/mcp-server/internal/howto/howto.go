// Package howto is the document model for the how-to corpus
// (revit/docs/howto-corpus-design.md, revit/docs/howto-seed-plan.md): the
// document and verification-stamp types, schema validation against the
// embedded JSON Schemas plus the cross-field rules a schema cannot express,
// JSONL corpus and sidecar loading with the one-line-per-lineage rule, and
// the join that tells a reader which Revit versions a document is verified
// on. It knows nothing about search or tools; those come in later steps.
package howto

import (
	"crypto/sha256"
	"encoding/hex"
	"time"
)

// SchemaVersion is the document schema revision this package writes and
// fully understands. Documents declaring a higher version are still read
// (unknown fields are allowed) and reported via Corpus.NewerThanBroker.
const SchemaVersion = 1

// Kind of a document.
const (
	KindHowTo    = "howto"
	KindPitfall  = "pitfall"
	KindNegative = "negative"
)

// Contributor roles.
const (
	RoleAuthor      = "author"
	RoleContributor = "contributor"
	RoleReviewer    = "reviewer"
)

// Provenance kinds.
const (
	ProvenanceHarness          = "harness"
	ProvenanceValidationCorpus = "validation-corpus"
	ProvenanceSubmission       = "submission"
	ProvenanceLocal            = "local"
	ProvenanceMaintainer       = "maintainer"
)

// Document is one how-to, mirroring schema/howto-schema.json. Unknown fields
// from a newer schema are preserved in Extra so a re-write does not drop them.
type Document struct {
	SchemaVersion int            `json:"schema_version"`
	ID            string         `json:"id"`
	Rev           int            `json:"rev"`
	Kind          string         `json:"kind"`
	Title         string         `json:"title"`
	Task          string         `json:"task"`
	Queries       *Queries       `json:"queries,omitempty"`
	Members       []string       `json:"members"`
	Script        string         `json:"script,omitempty"`
	ScriptLang    string         `json:"script_language,omitempty"`
	Pitfalls      []Pitfall      `json:"pitfalls,omitempty"`
	Tags          []string       `json:"tags,omitempty"`
	APISince      string         `json:"api_since,omitempty"`
	APIUntil      string         `json:"api_until,omitempty"`
	Contributors  []Contributor  `json:"contributors,omitempty"`
	Absorbs       []string       `json:"absorbs,omitempty"`
	Provenance    Provenance     `json:"provenance"`
	Verify        *Verify        `json:"verify,omitempty"`
	CreatedAt     time.Time      `json:"created_at"`
	UpdatedAt     time.Time      `json:"updated_at"`
	Extra         map[string]any `json:"-"`
}

// Queries records real phrasings: what found the answer and what missed.
type Queries struct {
	Hit  []Query `json:"hit,omitempty"`
	Miss []Query `json:"miss,omitempty"`
}

// Query is one recorded phrasing.
type Query struct {
	Text         string `json:"text"`
	Tool         string `json:"tool,omitempty"`
	Rank         int    `json:"rank,omitempty"`
	Surfaced     string `json:"surfaced,omitempty"`
	RevitVersion string `json:"revit_version,omitempty"`
	Ranker       string `json:"ranker,omitempty"`
}

// Pitfall is one mistake the how-to avoids.
type Pitfall struct {
	Symptom string   `json:"symptom"`
	Cause   string   `json:"cause"`
	Fix     string   `json:"fix"`
	Members []string `json:"members,omitempty"`
}

// Verify is what the tier-2 sweep asserts beyond "the script ran"
// (schema: verify). Maintainer-facing, like Provenance.
type Verify struct {
	Mutations        *ExpectedMutations `json:"mutations,omitempty"`
	Execute          map[string]any     `json:"execute,omitempty"`
	CreatesDocuments bool               `json:"creates_documents,omitempty"`
}

// ExpectedMutations is the net change report a how-to's run must produce.
// A nil counter is not asserted; NetModifiedMin replaces NetModified where
// the exact count is version-dependent.
type ExpectedMutations struct {
	NetCreated     *int                        `json:"net_created,omitempty"`
	NetModified    *int                        `json:"net_modified,omitempty"`
	NetDeleted     *int                        `json:"net_deleted,omitempty"`
	NetModifiedMin *int                        `json:"net_modified_min,omitempty"`
	ByCategory     map[string]ExpectedCategory `json:"by_category,omitempty"`
}

// ExpectedCategory is one by_category entry.
type ExpectedCategory struct {
	NetCreated  *int `json:"net_created,omitempty"`
	NetModified *int `json:"net_modified,omitempty"`
}

// Contributor is one opt-in credit entry.
type Contributor struct {
	Handle string     `json:"handle"`
	Role   string     `json:"role"`
	Rev    int        `json:"rev"`
	At     *time.Time `json:"at,omitempty"`
}

// Provenance is maintainer-facing and never returned to an agent.
type Provenance struct {
	Kind       string     `json:"kind"`
	Ref        string     `json:"ref,omitempty"`
	ReviewedBy string     `json:"reviewed_by,omitempty"`
	ReviewedAt *time.Time `json:"reviewed_at,omitempty"`
}

// Stamp is one verification record, mirroring
// schema/howto-verification-schema.json. Written only by the harness sweep
// (or, weaker, by the submitting session for its own local document).
type Stamp struct {
	ID               string    `json:"id"`
	Rev              int       `json:"rev"`
	ScriptSHA256     string    `json:"script_sha256"`
	RevitVersion     string    `json:"revit_version"`
	Status           string    `json:"status"` // "passed" | "failed"
	At               time.Time `json:"at"`
	By               string    `json:"by"` // "harness" | "session"
	ConnectorVersion string    `json:"connector_version,omitempty"`
	Diagnostic       string    `json:"diagnostic,omitempty"`
}

// Stamp statuses and authors.
const (
	StampPassed = "passed"
	StampFailed = "failed"
	ByHarness   = "harness"
	BySession   = "session"
)

// ScriptSHA256 is the hash a stamp binds to: the document's script text,
// byte for byte.
func ScriptSHA256(script string) string {
	sum := sha256.Sum256([]byte(script))
	return hex.EncodeToString(sum[:])
}

// Matches reports whether the stamp is for this exact document revision and
// script text. A stamp for another revision or a changed script is stale.
func (s Stamp) Matches(d *Document) bool {
	return s.ID == d.ID && s.Rev == d.Rev && s.ScriptSHA256 == ScriptSHA256(d.Script)
}
