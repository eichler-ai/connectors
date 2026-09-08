// Package diag is the hub's copy of the diagnostic-record shape the Revit
// connector established (Revit PRD §01) and the hub PRD adopts unchanged
// (§12). One shape serves three channels — the notices[] on a result, the
// error carried by a failed tool call, and structured log lines — so it lives
// in one importable package, outside internal/, where a connector package and
// (later) a local stdio server can use it without depending on hub runtime.
package diag

// Severity levels. info and warning ride along in notices[] on an otherwise
// successful result; error is the outcome itself and travels on the error
// channel instead — the two never overlap.
const (
	SeverityInfo    = "info"
	SeverityWarning = "warning"
	SeverityError   = "error"
)

// Record is the shared diagnostic record.
type Record struct {
	Severity string `json:"severity"`
	// Code is a stable kebab-case identifier an agent can branch on
	// (no-bridge, timeout, expect-mismatch, …); Message is for reading.
	Code    string `json:"code"`
	Source  string `json:"source"`
	Message string `json:"message"`
	// Detail carries code-specific structured fields; Remedy lists concrete
	// next steps and is expected wherever there is something actionable.
	Detail map[string]any `json:"detail,omitempty"`
	Remedy []string       `json:"remedy,omitempty"`
}

// New builds a Record. message should name the concrete identifiers involved
// (instance_id, exec id, workbook) and the actual condition, never a generic
// wrapper; source is the package that raised it, e.g. "hub.bridge".
func New(severity, code, source, message string) *Record {
	return &Record{Severity: severity, Code: code, Source: source, Message: message}
}

// WithDetail attaches structured fields and returns the Record for chaining.
func (r *Record) WithDetail(detail map[string]any) *Record {
	r.Detail = detail
	return r
}

// WithRemedy attaches suggested next steps and returns the Record for chaining.
func (r *Record) WithRemedy(steps ...string) *Record {
	r.Remedy = steps
	return r
}

// Error makes a Record usable as a Go error where a caller wants to hand the
// whole record up rather than flatten it to a string.
func (r *Record) Error() string { return r.Code + ": " + r.Message }
