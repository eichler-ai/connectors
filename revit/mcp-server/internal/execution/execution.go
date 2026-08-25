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
	ErrorDetail *diag.Record  `json:"error,omitempty"`
}

// wireResult is the shape the add-in is expected to respond with over the
// TCP connection for execute_script/poll_execution/cancel_execution — the
// same fields as Result, decoded independently so a malformed wire payload
// doesn't corrupt Result's own JSON tags.
type wireResult struct {
	Status      Status        `json:"status"`
	ExecutionID string        `json:"execution_id"`
	Output      string        `json:"output,omitempty"`
	Notices     []diag.Record `json:"notices,omitempty"`
	ErrorDetail *diag.Record  `json:"error,omitempty"`
}

type record struct {
	instanceID string
	status     Status
	result     *Result
}

// Manager owns the live set of instance wire connections and in-flight
// executions.
type Manager struct {
	mu               sync.Mutex
	conns            map[string]*transport.Conn
	executions       map[string]*record
	activeByInstance map[string]string

	newID func() string
	now   func() time.Time
}

// NewManager builds an empty Manager.
func NewManager() *Manager {
	return &Manager{
		conns:            make(map[string]*transport.Conn),
		executions:       make(map[string]*record),
		activeByInstance: make(map[string]string),
		newID:            func() string { return "exec-" + uuid.NewString() },
		now:              time.Now,
	}
}

// AttachInstance registers the wire connection to use for instanceID. A
// second call for the same instanceID (e.g. after a reconnect) replaces the
// prior connection.
func (m *Manager) AttachInstance(instanceID string, conn *transport.Conn) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.conns[instanceID] = conn
}

// DetachInstance drops the wire connection for instanceID. In-flight
// execution records are left as-is (a subsequent poll/cancel against them
// will surface an "instance disconnected" diagnostic rather than silently
// vanishing).
func (m *Manager) DetachInstance(instanceID string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.conns, instanceID)
}

const source = "mcp-server.internal.execution"

func errInstanceNotFound(instanceID string) *diag.Record {
	return diag.New(diag.SeverityError, "instance_not_found", source,
		fmt.Sprintf("instance %q is not registered with the broker (no live connection)", instanceID)).
		WithDetail(map[string]any{"instance_id": instanceID}).
		WithRemedy("confirm the instance_id from a recent register/reconnect, then retry")
}

func errInstanceDisconnected(instanceID, executionID string) *diag.Record {
	return diag.New(diag.SeverityError, "instance_disconnected", source,
		fmt.Sprintf("instance %q disconnected while execution %q was in flight", instanceID, executionID)).
		WithDetail(map[string]any{"instance_id": instanceID, "execution_id": executionID}).
		WithRemedy("wait for the add-in's reconnect loop to re-establish the connection, then retry poll_execution")
}

func errUnknownExecution(executionID string) *diag.Record {
	return diag.New(diag.SeverityError, "unknown_execution_id", source,
		fmt.Sprintf("execution_id %q is not known to this broker (never started, or the broker/add-in restarted since)", executionID)).
		WithDetail(map[string]any{"execution_id": executionID}).
		WithRemedy("start a new execution with execute_script")
}

func errWireCallFailed(executionID, method string, err error) *diag.Record {
	return diag.New(diag.SeverityError, "wire_call_failed", source,
		fmt.Sprintf("%s for execution_id %q did not complete: %s", method, executionID, err.Error())).
		WithDetail(map[string]any{"execution_id": executionID, "method": method}).
		WithRemedy("retry poll_execution; if this persists the instance may need a Revit restart")
}

func errWireDecodeFailed(executionID, method string, err error) *diag.Record {
	return diag.New(diag.SeverityError, "wire_response_malformed", source,
		fmt.Sprintf("%s response for execution_id %q could not be decoded: %s", method, executionID, err.Error())).
		WithDetail(map[string]any{"execution_id": executionID, "method": method})
}

func fromRPCError(executionID string, rpcErr *transport.RPCError) *diag.Record {
	if rpcErr.Data != nil {
		return rpcErr.Data
	}
	return diag.New(diag.SeverityError, "add_in_error", source,
		fmt.Sprintf("execution_id %q failed: %s", executionID, rpcErr.Message)).
		WithDetail(map[string]any{"execution_id": executionID})
}

// ExecuteScript forwards a script to instanceID's add-in connection. See
// PRD §06: a script finishing within timeoutMs returns the completed
// Result inline; otherwise the add-in's own {status:pending|running}
// response is returned so the caller can poll_execution. If instanceID
// already has a non-terminal execution, ExecuteScript returns
// {status:"busy", execution_id: <existing>} without forwarding anything —
// PRD §06's "Instance busy state".
func (m *Manager) ExecuteScript(ctx context.Context, instanceID, documentID, script string, timeoutMs, maxDurationMs int) (*Result, *diag.Record) {
	m.mu.Lock()
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

	params := map[string]any{
		"execution_id":    executionID,
		"document_id":     documentID,
		"script":          script,
		"timeout_ms":      timeoutMs,
		"max_duration_ms": maxDurationMs,
	}
	res, drec := m.callWire(ctx, conn, "execute_script", executionID, timeoutMs, params)
	if drec != nil {
		m.mu.Lock()
		delete(m.executions, executionID)
		if m.activeByInstance[instanceID] == executionID {
			delete(m.activeByInstance, instanceID)
		}
		m.mu.Unlock()
		return nil, drec
	}
	m.settle(instanceID, executionID, res)
	return res, nil
}

// PollExecution forwards a poll to the owning instance, per PRD §06. An
// unknown execution_id is an explicit error, never a hang, per §05's
// "Recovering state, not just the socket."
func (m *Manager) PollExecution(ctx context.Context, executionID string, timeoutMs int) (*Result, *diag.Record) {
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

	res, drec := m.callWire(ctx, conn, "poll_execution", executionID, timeoutMs, map[string]any{
		"execution_id": executionID,
		"timeout_ms":   timeoutMs,
	})
	if drec != nil {
		return nil, drec
	}
	m.settle(instanceID, executionID, res)
	return res, nil
}

// CancelExecution forwards a cancellation signal to the owning add-in
// connection, per PRD §06's cooperative cancellation model. The result
// reflects whatever the add-in reports back — typically "cancelled"
// immediately, or a still-non-terminal status if the add-in's own grace
// period hasn't lapsed yet.
func (m *Manager) CancelExecution(ctx context.Context, executionID string) (*Result, *diag.Record) {
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

	const cancelTimeoutMs = 10_000 // grace-period ceiling per PRD §06; not caller-configurable.
	res, drec := m.callWire(ctx, conn, "cancel_execution", executionID, cancelTimeoutMs, map[string]any{
		"execution_id": executionID,
	})
	if drec != nil {
		return nil, drec
	}
	m.settle(instanceID, executionID, res)
	return res, nil
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

	var wr wireResult
	if err := json.Unmarshal(raw, &wr); err != nil {
		return nil, errWireDecodeFailed(executionID, method, err)
	}
	return &Result{
		Status:      wr.Status,
		ExecutionID: executionID,
		Output:      wr.Output,
		Notices:     wr.Notices,
		ErrorDetail: wr.ErrorDetail,
	}, nil
}

// settle updates the manager's bookkeeping for executionID based on the
// wire result: non-terminal statuses stay tracked (and keep the instance
// marked busy); terminal statuses free the instance for a new
// execute_script call while the record itself is retained so a later
// poll_execution can still retrieve the final result.
func (m *Manager) settle(instanceID, executionID string, res *Result) {
	m.mu.Lock()
	defer m.mu.Unlock()
	rec, ok := m.executions[executionID]
	if !ok {
		return
	}
	rec.status = res.Status
	if IsTerminal(res.Status) {
		rec.result = res
		if m.activeByInstance[instanceID] == executionID {
			delete(m.activeByInstance, instanceID)
		}
	}
}
