package howto

import (
	"bytes"
	"embed"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"sync"

	"github.com/santhosh-tekuri/jsonschema/v6"
	"golang.org/x/text/language"
	"golang.org/x/text/message"
)

// printer renders the validator's error kinds; the library needs a real one.
var printer = message.NewPrinter(language.English)

// The JSON Schemas are the interchange contract (design note §1); this file
// embeds them so the broker validates with exactly the published text.
//
//go:embed schema/howto-schema.json schema/howto-verification-schema.json
var schemaFS embed.FS

var (
	compileOnce sync.Once
	docSchema   *jsonschema.Schema
	stampSchema *jsonschema.Schema
	compileErr  error
)

func schemas() (*jsonschema.Schema, *jsonschema.Schema, error) {
	compileOnce.Do(func() {
		c := jsonschema.NewCompiler()
		for _, name := range []string{"howto-schema.json", "howto-verification-schema.json"} {
			raw, err := schemaFS.ReadFile("schema/" + name)
			if err != nil {
				compileErr = err
				return
			}
			doc, err := jsonschema.UnmarshalJSON(bytes.NewReader(raw))
			if err != nil {
				compileErr = fmt.Errorf("howto: parsing %s: %w", name, err)
				return
			}
			if err := c.AddResource(name, doc); err != nil {
				compileErr = fmt.Errorf("howto: adding %s: %w", name, err)
				return
			}
		}
		if docSchema, compileErr = c.Compile("howto-schema.json"); compileErr != nil {
			return
		}
		stampSchema, compileErr = c.Compile("howto-verification-schema.json")
	})
	return docSchema, stampSchema, compileErr
}

// ValidationError lists every rule a document or stamp broke, schema and
// cross-field alike, so a submitter can fix them in one pass.
type ValidationError struct {
	What     string // "document <id>" or "stamp <id>@<rev>"
	Problems []string
}

func (e *ValidationError) Error() string {
	return fmt.Sprintf("howto: %s is invalid: %s", e.What, strings.Join(e.Problems, "; "))
}

// ValidateDocument checks raw JSON (one document) against the schema and the
// cross-field rules, and returns the decoded document.
func ValidateDocument(raw []byte) (*Document, error) {
	docSchema, _, err := schemas()
	if err != nil {
		return nil, err
	}
	var problems []string
	generic, err := jsonschema.UnmarshalJSON(bytes.NewReader(raw))
	if err != nil {
		return nil, &ValidationError{What: "document", Problems: []string{"not JSON: " + err.Error()}}
	}
	if err := docSchema.Validate(generic); err != nil {
		problems = append(problems, schemaProblems(err)...)
	}
	var d Document
	if err := json.Unmarshal(raw, &d); err != nil {
		problems = append(problems, "decoding: "+err.Error())
		return nil, &ValidationError{What: "document", Problems: problems}
	}
	d.Extra = extraFields(raw, documentKnownFields)
	problems = append(problems, crossFieldProblems(&d)...)
	if len(problems) > 0 {
		return &d, &ValidationError{What: "document " + d.ID, Problems: problems}
	}
	return &d, nil
}

// ValidateStamp checks raw JSON (one sidecar line) against its schema.
func ValidateStamp(raw []byte) (*Stamp, error) {
	_, stampSchema, err := schemas()
	if err != nil {
		return nil, err
	}
	generic, err := jsonschema.UnmarshalJSON(bytes.NewReader(raw))
	if err != nil {
		return nil, &ValidationError{What: "stamp", Problems: []string{"not JSON: " + err.Error()}}
	}
	var problems []string
	if err := stampSchema.Validate(generic); err != nil {
		problems = append(problems, schemaProblems(err)...)
	}
	var s Stamp
	if err := json.Unmarshal(raw, &s); err != nil {
		problems = append(problems, "decoding: "+err.Error())
	}
	if len(problems) > 0 {
		return &s, &ValidationError{What: fmt.Sprintf("stamp %s@%d", s.ID, s.Rev), Problems: problems}
	}
	return &s, nil
}

// crossFieldProblems are the rules howto-schema.json names as "enforced by
// the Go validator": absorbs excludes own id; contributors[].rev <= rev;
// updated_at >= created_at; hit/miss query shape; plus readable restatements
// of the schema's provenance conditionals, whose library message ("'not'
// failed") would not tell a submitter which field to fix.
func crossFieldProblems(d *Document) []string {
	var p []string
	for _, a := range d.Absorbs {
		if a == d.ID {
			p = append(p, "absorbs must not contain the document's own id")
			break
		}
	}
	for i, c := range d.Contributors {
		if c.Rev > d.Rev {
			p = append(p, fmt.Sprintf("contributors[%d].rev %d exceeds the document's rev %d", i, c.Rev, d.Rev))
		}
	}
	if d.Provenance.Kind == ProvenanceLocal && d.Provenance.ReviewedBy != "" {
		p = append(p, "a local document must not carry provenance.reviewed_by (only triage sets it)")
	}
	if d.Provenance.Kind == ProvenanceSubmission && d.Provenance.Ref == "" {
		p = append(p, "a submission must carry provenance.ref (the issue URL)")
	}
	if d.CreatedAt.IsZero() {
		p = append(p, "created_at is missing or zero")
	}
	if d.UpdatedAt.IsZero() {
		p = append(p, "updated_at is missing or zero")
	}
	if !d.CreatedAt.IsZero() && !d.UpdatedAt.IsZero() && d.UpdatedAt.Before(d.CreatedAt) {
		p = append(p, "updated_at is before created_at")
	}
	if d.Queries != nil {
		for i, q := range d.Queries.Hit {
			if q.Rank == 0 {
				p = append(p, fmt.Sprintf("queries.hit[%d] needs rank", i))
			}
			if q.Surfaced != "" {
				p = append(p, fmt.Sprintf("queries.hit[%d] must not carry surfaced (that is a miss field)", i))
			}
		}
		for i, q := range d.Queries.Miss {
			if q.Surfaced == "" {
				p = append(p, fmt.Sprintf("queries.miss[%d] needs surfaced", i))
			}
			if q.Rank != 0 {
				p = append(p, fmt.Sprintf("queries.miss[%d] must not carry rank (that is a hit field)", i))
			}
		}
	}
	return p
}

// schemaProblems flattens the library's error tree into one line per leaf.
func schemaProblems(err error) []string {
	var ve *jsonschema.ValidationError
	if !errors.As(err, &ve) {
		return []string{err.Error()}
	}
	var out []string
	var walk func(e *jsonschema.ValidationError)
	walk = func(e *jsonschema.ValidationError) {
		if len(e.Causes) == 0 {
			loc := "/" + strings.Join(e.InstanceLocation, "/")
			out = append(out, loc+": "+e.ErrorKind.LocalizedString(printer))
			return
		}
		for _, c := range e.Causes {
			walk(c)
		}
	}
	walk(ve)
	return out
}

var documentKnownFields = map[string]bool{
	"schema_version": true, "id": true, "rev": true, "kind": true, "title": true, "task": true,
	"queries": true, "members": true, "script": true, "script_language": true, "pitfalls": true,
	"tags": true, "api_since": true, "api_until": true, "contributors": true, "absorbs": true,
	"provenance": true, "verify": true, "created_at": true, "updated_at": true,
}

// extraFields keeps top-level fields this package does not know (a newer
// schema), so writing the document back preserves them.
func extraFields(raw []byte, known map[string]bool) map[string]any {
	var all map[string]json.RawMessage
	if json.Unmarshal(raw, &all) != nil {
		return nil
	}
	var extra map[string]any
	for k, v := range all {
		if known[k] {
			continue
		}
		if extra == nil {
			extra = map[string]any{}
		}
		var val any
		_ = json.Unmarshal(v, &val)
		extra[k] = val
	}
	return extra
}

// MarshalDocument writes a document as one JSON object with no trailing
// newline (the JSONL writer adds it). A nil Members slice is written as [],
// since the schema requires the array; preserved unknown fields ride along
// (key order is then alphabetical -- Go maps -- which is fine for JSONL).
func MarshalDocument(d *Document) ([]byte, error) {
	if d.Members == nil {
		cp := *d
		cp.Members = []string{}
		d = &cp
	}
	b, err := json.Marshal(d)
	if err != nil {
		return nil, err
	}
	if len(d.Extra) == 0 {
		return b, nil
	}
	var m map[string]any
	if err := json.Unmarshal(b, &m); err != nil {
		return nil, err
	}
	for k, v := range d.Extra {
		if _, taken := m[k]; !taken {
			m[k] = v
		}
	}
	return json.Marshal(m)
}
