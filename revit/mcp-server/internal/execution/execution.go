// Package execution implements the broker's side of PRD §06 (Threading &
// script execution): routing execute_script/poll_execution/cancel_execution
// to the right Revit instance's wire connection, and the two-shape response
// contract (completed result, or {status, execution_id}) that keeps an MCP
// call from ever hanging on a slow or stuck script.
//
// The manager treats each wire round trip as synchronous: the add-in is
// expected to wait internally (it owns the ExternalEvent/UI-thread state)
// up to the caller's timeout_ms before answering, either with a completed
// result or with the current {status, execution_id}. The manager's own
// responsibility is routing, busy-instance detection, and translating any
// wire-level failure into the shared diagnostic-record shape — not
// reimplementing the add-in's own polling loop.
package execution

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"sync"
	"time"

	"github.com/google/uuid"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

// Status is one of the values execute_script/poll_execution/cancel_execution
// can report, per PRD §06.
type Status string

const (
	StatusPending       Status = "pending"
	StatusRunning       Status = "running"
	StatusBusy          Status = "busy"
	StatusSuccess       Status = "success"
	StatusError         Status = "error"
	StatusCancelled     Status = "cancelled"
	StatusUnrecoverable Status = "unrecoverable"

	// StatusIdle is an instance-level state (list_instances, PRD §05) — an
	// instance with no active execution. It's never returned as an
	// execute_script/poll_execution/cancel_execution *result*, only from
	// StatusForInstance below; kept in this same enum since it shares the
	// same underlying concept, not to be confused with the per-call shapes.
	StatusIdle Status = "idle"
)

// IsTerminal reports whether s is a final state — no further
// poll_execution/cancel_execution against it will change it.
func IsTerminal(s Status) bool {
	switch s {
	case StatusSuccess, StatusError, StatusCancelled, StatusUnrecoverable:
		return true
	default:
		return false
	}
}

// Result is the shape returned to the MCP tool layer by ExecuteScript,
// PollExecution, and CancelExecution — either a completed result (Status
// success/error/cancelled/unrecoverable, with Output/Notices/ErrorDetail
// populated as relevant) or a non-terminal status pointing at ExecutionID
// for the caller to poll (PRD §06's two-shape contract).
type Result struct {
	Status      Status        `json:"status"`
	ExecutionID string        `json:"execution_id"`
	Output      string        `json:"output,omitempty"`
	Notices     []diag.Record `json:"notices,omitempty"`
	Files       []FileRecord  `json:"files,omitempty"`
	ErrorDetail *diag.Record  `json:"error,omitempty"`
}

// FileRecord mirrors the add-in's per-published-file report (PRD §09):
// one entry per Publish() call a script made, independent of the run's
// own overall success/failure — see ScriptExecutionOutcome.Files on the
// C# side for the invariant this preserves.
type FileRecord struct {
	Name    string `json:"name"`
	Path    string `json:"path"`
	Status  string `json:"status"`
	Message string `json:"message,omitempty"`
}

type record struct {
	instanceID string
	status     Status
	result     *Result
	// settledAt is stamped when status turns terminal; zero while the
	// execution is still live. It drives pruneSettledLocked's eviction.
	settledAt time.Time
}

const (
	// maxSettledExecutions and settledRetention bound how many terminal
	// execution records the broker retains for late poll_execution calls,
	// mirroring the add-in's own bounded replay buffer (PRD §05: "a small
	// ring buffer of recent execution results (last N / ~10 minutes)").
	// Without a bound the primary — a long-running process — grew its
	// executions map by one record per execute_script for its whole
	// lifetime (v1 integrated review). Non-terminal records are never
	// evicted: they are live state, and their instance's busy latch
	// depends on them. A poll_execution against an evicted id gets
	// unknown-execution-id — the same answer PRD §05 already specifies
	// for an id the add-in's own bounded buffer no longer knows.
	maxSettledExecutions = 200
	settledRetention     = 10 * time.Minute
)

// Manager owns the live set of instance wire connections and in-flight
// executions.
type Manager struct {
	mu               sync.Mutex
	conns            map[string]*transport.Conn
	executions       map[string]*record
	activeByInstance map[string]string
	unrecoverable    map[string]bool

	newID func() string
	now   func() time.Time

	// graceMs is how long, per PRD §06 ("Cancellation starts a grace
	// timer (default ~5-10s)"), a cancelled-but-not-yet-terminal execution
	// gets before the broker gives up on it and flips the owning instance
	// to unrecoverable — provided the connection that was asked to cancel
	// is still the instance's current one when the timer fires (issue #47;
	// see escalateUnrecoverable). Overridable (tests only) so the
	// escalation path can be exercised without a real multi-second wait.
	graceMs int
	// afterFunc schedules f to run after d elapses, returning a handle
	// whose Stop cancels it — the same shape as time.AfterFunc, which is
	// the default. Overridable in tests to make the grace-period
	// escalation and max-duration auto-cancel deterministic instead of
	// depending on real wall-clock timers.
	afterFunc func(d time.Duration, f func()) *time.Timer
}

// NewManager builds an empty Manager.
func NewManager() *Manager {
	return &Manager{
		conns:            make(map[string]*transport.Conn),
		executions:       make(map[string]*record),
		activeByInstance: make(map[string]string),
		unrecoverable:    make(map[string]bool),
		newID:            func() string { return "exec-" + uuid.NewString() },
		now:              time.Now,
		graceMs:          10_000,
		afterFunc:        time.AfterFunc,
	}
}

// AttachInstance registers the wire connection to use for instanceID and
// returns the connection it displaced, if any. A second call for the same
// instanceID (e.g. after a reconnect) replaces the prior connection. The
// caller is expected to Close a non-nil displaced connection: it's usually
// a half-open socket left over from a network blip, and closing it is what
// unblocks its serve goroutine so its own teardown can run — harmlessly,
// per DetachInstance's identity check below — instead of the socket and
// goroutine leaking until TCP itself gives up.
func (m *Manager) AttachInstance(instanceID string, conn *transport.Conn) *transport.Conn {
	m.mu.Lock()
	defer m.mu.Unlock()
	prev := m.conns[instanceID]
	m.conns[instanceID] = conn
	if prev == conn {
		return nil
	}
	return prev
}

// DetachInstance drops the wire connection for instanceID — but only if
// conn is still the instance's *current* connection — and reports whether
// it detached anything. The identity check is load-bearing, not defensive
// trim: a half-open TCP drop can leave the old connection's serve goroutine
// blocked in its read loop long after the add-in has redialed and
// re-registered the same stable instance_id (PRD §05) on a new connection.
// When the old socket finally errors out, a detach keyed by instance_id
// alone would tear down the live replacement — the instance vanishes from
// routing while the add-in, seeing a perfectly healthy connection, never
// re-registers, permanently.
//
// A detach that applies also clears the instance's busy bookkeeping: a
// disconnected instance can't still be "busy" once its connection is gone
// (otherwise every reconnect would permanently wedge execute_script behind
// an execution that will never complete). In-flight execution *records* are
// left as-is (a subsequent poll/cancel against them will surface an
// "instance disconnected" diagnostic rather than silently vanishing).
//
// The unrecoverable latch is deliberately NOT cleared here. PRD §05: an
// instance_id is stable for the life of the Revit *process*, independent of
// any particular connection — a reconnect under the same instance_id is a
// network blip, the same wedged Revit process reappearing, not a recovery.
// A genuine recovery (a Revit restart) mints a brand-new instance_id per
// §05, a different map key this latch never touches — so leaving a stale
// instance_id's entry here forever is harmless, and clearing it on a
// same-id reconnect would silently un-latch a still-wedged instance.
func (m *Manager) DetachInstance(instanceID string, conn *transport.Conn) bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.conns[instanceID] != conn {
		return false
	}
	delete(m.conns, instanceID)
	delete(m.activeByInstance, instanceID)
	return true
}

// CloseInstanceConn closes the wire connection currently attached for
// instanceID, if any. Used by the heartbeat prune sweep (PRD §05): pruning
// an instance that went silent removes only its registry entry, so a
// still-open-but-quiet socket would otherwise leave a split-brain instance
// — invisible in list_instances forever (RecordPing no-ops for
// unregistered ids, so resumed pings can't resurrect it) yet still
// executable through this manager's intact conns map. Closing the socket
// routes recovery through the one path that already works end to end: the
// serve goroutine's teardown, then the add-in's reconnect loop producing a
// fresh register.
func (m *Manager) CloseInstanceConn(instanceID string) {
	m.mu.Lock()
	conn := m.conns[instanceID]
	m.mu.Unlock()
	if conn != nil {
		conn.Close()
	}
}

// StatusForInstance derives the instance-level status list_instances (PRD
// §05) needs, from state this Manager already tracks internally:
// unrecoverable (the latch from a grace-period escalation, §06) beats
// everything else; otherwise an active execution's own last-known status
// (pending, or running — surfaced here as StatusBusy, the instance-level
// name for "actively occupying the UI thread right now") is reported;
// otherwise the instance is idle. Heartbeat-derived unresponsiveness is
// layered on top of this by the caller (registry.IsResponsive), not here —
// this method only knows about execution state, not connection liveness.
func (m *Manager) StatusForInstance(instanceID string) Status {
	m.mu.Lock()
	defer m.mu.Unlock()

	if m.unrecoverable[instanceID] {
		return StatusUnrecoverable
	}
	executionID, busy := m.activeByInstance[instanceID]
	if !busy {
		return StatusIdle
	}
	rec, ok := m.executions[executionID]
	if !ok || rec.status == StatusPending {
		return StatusPending
	}
	return StatusBusy
}

const source = "mcp-server.internal.execution"

func errInstanceUnrecoverable(instanceID string) *diag.Record {
	return diag.New(diag.SeverityError, "instance-unrecoverable", source,
		fmt.Sprintf("instance %q is unrecoverable: a prior execution didn't respond to cancellation within its grace period (PRD §06)", instanceID)).
		WithDetail(map[string]any{"instance_id": instanceID}).
		WithRemedy("restart Revit for this instance; the add-in will register a fresh instance_id on reconnect")
}

func errInstanceNotFound(instanceID string) *diag.Record {
	return diag.New(diag.SeverityError, "instance-not-found", source,
		fmt.Sprintf("instance %q is not registered with the broker (no live connection)", instanceID)).
		WithDetail(map[string]any{"instance_id": instanceID}).
		WithRemedy("confirm the instance_id from a recent register/reconnect, then retry")
}

func errInstanceDisconnected(instanceID, executionID string) *diag.Record {
	return diag.New(diag.SeverityError, "instance-disconnected", source,
		fmt.Sprintf("instance %q disconnected while execution %q was in flight", instanceID, executionID)).
		WithDetail(map[string]any{"instance_id": instanceID, "execution_id": executionID}).
		WithRemedy("wait for the add-in's reconnect loop to re-establish the connection, then retry poll_execution")
}

func errUnknownExecution(executionID string) *diag.Record {
	return diag.New(diag.SeverityError, "unknown-execution-id", source,
		fmt.Sprintf("execution_id %q is not known to this broker (never started, or the broker/add-in restarted since)", executionID)).
		WithDetail(map[string]any{"execution_id": executionID}).
		WithRemedy("start a new execution with execute_script")
}

func errWireCallFailed(executionID, method string, err error) *diag.Record {
	return diag.New(diag.SeverityError, "wire-call-failed", source,
		fmt.Sprintf("%s for execution_id %q did not complete: %s", method, executionID, err.Error())).
		WithDetail(map[string]any{"execution_id": executionID, "method": method}).
		WithRemedy("retry poll_execution; if this persists the instance may need a Revit restart")
}

func errWireDecodeFailed(executionID, method string, err error) *diag.Record {
	return diag.New(diag.SeverityError, "wire-response-malformed", source,
		fmt.Sprintf("%s response for execution_id %q could not be decoded: %s", method, executionID, err.Error())).
		WithDetail(map[string]any{"execution_id": executionID, "method": method})
}

func fromRPCError(executionID string, rpcErr *transport.RPCError) *diag.Record {
	if rpcErr.Data != nil {
		return rpcErr.Data
	}
	return diag.New(diag.SeverityError, "add-in-error", source,
		fmt.Sprintf("execution_id %q failed: %s", executionID, rpcErr.Message)).
		WithDetail(map[string]any{"execution_id": executionID})
}

// ScriptOptions carries the per-request policy flags execute_script accepts
// beyond the script itself. A struct rather than more bool parameters
// deliberately: both members are booleans with unrelated meanings, and
// positional bools next to each other in a Go call are exactly the shape
// that silently transposes.
type ScriptOptions struct {
	// OverwriteOutputFiles applies uniformly to every file Publish() touches
	// during the run (PRD §09).
	OverwriteOutputFiles bool

	// ConfirmLifecycleActions permits the Revit API members that act outside
	// the ambient transaction's rollback boundary (PRD §14). Without it the
	// add-in refuses such a script before running it.
	ConfirmLifecycleActions bool
}

// ExecuteScript forwards a script to instanceID's add-in connection. See
// PRD §06: a script finishing within timeoutMs returns the completed
// Result inline; otherwise the add-in's own {status:pending|running}
// response is returned so the caller can poll_execution. If instanceID
// already has a non-terminal execution, ExecuteScript returns
// {status:"busy", execution_id: <existing>} without forwarding anything —
// PRD §06's "Instance busy state". If instanceID previously latched
// unrecoverable (a prior execution didn't respond to cancellation), further
// calls return an explicit error rather than queuing or reporting busy,
// per §06 — the instance stays unrecoverable until a Revit restart mints a
// fresh instance_id (§05).
func (m *Manager) ExecuteScript(ctx context.Context, instanceID, documentID, script string, timeoutMs, maxDurationMs int, opts ScriptOptions) (*Result, *diag.Record) {
	m.mu.Lock()
	if m.unrecoverable[instanceID] {
		m.mu.Unlock()
		return nil, errInstanceUnrecoverable(instanceID)
	}
	if existingID, busy := m.activeByInstance[instanceID]; busy {
		m.mu.Unlock()
		return &Result{Status: StatusBusy, ExecutionID: existingID}, nil
	}
	conn, ok := m.conns[instanceID]
	if !ok {
		m.mu.Unlock()
		return nil, errInstanceNotFound(instanceID)
	}
	executionID := m.newID()
	m.executions[executionID] = &record{instanceID: instanceID, status: StatusPending}
	m.activeByInstance[instanceID] = executionID
	m.mu.Unlock()

	if maxDurationMs > 0 {
		m.scheduleAutoCancel(executionID, maxDurationMs)
	}

	params := map[string]any{
		"execution_id":           executionID,
		"document_id":            documentID,
		"script":                 script,
		"timeout_ms":             timeoutMs,
		"max_duration_ms":        maxDurationMs,
		"overwrite_output_files": opts.OverwriteOutputFiles,

		// PRD §14: always sent, even when false. The add-in defaults it to
		// false itself, but sending it explicitly keeps the wire params a
		// complete statement of what this request asked for rather than
		// something a reader has to reconstruct from absence.
		"confirm_lifecycle_actions": opts.ConfirmLifecycleActions,
	}
	res, drec := m.callWire(ctx, conn, "execute_script", executionID, timeoutMs, params)
	if drec != nil {
		// A wire-level failure (network hiccup, context timeout) doesn't
		// tell us whether the add-in actually received or ran the script —
		// it may genuinely still be in flight. Leave the execution record
		// and busy state exactly as they are rather than guessing: a still-
		// live connection lets a later poll_execution retry and get the
		// real answer, and a truly dead connection gets cleaned up by
		// DetachInstance (which frees the instance) once the broker
		// actually notices — neither path needs this call to assert an
		// outcome it doesn't know. Asserting one anyway would be worse: it
		// would rule out the one recovery path that actually works today —
		// a retry against a still-live connection getting the real answer.
		// (PRD §05 also describes the add-in replaying its own ring buffer
		// on reconnect as a further safety net, but that's add-in-side
		// design intent only: the broker has no wire-protocol counterpart
		// that consumes or applies such a replay today, so it isn't what's
		// actually closing this gap yet.)
		return nil, drec
	}
	m.settle(instanceID, executionID, res)
	return res, nil
}

// scheduleAutoCancel arranges for executionID to be cancelled on the
// agent's behalf once maxDurationMs elapses, per PRD §06: "a hard ceiling
// on total runtime ... the broker auto-issues the same cancellation signal
// on the agent's behalf, so a script nobody's actively polling doesn't sit
// forever silently occupying the instance." A no-op if the execution has
// already reached a terminal state by then.
func (m *Manager) scheduleAutoCancel(executionID string, maxDurationMs int) {
	m.afterFunc(time.Duration(maxDurationMs)*time.Millisecond, func() {
		m.mu.Lock()
		rec, ok := m.executions[executionID]
		alreadyDone := !ok || IsTerminal(rec.status)
		m.mu.Unlock()
		if alreadyDone {
			return
		}
		// Best-effort: the agent observes the eventual outcome via its own
		// poll_execution, so nothing here needs the result/error.
		_, _ = m.CancelExecution(context.Background(), executionID)
	})
}

// forwardExisting looks up executionID, returns its cached terminal result
// if it's already settled, otherwise forwards wireMethod to the owning
// instance's connection with the given timeout/params and settles the
// result. Shared by PollExecution and CancelExecution, which differ only in
// wire method, params, and timeout.
func (m *Manager) forwardExisting(ctx context.Context, executionID, wireMethod string, timeoutMs int, params map[string]any) (*Result, *diag.Record) {
	m.mu.Lock()
	rec, ok := m.executions[executionID]
	if !ok {
		m.mu.Unlock()
		return nil, errUnknownExecution(executionID)
	}
	if IsTerminal(rec.status) {
		result := rec.result
		m.mu.Unlock()
		return result, nil
	}
	conn, connOK := m.conns[rec.instanceID]
	instanceID := rec.instanceID
	m.mu.Unlock()
	if !connOK {
		return nil, errInstanceDisconnected(instanceID, executionID)
	}

	res, drec := m.callWire(ctx, conn, wireMethod, executionID, timeoutMs, params)
	if drec != nil {
		// One diagnostic IS a terminal answer, not a wire failure: the
		// add-in itself reporting unknown-execution-id (issue #42, from PR
		// #41's review). That happens when the execute_script wire call was
		// lost on a half-open connection — the add-in never received the
		// script — and the add-in that answered is authoritative that it is
		// not running this execution. Without settling here, the record
		// stayed non-terminal with the busy latch held, and the only exit
		// was auto-cancel → grace escalation → a HEALTHY instance falsely
		// latched unrecoverable until a Revit restart. Guarded on the
		// answering connection still being the instance's current one
		// (CONVENTIONS.md: the acting connection's identity travels with
		// the action) — a displaced connection's late answer must not
		// settle a run the live connection may genuinely be executing.
		if drec.Code == "unknown-execution-id" {
			m.settleLostExecution(instanceID, executionID, conn)
		}

		// Anything else: same reasoning as ExecuteScript's own wire-failure
		// path — don't assert a terminal outcome the broker doesn't
		// actually know. Leaving the record non-terminal lets a retry
		// against a still-live connection recover the real answer, and
		// DetachInstance — not this call — is what frees the instance once
		// the connection is genuinely gone.
		return nil, drec
	}
	m.settle(instanceID, executionID, res)
	return res, nil
}

// settleLostExecution marks executionID terminally failed after its OWNING
// instance's CURRENT connection answered unknown-execution-id — see the call
// site in forwardExisting for the phantom-execution scenario (issue #42).
// The conn-identity re-check happens here, under the lock, at the moment of
// mutation: the connection that was current when the poll was forwarded may
// have been displaced by a re-register while the answer was in flight. The
// stored result says plainly what happened and that the instance is free —
// deliberately NOT the unrecoverable latch, which is for a wedged Revit,
// the opposite of what this evidence shows.
func (m *Manager) settleLostExecution(instanceID, executionID string, conn *transport.Conn) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.conns[instanceID] != conn {
		return
	}
	rec, ok := m.executions[executionID]
	if !ok || IsTerminal(rec.status) {
		return
	}
	result := &Result{
		Status:      StatusError,
		ExecutionID: executionID,
		ErrorDetail: errExecutionLost(instanceID, executionID),
	}
	rec.status = StatusError
	rec.result = result
	rec.settledAt = m.now()
	if m.activeByInstance[instanceID] == executionID {
		delete(m.activeByInstance, instanceID)
	}
	m.pruneSettledLocked(rec.settledAt)
}

func errExecutionLost(instanceID, executionID string) *diag.Record {
	return diag.New(diag.SeverityError, "execution-lost", source,
		fmt.Sprintf("execution %q was reported unknown by instance %q's own live connection — either the script never reached the add-in (a connection drop raced the request; the common case) or its result aged out of the add-in's replay buffer before any poll retrieved it. The broker has settled it as failed and freed the instance", executionID, instanceID)).
		WithDetail(map[string]any{"instance_id": instanceID, "execution_id": executionID}).
		WithRemedy("re-issue execute_script (verify current document state first if the original script may have run to completion unobserved)")
}

// PollExecution forwards a poll to the owning instance, per PRD §06. An
// unknown execution_id is an explicit error, never a hang, per §05's
// "Recovering state, not just the socket."
func (m *Manager) PollExecution(ctx context.Context, executionID string, timeoutMs int) (*Result, *diag.Record) {
	return m.forwardExisting(ctx, executionID, "poll_execution", timeoutMs, map[string]any{
		"execution_id": executionID,
		"timeout_ms":   timeoutMs,
	})
}

// CancelExecution forwards a cancellation signal to the owning add-in
// connection, per PRD §06's cooperative cancellation model. The result
// reflects whatever the add-in reports back — typically "cancelled"
// immediately, or a still-non-terminal status if the add-in's own grace
// period hasn't lapsed yet.
//
// Per PRD §06 ("The fallback, for scripts that don't cooperate"):
// cancellation starts a grace timer, and if the execution still hasn't
// reached a terminal state by the time it lapses — the script ignored the
// cancellation token, or the add-in never even answered the cancel_execution
// wire call at all (a live-but-unresponsive connection, no heartbeat yet to
// catch it any other way) — the broker itself flips the owning instance to
// unrecoverable, exactly like the add-in-reported StatusUnrecoverable path,
// PROVIDED the connection that was asked to cancel is still the instance's
// current one when the grace period lapses (issue #47: a dropped or
// replaced connection isn't evidence of a wedged Revit — see
// escalateUnrecoverable's decline).
// That escalation is scheduled here unconditionally, independent of whether
// the wire call below succeeds, fails, or hangs: a wire-level failure on
// *this* call must not itself assert a terminal outcome (see
// forwardExisting's own reasoning), but it also must not silently leave the
// instance wedged busy forever with no operator-visible recovery — the
// grace-period escalation is what closes that gap.
//
// (An earlier version of this comment claimed the add-in had no
// cancel_execution handler wired up at all. That has been false since the
// live-wiring work landed: RequestDispatcher dispatches cancel_execution
// and ExecutionManager signals the script's CancellationToken, so a
// cooperative script genuinely resolves to "cancelled" over the wire. The
// grace-period escalation above is the fallback for a script that ignores
// the token or an add-in that never answers — not, as the old text said,
// the path every cancel takes.)
func (m *Manager) CancelExecution(ctx context.Context, executionID string) (*Result, *diag.Record) {
	const cancelTimeoutMs = 10_000 // grace-period ceiling per PRD §06; not caller-configurable.

	m.mu.Lock()
	rec, ok := m.executions[executionID]
	var instanceID string
	var alreadyTerminal bool
	if ok {
		instanceID = rec.instanceID
		alreadyTerminal = IsTerminal(rec.status)
	}
	m.mu.Unlock()
	if ok && !alreadyTerminal {
		m.scheduleGraceEscalation(instanceID, executionID)
	}

	return m.forwardExisting(ctx, executionID, "cancel_execution", cancelTimeoutMs, map[string]any{
		"execution_id": executionID,
	})
}

// scheduleGraceEscalation arranges for instanceID to be flipped
// unrecoverable if executionID still hasn't reached a terminal state once
// the PRD §06 cancellation grace period lapses — PROVIDED the connection
// that was asked to cancel is still the instance's current one at fire
// time. The instance's current connection (possibly nil) is captured HERE,
// at schedule time, and travels to the mutation (CONVENTIONS.md's
// connection-identity invariant, applied in full): "unrecoverable" is the
// verdict on a connection that was told to cancel and didn't comply, so
// the evidence is only valid while that same connection is still the one
// attached. A no-op at fire time if the execution already settled
// (cooperatively cancelled, completed, or already escalated) by then.
func (m *Manager) scheduleGraceEscalation(instanceID, executionID string) {
	m.mu.Lock()
	expectedConn := m.conns[instanceID]
	m.mu.Unlock()
	m.afterFunc(time.Duration(m.graceMs)*time.Millisecond, func() {
		m.escalateUnrecoverable(instanceID, executionID, expectedConn)
	})
}

// escalateUnrecoverable is the broker-side counterpart to an add-in-reported
// StatusUnrecoverable: it declares the INSTANCE unusable after the
// cancellation grace period is exhausted without the execution reaching a
// terminal state — it does not assert anything about what the script itself
// did (unlike the old, removed settleError, which wrongly asserted the
// script had failed on a bare wire hiccup). Uses the same settle-style
// terminal/latch bookkeeping as a normal StatusUnrecoverable result so
// list_instances/poll_execution/execute_script all see the instance the same
// way regardless of which path produced it.
func (m *Manager) escalateUnrecoverable(instanceID, executionID string, expectedConn *transport.Conn) {
	m.mu.Lock()
	defer m.mu.Unlock()
	rec, ok := m.executions[executionID]
	if !ok || IsTerminal(rec.status) {
		return
	}

	// ESCALATE ONLY IF the connection captured at schedule time is non-nil
	// AND still the instance's current one (issue #47, the residual sliver
	// of #42's phantom-execution latch; CONVENTIONS.md's connection-identity
	// invariant applied in full — the first version of this fix checked only
	// "is ANY connection attached at fire time", and the independent review
	// proved that latches a HEALTHY instance in the dominant timing: the
	// add-in's reconnect backoff starts at 1s while grace is 10s, so a
	// detach → cancel-in-the-gap → redial sequence has a fresh, innocent
	// connection attached by the time the timer fires). Unrecoverable is a
	// verdict about one specific connection — "the one that was told to
	// cancel is still here and didn't comply", PRD §06's wedged-UI-thread
	// evidence. A nil capture (nothing attached when the cancel was issued)
	// or a different connection at fire time (dropped and redialed, or
	// displaced by a re-register) is different evidence entirely: the
	// original connection's teardown/displacement already has its own
	// recovery paths — the #46 execution-lost settle on the first
	// post-redial poll, or a fresh cancel against the NEW connection, which
	// captures THAT connection and escalates it if it too doesn't comply.
	// The record stays non-terminal on decline, deliberately: asserting an
	// outcome the broker doesn't know is what the wire-failure paths
	// already refuse to do, and the recovery paths above need it alive.
	//
	// The boundary this check must never cross: the SAME connection
	// attached from schedule through fire, not answering, is exactly what
	// escalation exists for — identity, not any responsiveness heuristic,
	// is the whole test.
	if expectedConn == nil || m.conns[instanceID] != expectedConn {
		// Free the busy latch if this execution still holds it. Live, not
		// defensive, in the DISPLACED case: an identity-guarded
		// DetachInstance for the old connection declines once a new one has
		// attached, so nothing else clears the old execution's latch until
		// a settle — and a declined escalation was one of the two exits.
		// Guarded on the execution id so a newer execution's latch is
		// never touched.
		if m.activeByInstance[instanceID] == executionID {
			delete(m.activeByInstance, instanceID)
		}

		return
	}

	result := &Result{
		Status:      StatusUnrecoverable,
		ExecutionID: executionID,
		ErrorDetail: errInstanceUnrecoverable(instanceID),
	}
	rec.status = StatusUnrecoverable
	rec.result = result
	rec.settledAt = m.now()
	m.unrecoverable[instanceID] = true
	if m.activeByInstance[instanceID] == executionID {
		delete(m.activeByInstance, instanceID)
	}
	m.pruneSettledLocked(rec.settledAt)
}

// callWire performs one JSON-RPC round trip for method against conn,
// bounding the wait to timeoutMs plus a small network buffer so a
// non-responsive add-in doesn't hang the MCP call indefinitely.
func (m *Manager) callWire(ctx context.Context, conn *transport.Conn, method, executionID string, timeoutMs int, params any) (*Result, *diag.Record) {
	budget := time.Duration(timeoutMs)*time.Millisecond + 5*time.Second
	callCtx, cancel := context.WithTimeout(ctx, budget)
	defer cancel()

	raw, rpcErr, err := conn.Call(callCtx, method, params)
	if err != nil {
		return nil, errWireCallFailed(executionID, method, err)
	}
	if rpcErr != nil {
		return nil, fromRPCError(executionID, rpcErr)
	}

	var res Result
	if err := json.Unmarshal(raw, &res); err != nil {
		return nil, errWireDecodeFailed(executionID, method, err)
	}
	res.ExecutionID = executionID
	return &res, nil
}

// settle updates the manager's bookkeeping for executionID based on the
// wire result: non-terminal statuses stay tracked (and keep the instance
// marked busy); terminal statuses free the instance for a new
// execute_script call while the record itself is retained so a later
// poll_execution can still retrieve the final result. StatusUnrecoverable
// is the one terminal status that does *not* simply free the instance — it
// latches (PRD §06: "further calls against that instance return an
// explicit error ... rather than queuing or reporting busy"), so a
// subsequent execute_script is rejected outright instead of being forwarded
// to a Revit instance that's known to be wedged.
//
// If rec.status is already terminal, this is a no-op: concurrent
// poll_execution/cancel_execution calls can both be in flight against the
// same execution, and a later (possibly stale) response must never regress
// an already-settled terminal result.
func (m *Manager) settle(instanceID, executionID string, res *Result) {
	m.mu.Lock()
	defer m.mu.Unlock()
	rec, ok := m.executions[executionID]
	if !ok || IsTerminal(rec.status) {
		return
	}
	rec.status = res.Status
	if IsTerminal(res.Status) {
		rec.result = res
		rec.settledAt = m.now()
		if res.Status == StatusUnrecoverable {
			m.unrecoverable[instanceID] = true
		}
		if m.activeByInstance[instanceID] == executionID {
			delete(m.activeByInstance, instanceID)
		}
		m.pruneSettledLocked(rec.settledAt)
	}
}

// pruneSettledLocked evicts settled (terminal) execution records that have
// aged past settledRetention, then the oldest settled records beyond
// maxSettledExecutions. Runs opportunistically at settle time — no
// background sweep goroutine to own or shut down — which is enough, since
// the map only ever grows at settle time too. Caller must hold m.mu.
func (m *Manager) pruneSettledLocked(now time.Time) {
	type settled struct {
		id string
		at time.Time
	}
	var kept []settled
	for id, rec := range m.executions {
		if !IsTerminal(rec.status) {
			continue
		}
		if now.Sub(rec.settledAt) > settledRetention {
			delete(m.executions, id)
			continue
		}
		kept = append(kept, settled{id: id, at: rec.settledAt})
	}
	if len(kept) <= maxSettledExecutions {
		return
	}
	sort.Slice(kept, func(i, j int) bool { return kept[i].at.Before(kept[j].at) })
	for _, s := range kept[:len(kept)-maxSettledExecutions] {
		delete(m.executions, s.id)
	}
}
