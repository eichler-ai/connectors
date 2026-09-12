// Package execution routes execute_script / poll_execution / cancel_execution
// to the right Rhino's connection (rhino/docs/PRD.md §05/§06). Unlike the
// Revit server it holds NO busy latch and NO grace escalation: the plug-in
// owns the run's state (it serialises runs per instance, answers `busy`,
// escalates to `unrecoverable`, keeps the ring buffer), so this side only
// mints execution ids, remembers which instance each id belongs to, and
// translates wire failures into the shared diagnostic record.
package execution

import (
	"context"
	"encoding/json"
	"fmt"
	"sync"
	"time"

	"github.com/google/uuid"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/internal/servercore/transport"
)

const source = "mcp-server.internal.execution"

// Bounds (CONVENTIONS.md): the id->instance map mirrors the plug-in's ring
// buffer (last 50 / 10 minutes) so a poll for an id the plug-in has evicted is
// answered with unknown-execution-id here without a round trip.
const (
	maxRoutes    = 50
	routeMaxAge  = 10 * time.Minute
	wireBufferMs = 5_000 // slack the wire call gets beyond the caller's timeout_ms
)

// Result is the tool-facing shape, one field per wire field the plug-in's
// ExecutionResultMessage writes (keep the two in step).
type Result struct {
	Status      string          `json:"status"`
	ExecutionID string          `json:"execution_id"`
	Output      string          `json:"output,omitempty"`
	ReturnValue string          `json:"return_value,omitempty"`
	Notices     []diag.Record   `json:"notices,omitempty"`
	Files       []FileRecord    `json:"files,omitempty"`
	Mutations   *MutationReport `json:"mutations,omitempty"`
	// Grasshopper is the solve report (PRD §10), present only when a solution ended during the run.
	Grasshopper *GrasshopperReport `json:"grasshopper,omitempty"`
	ErrorDetail *diag.Record       `json:"error,omitempty"`
	// LastRun is the run that completed on the same document before this one (PRD §05), so a
	// caller can see another client's work since its own last call.
	LastRun *LastRun `json:"last_run,omitempty"`
}

// GrasshopperReport mirrors the plug-in's GrasshopperReport (PRD §10): the solutions that ended during
// the run and every object that carried a runtime message or ended non-Computed. Typed (not raw) for the
// same reason as MutationReport -- so the MCP SDK derives a real output schema.
type GrasshopperReport struct {
	Solutions  []GrasshopperSolution        `json:"solutions"`
	Components []GrasshopperComponentReport `json:"components"`
}

// GrasshopperSolution is one ended solution.
type GrasshopperSolution struct {
	StartedAt  string  `json:"started_at"`
	DurationMs float64 `json:"duration_ms"`
	State      string  `json:"state"`
	Depth      int     `json:"depth"`
}

// GrasshopperComponentReport is one object's outcome in the solve.
type GrasshopperComponentReport struct {
	GUID        string               `json:"guid"`
	Nickname    string               `json:"nickname"`
	Type        string               `json:"type"`
	Phase       string               `json:"phase"`
	ProcessorMs float64              `json:"processor_ms"`
	Messages    []GrasshopperMessage `json:"messages"`
}

// GrasshopperMessage is one runtime message on an object.
type GrasshopperMessage struct {
	Severity string `json:"severity"`
	Text     string `json:"text"`
}

// MutationReport mirrors the plug-in's MutationReport (rhino/docs/PRD.md §07): what a successful
// run changed, net. Typed rather than raw so the MCP SDK derives a real output schema for it -- a
// json.RawMessage is schematised as a string and the SDK then rejects the object at validation.
type MutationReport struct {
	NetAdded     int                      `json:"net_added"`
	NetModified  int                      `json:"net_modified"`
	NetDeleted   int                      `json:"net_deleted"`
	ByObjectType map[string]MutationTally `json:"by_object_type,omitempty"`
	ByLayer      map[string]MutationTally `json:"by_layer,omitempty"`
}

// MutationTally is one bucket of a MutationReport.
type MutationTally struct {
	Added    int `json:"added"`
	Modified int `json:"modified"`
	Deleted  int `json:"deleted"`
}

// FileRecord is one published file (phase 5 fills these in).
type FileRecord struct {
	Name    string `json:"name"`
	Path    string `json:"path,omitempty"`
	Status  string `json:"status"`
	Message string `json:"message,omitempty"`
}

// IsTerminal reports whether no further poll will change the status.
func IsTerminal(status string) bool {
	switch status {
	case "success", "error", "cancelled", "unrecoverable":
		return true
	}
	return false
}

// Conns is what the router needs from the dialer.
type Conns interface {
	Conn(instanceID string) (*transport.Conn, bool)
	// Connected lists the instance ids with a live connection, for routing an
	// execution id this server did not mint (another server's, or one from
	// before this server restarted): the plug-in owns the record, so every
	// server may poll or cancel it (PRD §05).
	Connected() []string
}

type route struct {
	instanceID string
	mintedAt   time.Time
}

// Router maps execution ids to instances and forwards the three methods.
type Router struct {
	conns    Conns
	serverID string
	now      func() time.Time

	mu     sync.Mutex
	routes map[string]route
	order  []string // insertion order for eviction
}

func NewRouter(conns Conns, serverID string) *Router {
	return &Router{conns: conns, serverID: serverID, now: time.Now, routes: map[string]route{}}
}

// Options are execute_script's optional parameters.
type Options struct {
	Language                string
	DocumentID              string
	GrasshopperDocumentID   string
	TimeoutMs               int
	MaxDurationMs           int
	ConfirmLifecycleActions bool
	Label                   string
}

// ExecuteScript mints an id (namespaced by this server so two servers can never
// collide) and forwards. The plug-in answers inline when the script finishes
// within timeout_ms, else with pending/running/busy.
func (r *Router) ExecuteScript(ctx context.Context, instanceID, script string, opts Options) (*Result, *diag.Record) {
	conn, ok := r.conns.Conn(instanceID)
	if !ok {
		return nil, diag.New(diag.SeverityError, "instance-not-found", source,
			fmt.Sprintf("no connected Rhino instance has instance_id %q", instanceID)).
			WithRemedy("call list_instances and pick a current instance_id; a Rhino that just started appears within seconds")
	}
	executionID := "exec-" + r.serverID + "-" + uuid.NewString()[:8]
	r.remember(executionID, instanceID)
	params := map[string]any{
		"execution_id":              executionID,
		"agent_client_id":           r.serverID,
		"language":                  opts.Language,
		"script":                    script,
		"document_id":               opts.DocumentID,
		"gh_document_id":            opts.GrasshopperDocumentID,
		"timeout_ms":                opts.TimeoutMs,
		"max_duration_ms":           opts.MaxDurationMs,
		"confirm_lifecycle_actions": opts.ConfirmLifecycleActions,
	}
	if opts.Label != "" {
		params["label"] = opts.Label
	}
	return r.call(ctx, conn, "execute_script", executionID, opts.TimeoutMs, params)
}

// UndoRedo posts the plug-in's undo_redo method (PRD §07): direction "undo" or "redo", confirm,
// and the document. The plug-in decides from its own record of document changes made outside its
// runs whether the top entry is the connector's work and refuses (undo-confirmation-required)
// otherwise unless confirmed.
func (r *Router) UndoRedo(ctx context.Context, instanceID, direction string, confirm bool, timeoutMs int, documentID string) (*Result, *diag.Record) {
	conn, ok := r.conns.Conn(instanceID)
	if !ok {
		return nil, diag.New(diag.SeverityError, "instance-not-found", source,
			fmt.Sprintf("no connected Rhino instance has instance_id %q", instanceID)).
			WithRemedy("call list_instances and pick a current instance_id")
	}
	executionID := "exec-" + r.serverID + "-" + uuid.NewString()[:8]
	r.remember(executionID, instanceID)
	return r.call(ctx, conn, "undo_redo", executionID, timeoutMs, map[string]any{
		"execution_id": executionID,
		"direction":    direction,
		"confirm":      confirm,
		"document_id":  documentID,
		"timeout_ms":   timeoutMs,
	})
}

// DocSaveState is one open document's save state from restart_snapshot (PRD §10/§15).
type DocSaveState struct {
	Kind     string `json:"kind"` // "rhino" | "grasshopper"
	Title    string `json:"title"`
	Path     string `json:"path,omitempty"`
	Modified bool   `json:"modified"`
}

// RestartSnapshot asks the plug-in for every open document's save state, for the restart tool's
// unsaved-work guard and reopen list. Read-only; the plug-in does not exit.
func (r *Router) RestartSnapshot(ctx context.Context, instanceID string) ([]DocSaveState, *diag.Record) {
	conn, ok := r.conns.Conn(instanceID)
	if !ok {
		return nil, diag.New(diag.SeverityError, "instance-not-found", source,
			fmt.Sprintf("no connected Rhino instance has instance_id %q", instanceID)).
			WithRemedy("call list_instances and pick a current instance_id")
	}
	wctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	raw, rpcErr, err := conn.Call(wctx, "restart_snapshot", map[string]any{})
	if err != nil {
		return nil, diag.New(diag.SeverityError, "wire-call-failed", source,
			fmt.Sprintf("restart_snapshot did not complete: %v", err)).
			WithRemedy("check the Rhino is responsive (list_instances) and retry")
	}
	if rpcErr != nil {
		if rpcErr.Data != nil {
			return nil, rpcErr.Data
		}
		return nil, diag.New(diag.SeverityError, "bridge-error", source,
			fmt.Sprintf("restart_snapshot was refused by the plug-in: %s", rpcErr.Message))
	}
	var res struct {
		Documents []DocSaveState `json:"documents"`
	}
	if err := json.Unmarshal(raw, &res); err != nil {
		return nil, diag.New(diag.SeverityError, "wire-decode-failed", source,
			fmt.Sprintf("restart_snapshot returned a result this server could not decode: %v", err))
	}
	return res.Documents, nil
}

// InspectObject is one object in an InspectResult: its identity, canvas geometry (Pivot [x,y] and Bounds
// [x,y,width,height]) and component-level wiring (neighbour guids). Keep in step with the plug-in's
// InspectDefinitionMessage.
type InspectObject struct {
	GUID       string    `json:"guid"`
	Nickname   string    `json:"nickname"`
	Name       string    `json:"name"`
	Kind       string    `json:"kind"`
	Pivot      []float64 `json:"pivot"`
	Bounds     []float64 `json:"bounds"`
	Upstream   []string  `json:"upstream"`
	Downstream []string  `json:"downstream"`
}

// InspectResult is inspect_gh_definition's result (PRD §10): a read-only snapshot of an open Grasshopper
// definition's structure.
type InspectResult struct {
	GrasshopperDocumentID string          `json:"gh_document_id"`
	Title                 string          `json:"title"`
	Path                  string          `json:"path,omitempty"`
	ObjectCount           int             `json:"object_count"`
	Enabled               bool            `json:"enabled"`
	MatchCount            int             `json:"match_count"`
	Offset                int             `json:"offset"`
	Truncated             bool            `json:"truncated"`
	Objects               []InspectObject `json:"objects"`
}

// InspectDefinition asks the plug-in for an open Grasshopper definition's structure (its objects, their
// canvas positions and their wiring), for the inspect_gh_definition tool. Read-only. An empty ghDocumentID
// means the active canvas definition.
func (r *Router) InspectDefinition(ctx context.Context, instanceID, ghDocumentID, nameFilter string, offset, limit int) (*InspectResult, *diag.Record) {
	conn, ok := r.conns.Conn(instanceID)
	if !ok {
		return nil, diag.New(diag.SeverityError, "instance-not-found", source,
			fmt.Sprintf("no connected Rhino instance has instance_id %q", instanceID)).
			WithRemedy("call list_instances and pick a current instance_id")
	}
	wctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	params := map[string]any{}
	if ghDocumentID != "" {
		params["gh_document_id"] = ghDocumentID
	}
	if nameFilter != "" {
		params["name_filter"] = nameFilter
	}
	if offset > 0 {
		params["offset"] = offset
	}
	if limit > 0 {
		params["limit"] = limit
	}
	raw, rpcErr, err := conn.Call(wctx, "inspect_gh_definition", params)
	if err != nil {
		return nil, diag.New(diag.SeverityError, "wire-call-failed", source,
			fmt.Sprintf("inspect_gh_definition did not complete: %v", err)).
			WithRemedy("check the Rhino is responsive (list_instances) and retry")
	}
	if rpcErr != nil {
		if rpcErr.Data != nil {
			return nil, rpcErr.Data
		}
		return nil, diag.New(diag.SeverityError, "bridge-error", source,
			fmt.Sprintf("inspect_gh_definition was refused by the plug-in: %s", rpcErr.Message))
	}
	var res InspectResult
	if err := json.Unmarshal(raw, &res); err != nil {
		return nil, diag.New(diag.SeverityError, "wire-decode-failed", source,
			fmt.Sprintf("inspect_gh_definition returned a result this server could not decode: %v", err))
	}
	return &res, nil
}

// PollExecution forwards to the owning instance; a wait up to timeoutMs happens plug-in side.
func (r *Router) PollExecution(ctx context.Context, executionID string, timeoutMs int) (*Result, *diag.Record) {
	conn, drec := r.lookup(ctx, executionID)
	if drec != nil {
		return nil, drec
	}
	return r.call(ctx, conn, "poll_execution", executionID, timeoutMs, map[string]any{"execution_id": executionID, "timeout_ms": timeoutMs})
}

// CancelExecution forwards the cooperative cancel (PRD §06).
func (r *Router) CancelExecution(ctx context.Context, executionID string) (*Result, *diag.Record) {
	conn, drec := r.lookup(ctx, executionID)
	if drec != nil {
		return nil, drec
	}
	return r.call(ctx, conn, "cancel_execution", executionID, 0, map[string]any{"execution_id": executionID})
}

func (r *Router) remember(executionID, instanceID string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	now := r.now()
	if _, known := r.routes[executionID]; !known {
		r.order = append(r.order, executionID) // never twice: a re-remember would inflate the order list
	}
	r.routes[executionID] = route{instanceID: instanceID, mintedAt: now}
	for len(r.order) > 0 && (len(r.order) > maxRoutes || now.Sub(r.routes[r.order[0]].mintedAt) > routeMaxAge) {
		delete(r.routes, r.order[0])
		r.order = r.order[1:]
	}
}

// lookup finds the connection for an execution id: the instance this server
// minted it for, else -- an id minted by another server, or by this one before
// a restart -- every connected instance in turn is asked; the plug-in that owns
// the record answers, the others say unknown-execution-id.
func (r *Router) lookup(ctx context.Context, executionID string) (*transport.Conn, *diag.Record) {
	r.mu.Lock()
	rt, ok := r.routes[executionID]
	r.mu.Unlock()
	if ok {
		conn, ok := r.conns.Conn(rt.instanceID)
		if !ok {
			return nil, diag.New(diag.SeverityError, "instance-disconnected", source,
				fmt.Sprintf("the Rhino instance %q that ran execution %q is no longer connected", rt.instanceID, executionID)).
				WithRemedy("call list_instances; if the instance is back, poll again -- the plug-in keeps recent results for 10 minutes")
		}
		return conn, nil
	}
	for _, id := range r.conns.Connected() {
		conn, ok := r.conns.Conn(id)
		if !ok {
			continue
		}
		pctx, cancel := context.WithTimeout(ctx, 3*time.Second)
		raw, rpcErr, err := conn.Call(pctx, "poll_execution", map[string]any{"execution_id": executionID, "timeout_ms": 0})
		cancel()
		if err != nil || rpcErr != nil {
			continue
		}
		// Ownership is proven by the plug-in echoing the id, not by the absence of an error.
		var probe struct {
			ExecutionID string `json:"execution_id"`
		}
		if json.Unmarshal(raw, &probe) == nil && probe.ExecutionID == executionID {
			r.remember(executionID, id)
			return conn, nil
		}
	}
	return nil, diag.New(diag.SeverityError, "unknown-execution-id", source,
		fmt.Sprintf("no connected Rhino knows execution_id %q (never started, or older than the plug-in's 10-minute result buffer)", executionID)).
		WithRemedy("start a new execution with execute_script")
}

func (r *Router) call(ctx context.Context, conn *transport.Conn, method, executionID string, timeoutMs int, params any) (*Result, *diag.Record) {
	wctx, cancel := context.WithTimeout(ctx, time.Duration(timeoutMs+wireBufferMs)*time.Millisecond)
	defer cancel()
	raw, rpcErr, err := conn.Call(wctx, method, params)
	if err != nil {
		return nil, diag.New(diag.SeverityError, "wire-call-failed", source,
			fmt.Sprintf("%s for execution %q did not complete: %v", method, executionID, err)).
			WithDetail(map[string]any{"execution_id": executionID}).
			WithRemedy("poll_execution with the same execution_id once the connection is back; the plug-in keeps the result")
	}
	if rpcErr != nil {
		if rpcErr.Data != nil {
			return nil, rpcErr.Data
		}
		return nil, diag.New(diag.SeverityError, "bridge-error", source,
			fmt.Sprintf("%s for execution %q was refused by the plug-in: %s", method, executionID, rpcErr.Message)).
			WithDetail(map[string]any{"execution_id": executionID})
	}
	var res Result
	if err := json.Unmarshal(raw, &res); err != nil {
		return nil, diag.New(diag.SeverityError, "wire-decode-failed", source,
			fmt.Sprintf("%s for execution %q returned a result this server could not decode: %v", method, executionID, err)).
			WithDetail(map[string]any{"execution_id": executionID}).
			WithRemedy("the plug-in and server builds may disagree; update both")
	}
	if res.ExecutionID == "" {
		res.ExecutionID = executionID // the plug-in echoes it; a build that omits it still routes
	}
	return &res, nil
}
