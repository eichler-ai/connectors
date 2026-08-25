package transport

import (
	"bufio"
	"encoding/json"
	"fmt"
	"io"
	"sync"
)

// maxLineBytes bounds a single NDJSON line to guard against a malformed or
// hostile peer sending an unbounded line and exhausting memory.
const maxLineBytes = 64 * 1024 * 1024 // 64MiB — generous for a script body.

// Framer reads and writes NDJSON-framed JSON-RPC 2.0 messages, per PRD §05
// ("Framing"): one JSON object per line, no Content-Length headers. Writes
// are safe for concurrent use from multiple goroutines; reads are not
// (callers are expected to have a single reader goroutine per connection,
// consistent with how the broker dispatches inbound wire traffic).
type Framer struct {
	scanner *bufio.Scanner

	writeMu sync.Mutex
	w       io.Writer
}

// NewFramer builds a Framer reading from r and writing to w. r and w are
// typically the same net.Conn.
func NewFramer(r io.Reader, w io.Writer) *Framer {
	scanner := bufio.NewScanner(r)
	scanner.Buffer(make([]byte, 0, 64*1024), maxLineBytes)
	return &Framer{scanner: scanner, w: w}
}

// ReadMessage reads and decodes the next NDJSON line as a Message. Blank
// lines are skipped. Returns io.EOF when the underlying stream is
// exhausted.
func (f *Framer) ReadMessage() (*Message, error) {
	for {
		if !f.scanner.Scan() {
			if err := f.scanner.Err(); err != nil {
				return nil, fmt.Errorf("transport: reading NDJSON line: %w", err)
			}
			return nil, io.EOF
		}
		line := f.scanner.Bytes()
		if len(line) == 0 {
			continue
		}
		var m Message
		if err := json.Unmarshal(line, &m); err != nil {
			return nil, fmt.Errorf("transport: malformed JSON-RPC line: %w", err)
		}
		return &m, nil
	}
}

// WriteMessage encodes m as a single JSON object followed by a newline.
// Safe for concurrent callers.
func (f *Framer) WriteMessage(m *Message) error {
	b, err := json.Marshal(m)
	if err != nil {
		return fmt.Errorf("transport: marshaling message: %w", err)
	}
	b = append(b, '\n')

	f.writeMu.Lock()
	defer f.writeMu.Unlock()
	if _, err := f.w.Write(b); err != nil {
		return fmt.Errorf("transport: writing NDJSON line: %w", err)
	}
	return nil
}
