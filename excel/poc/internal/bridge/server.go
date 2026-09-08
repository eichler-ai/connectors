package bridge

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"io/fs"
	"log"
	"net/http"
	"slices"
	"time"

	"golang.org/x/net/websocket"
)

// ExecRequest is the CLI → bridge body for POST /exec.
type ExecRequest struct {
	Script    string `json:"script"`
	TimeoutMs int64  `json:"timeoutMs,omitempty"`
}

// ExecResult is the bridge → CLI body. Either the add-in replied (Response populated) or the bridge
// itself failed (BridgeError set), e.g. no add-in connected or the deadline passed.
type ExecResult struct {
	Response
	BridgeError string `json:"bridgeError,omitempty"`
}

const (
	defaultTimeout = 30 * time.Second
	maxScriptBytes = 1 << 20
)

// Options configures Handler beyond the hub and the static files.
type Options struct {
	// ExtraOrigins are additional browser origins allowed to open /ws besides the bridge's own
	// (e.g. Script Lab's runner when the client runs as a snippet there).
	ExtraOrigins []string
	// Token, when set, is required as "Authorization: Bearer <token>" on /exec and /status. Mandatory
	// when the bridge is reachable from beyond localhost.
	Token string
	// PublicURL is the externally visible base URL (scheme://host[/prefix]) substituted into the
	// manifest in place of the localhost default, so one embedded manifest serves both deployments.
	PublicURL string
}

const localURL = "https://localhost:3000"

// Handler builds the bridge mux. addin is the directory of static add-in files served at /.
func Handler(h *Hub, addin fs.FS, opt Options) http.Handler {
	extraOrigins := opt.ExtraOrigins
	mux := http.NewServeMux()
	mux.Handle("/ws", websocket.Server{
		// Office loads the task pane from this same origin, so a same-origin check is enough; a
		// nil Handshake would accept any Origin, which we do not want even for a POC.
		Handshake: func(cfg *websocket.Config, r *http.Request) error {
			origin := r.Header.Get("Origin")
			// Behind Cloud Run the request arrives as plain HTTP but the browser's origin is https.
			if origin != "https://"+r.Host && !slices.Contains(extraOrigins, origin) && !slices.Contains(extraOrigins, "*") {
				log.Printf("ws: rejected origin %q (host %s)", origin, r.Host)
				return errors.New("origin not allowed")
			}
			return nil
		},
		Handler: h.Serve,
	})
	auth := func(next http.HandlerFunc) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			if opt.Token != "" && r.Header.Get("Authorization") != "Bearer "+opt.Token {
				log.Printf("%s %s: unauthorized from %s", r.Method, r.URL.Path, r.RemoteAddr)
				http.Error(w, "unauthorized", http.StatusUnauthorized)
				return
			}
			next(w, r)
		}
	}
	mux.HandleFunc("GET /status", auth(func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, h.Status())
	}))
	mux.HandleFunc("GET /manifest.xml", func(w http.ResponseWriter, r *http.Request) {
		b, err := fs.ReadFile(addin, "manifest.xml")
		if err != nil {
			http.NotFound(w, r)
			return
		}
		if opt.PublicURL != "" {
			// A distinct Id lets the hosted add-in be sideloaded alongside the local one.
			b = bytes.ReplaceAll(b, []byte(localURL), []byte(opt.PublicURL))
			b = bytes.ReplaceAll(b, []byte("<Id>8475d0f9-b1f0-4e4b-9253-bfe1a5711e8a</Id>"), []byte("<Id>2c9e7d41-6b3a-4f0e-9d21-7a5e0c4b8f13</Id>"))
			b = bytes.ReplaceAll(b, []byte("Excel Bridge (POC)"), []byte("Excel Bridge (hosted POC)"))
		}
		w.Header().Set("Content-Type", "application/xml")
		w.Write(b)
	})
	mux.HandleFunc("POST /exec", auth(func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxScriptBytes))
		if err != nil {
			http.Error(w, err.Error(), http.StatusRequestEntityTooLarge)
			return
		}
		var req ExecRequest
		if err := json.Unmarshal(body, &req); err != nil {
			http.Error(w, "bad JSON: "+err.Error(), http.StatusBadRequest)
			return
		}
		timeout := defaultTimeout
		if req.TimeoutMs > 0 {
			timeout = time.Duration(req.TimeoutMs) * time.Millisecond
		}
		ctx, cancel := context.WithTimeout(r.Context(), timeout)
		defer cancel()
		start := time.Now()
		resp, err := h.Exec(ctx, req.Script)
		if err != nil {
			log.Printf("exec: %v (after %s)", err, time.Since(start).Round(time.Millisecond))
			status := http.StatusBadGateway
			if errors.Is(err, ErrNotConnected) {
				status = http.StatusServiceUnavailable
			} else if errors.Is(err, context.DeadlineExceeded) {
				status = http.StatusGatewayTimeout
			}
			writeJSON(w, status, ExecResult{BridgeError: err.Error()})
			return
		}
		log.Printf("exec: %s ok=%v addin=%.0fms roundtrip=%s result=%dB", resp.ID, resp.OK, resp.DurationMs,
			time.Since(start).Round(time.Millisecond), len(resp.Result))
		writeJSON(w, http.StatusOK, ExecResult{Response: resp})
	}))
	files := http.FileServerFS(addin)
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		log.Printf("static: %s %s referer=%q origin=%q", r.Method, r.URL.Path, r.Referer(), r.Header.Get("Origin"))
		files.ServeHTTP(w, r)
	})
	return mux
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	enc := json.NewEncoder(w)
	enc.SetIndent("", "  ")
	_ = enc.Encode(v)
}
