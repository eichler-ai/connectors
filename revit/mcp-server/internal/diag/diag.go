// Package diag implements the shared diagnostic-record shape defined in
// PRD §01 ("Observability & error reporting standard"). The same shape is
// reused for the `notices[]` array on a successful tool result, the `data`
// field of a JSON-RPC `error`, and every NDJSON log record — this package is
// the single source of truth for that shape so it can't drift between call
// sites.
package diag

// Severity levels, per PRD §01. Auto-resolved warnings/info populate
// notices[] on an otherwise-successful result; error rolls back and surfaces
// through the JSON-RPC error path instead — the two channels never overlap.
const (
	SeverityDebug   = "debug"
	SeverityInfo    = "info"
	SeverityWarning = "warning"
	SeverityError   = "error"
)

// Record is the shared diagnostic-record shape from PRD §01.
type Record struct {
	Severity string         `json:"severity"`
	Code     string         `json:"code"`
	Source   string         `json:"source"`
	Message  string         `json:"message"`
	Detail   map[string]any `json:"detail,omitempty"`
	Remedy   []string       `json:"remedy,omitempty"`
}

// New builds a Record. message must be specific and concrete — it should
// name the concrete identifiers involved (execution_id/instance_id/
// document_id, whichever apply) and the actual underlying condition, never a
// generic wrapper like "An error occurred". source should match a real
// package/module name, e.g. "mcp-server.internal.execution".
func New(severity, code, source, message string) *Record {
	return &Record{
		Severity: severity,
		Code:     code,
		Source:   source,
		Message:  message,
	}
}

// WithDetail attaches structured, code-specific fields and returns the
// Record for chaining.
func (r *Record) WithDetail(detail map[string]any) *Record {
	r.Detail = detail
	return r
}

// WithRemedy attaches one or more suggested next steps and returns the
// Record for chaining. Per PRD §01, remedy is expected, not decorative —
// omit it only when there's genuinely nothing actionable to suggest.
func (r *Record) WithRemedy(steps ...string) *Record {
	r.Remedy = steps
	return r
}
