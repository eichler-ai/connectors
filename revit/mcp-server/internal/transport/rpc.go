// Package transport implements the wire protocol used between the broker
// and the add-in (and between a secondary broker's agent-client proxy and
// the primary broker): JSON-RPC 2.0 messages, newline-delimited — one JSON
// object per line, per PRD §05 ("Framing").
package transport

import (
	"encoding/json"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
)

// Version is the JSON-RPC protocol version string used on every message.
const Version = "2.0"

// Message is a single JSON-RPC 2.0 object as it appears on the wire. A
// request has a non-nil ID and a Method; a notification has a nil ID and a
// Method; a response has a non-nil ID and no Method, carrying either Result
// or Error.
type Message struct {
	JSONRPC string           `json:"jsonrpc"`
	ID      *json.RawMessage `json:"id,omitempty"`
	Method  string           `json:"method,omitempty"`
	Params  json.RawMessage  `json:"params,omitempty"`
	Result  json.RawMessage  `json:"result,omitempty"`
	Error   *RPCError        `json:"error,omitempty"`
}

// RPCError is a JSON-RPC 2.0 error object. Data carries the shared
// diagnostic-record shape from PRD §01, not a bare string.
type RPCError struct {
	Code    int          `json:"code"`
	Message string       `json:"message"`
	Data    *diag.Record `json:"data,omitempty"`
}

// Standard JSON-RPC 2.0 error codes, plus a couple of protocol-specific ones
// used across the broker.
const (
	ErrCodeParseError     = -32700
	ErrCodeInvalidRequest = -32600
	ErrCodeMethodNotFound = -32601
	ErrCodeInvalidParams  = -32602
	ErrCodeInternalError  = -32603
	// ErrCodeUnauthorized is used for the pre-auth gate described in PRD §10:
	// any message from a connection that hasn't presented a valid token yet.
	ErrCodeUnauthorized = -32000
)

// IsRequest reports whether the message is a request expecting a response.
func (m *Message) IsRequest() bool {
	return m.Method != "" && m.ID != nil
}

// IsNotification reports whether the message is a notification (no reply
// expected).
func (m *Message) IsNotification() bool {
	return m.Method != "" && m.ID == nil
}

// IsResponse reports whether the message is a response to a prior request.
func (m *Message) IsResponse() bool {
	return m.Method == "" && m.ID != nil
}

func idFromRaw(id json.RawMessage) *json.RawMessage {
	if id == nil {
		return nil
	}
	cp := make(json.RawMessage, len(id))
	copy(cp, id)
	return &cp
}

// NewRequest builds a JSON-RPC 2.0 request message.
func NewRequest(id json.RawMessage, method string, params any) (*Message, error) {
	raw, err := marshalParams(params)
	if err != nil {
		return nil, err
	}
	return &Message{JSONRPC: Version, ID: idFromRaw(id), Method: method, Params: raw}, nil
}

// NewNotification builds a JSON-RPC 2.0 notification message (no ID).
func NewNotification(method string, params any) (*Message, error) {
	raw, err := marshalParams(params)
	if err != nil {
		return nil, err
	}
	return &Message{JSONRPC: Version, Method: method, Params: raw}, nil
}

// NewResultResponse builds a JSON-RPC 2.0 success response.
func NewResultResponse(id json.RawMessage, result any) (*Message, error) {
	raw, err := json.Marshal(result)
	if err != nil {
		return nil, err
	}
	return &Message{JSONRPC: Version, ID: idFromRaw(id), Result: raw}, nil
}

// NewErrorResponse builds a JSON-RPC 2.0 error response using the shared
// diagnostic-record shape as the error's data field.
func NewErrorResponse(id json.RawMessage, code int, message string, data *diag.Record) *Message {
	return &Message{
		JSONRPC: Version,
		ID:      idFromRaw(id),
		Error:   &RPCError{Code: code, Message: message, Data: data},
	}
}

func marshalParams(params any) (json.RawMessage, error) {
	if params == nil {
		return nil, nil
	}
	return json.Marshal(params)
}
