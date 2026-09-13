// Package howto is the document model for the Rhino how-to corpus
// (rhino/docs/PRD.md §09 follow-on): the document and verification-stamp
// types, schema validation against the embedded JSON Schemas plus the
// cross-field rules a schema cannot express, JSONL corpus and sidecar loading
// with the one-line-per-lineage rule, and the join that tells a reader which
// Rhino versions a document is verified on. It knows nothing about search or
// tools; those are in howtosearch and mcpserver.
//
// This is the Rhino analog of revit/mcp-server/internal/howto, read-side only:
// the submission/local-overlay write path (Put/Absorb/Overlay, the local
// corpus and session sidecar) is deliberately absent in v1 — the seed corpus
// is maintainer-authored and the broker only serves it.
package howto

import (
	"crypto/sha256"
	"encoding/hex"
	"time"
)

// SchemaVersion is the document schema revision this package writes and fully
// understands. Documents declaring a higher version are still read (unknown
// fields are allowed) and reported via Corpus.NewerThanBroker.
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

// Provenance kinds. The Rhino corpus has no submission pipeline in v1, so the
// user-facing submission/local kinds are absent here.
const (
	ProvenanceHarness          = "harness"
	ProvenanceValidationCorpus = "validation-corpus"
	ProvenanceMaintainer       = "maintainer"
)

// Script languages a how-to script is run in. Rhino execute_script takes both,
// so a document names which one its script is written in (unlike Revit, whose
// single dialect is a const).
const (
	ScriptPython = "python"
	ScriptCSharp = "csharp-script"
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
	RhinoVersion string `json:"rhino_version,omitempty"`
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
// (schema: verify). Maintainer-facing, like Provenance; never returned to an
// agent.
//
// Rhino has no transaction mutation report like Revit's, so the contract is
// the two things a Rhino script run can be checked against cheaply: the net
// change in the active document's object count, and a substring the run's
// return value must contain. A nil/empty field is not asserted.
type Verify struct {
	// ExpectObjectDelta is the net change in RhinoDoc.Objects the run must
	// produce: a how-to that adds one object expects 1, a read-only one 0, a
	// create-then-undo one 0. Nil is not asserted.
	ExpectObjectDelta *int `json:"expect_object_delta,omitempty"`
	// ExpectReturnContains is a substring the run's return value must contain.
	ExpectReturnContains string `json:"expect_return_contains,omitempty"`
	// Execute carries extra execute_script arguments the sweep passes verbatim
	// (e.g. confirm_lifecycle_actions: true for a how-to that saves or closes).
	Execute map[string]any `json:"execute,omitempty"`
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
// schema/howto-verification-schema.json. Written only by the harness sweep.
type Stamp struct {
	ID               string    `json:"id"`
	Rev              int       `json:"rev"`
	ScriptSHA256     string    `json:"script_sha256"`
	RhinoVersion     string    `json:"rhino_version"`
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

// ScriptSHA256 is the hash a stamp binds to: the document's script text, byte
// for byte.
func ScriptSHA256(script string) string {
	sum := sha256.Sum256([]byte(script))
	return hex.EncodeToString(sum[:])
}

// Matches reports whether the stamp is for this exact document revision and
// script text. A stamp for another revision or a changed script is stale.
func (s Stamp) Matches(d *Document) bool {
	return s.ID == d.ID && s.Rev == d.Rev && s.ScriptSHA256 == ScriptSHA256(d.Script)
}
