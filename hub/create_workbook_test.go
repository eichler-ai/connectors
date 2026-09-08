package hub_test

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/hub"
	"github.com/eichler-ai/connectors/hub/internal/graph"
	"github.com/eichler-ai/connectors/hub/internal/graphtoken"
	"github.com/eichler-ai/connectors/hub/internal/store"
	"github.com/eichler-ai/connectors/internal/auth"
)

// This file exercises hub.Host.CreateWorkbookFile directly (RFC
// excel/docs/rfc-graph-create-and-open.md §3.1, §3.2): the plumbing between
// the store's encrypted graph_tokens and hub/internal/graph's Client, which
// a connector's create_workbook tool (excel/connector/create_workbook.go)
// cannot exercise itself — it can only import the hub package, not
// hub/internal/graph or hub/internal/store (Go's internal rule), so this is
// where the full wiring gets a real test. No live Microsoft calls: a fake
// token endpoint and a fake Graph, both httptest.

// fakeGraph stands in for Microsoft's token endpoint and Graph's
// .../content PUT.
type fakeGraph struct {
	srv *httptest.Server

	rotateTo  string
	failToken bool
	failPut   int // non-zero HTTP status makes the PUT fail
	item      graph.DriveItem

	lastRefreshToken string
	lastAuth         string
}

func newFakeGraph(t *testing.T) *fakeGraph {
	t.Helper()
	f := &fakeGraph{item: graph.DriveItem{ID: "953169F03C1B112C!456", Name: "Budget-ab12cd.xlsx", WebURL: "https://onedrive.live.com/edit.aspx?x"}}
	f.item.ParentReference.Path = "/drive/root:/Eichler Connectors"
	mux := http.NewServeMux()
	mux.HandleFunc("POST /token", func(w http.ResponseWriter, r *http.Request) {
		_ = r.ParseForm()
		f.lastRefreshToken = r.PostForm.Get("refresh_token")
		if f.failToken {
			w.WriteHeader(400)
			_ = json.NewEncoder(w).Encode(map[string]string{"error": "invalid_grant", "error_description": "fake"})
			return
		}
		resp := map[string]string{"access_token": "at-1"}
		if f.rotateTo != "" {
			resp["refresh_token"] = f.rotateTo
		}
		_ = json.NewEncoder(w).Encode(resp)
	})
	mux.HandleFunc("PUT /me/drive/root:/", func(w http.ResponseWriter, r *http.Request) {
		f.lastAuth = r.Header.Get("Authorization")
		if f.failPut != 0 {
			w.WriteHeader(f.failPut)
			return
		}
		w.WriteHeader(201)
		_ = json.NewEncoder(w).Encode(f.item)
	})
	f.srv = httptest.NewServer(mux)
	t.Cleanup(f.srv.Close)
	return f
}

func (f *fakeGraph) client() *graph.Client {
	return &graph.Client{ClientID: "c1", ClientSecret: "s1", HTTPClient: f.srv.Client(), TokenEndpoint: f.srv.URL + "/token", APIBase: f.srv.URL}
}

func testKeys(t *testing.T) *auth.KeySet {
	t.Helper()
	pem, err := auth.GenerateKeyPEM()
	if err != nil {
		t.Fatal(err)
	}
	ks, err := auth.ParseKeySet(pem)
	if err != nil {
		t.Fatal(err)
	}
	return ks
}

func newGraphHost(t *testing.T, keys *auth.KeySet, gc *graph.Client, st store.Store, logBuf *bytes.Buffer) *hub.Host {
	t.Helper()
	var logger *slog.Logger
	if logBuf != nil {
		logger = slog.New(slog.NewTextHandler(logBuf, nil))
	} else {
		logger = slog.New(slog.NewTextHandler(io.Discard, nil))
	}
	srv, err := hub.NewServer(hub.Options{
		Auth:       mustDevToken(t),
		Connectors: []hub.Connector{&stub{}},
		Store:      st,
		Keys:       keys,
		Graph:      gc,
		Logger:     logger,
	})
	if err != nil {
		t.Fatal(err)
	}
	return srv.Host()
}

func mustDevToken(t *testing.T) *auth.DevToken {
	t.Helper()
	a, err := auth.NewDevToken(token)
	if err != nil {
		t.Fatal(err)
	}
	return a
}

func TestCreateWorkbookFileUnconfigured(t *testing.T) {
	h := newGraphHost(t, nil, nil, store.NewMemory(), nil)
	_, rec := h.CreateWorkbookFile(context.Background(), "u1", "a.xlsx", []byte("x"))
	if rec == nil || rec.Code != "graph-unconfigured" {
		t.Fatalf("rec = %+v", rec)
	}
}

func TestCreateWorkbookFileNotConnected(t *testing.T) {
	f := newFakeGraph(t)
	h := newGraphHost(t, testKeys(t), f.client(), store.NewMemory(), nil)
	_, rec := h.CreateWorkbookFile(context.Background(), "u1", "a.xlsx", []byte("x"))
	if rec == nil || rec.Code != "graph-not-connected" {
		t.Fatalf("rec = %+v", rec)
	}
}

func TestCreateWorkbookFileSuccess(t *testing.T) {
	f := newFakeGraph(t)
	keys := testKeys(t)
	st := store.NewMemory()
	const refreshToken = "M.C1_BL2.super-secret-refresh-token"
	if err := graphtoken.Put(context.Background(), st, keys, "u1", refreshToken, time.Now()); err != nil {
		t.Fatal(err)
	}
	var logBuf bytes.Buffer
	h := newGraphHost(t, keys, f.client(), st, &logBuf)

	item, rec := h.CreateWorkbookFile(context.Background(), "u1", "Budget-ab12cd.xlsx", []byte("xlsx-bytes"))
	if rec != nil {
		t.Fatalf("rec = %+v", rec)
	}
	if item.WebURL != f.item.WebURL || item.DriveID != "953169F03C1B112C" || item.Name != f.item.Name || item.Folder != "Eichler Connectors" {
		t.Fatalf("item: %+v", item)
	}
	if f.lastAuth != "Bearer at-1" {
		t.Fatalf("authorization: %q", f.lastAuth)
	}
	if f.lastRefreshToken != refreshToken {
		t.Fatalf("graph client used the wrong refresh token")
	}
	// Never logged: the plaintext refresh token must not appear in the log
	// stream Host.CreateWorkbookFile wrote to.
	if strings.Contains(logBuf.String(), refreshToken) {
		t.Fatalf("the refresh token leaked into the log: %s", logBuf.String())
	}
	// The correlation spike's observability (RFC §3.3, §6): the driveItem
	// id and web_url are logged at Info.
	if !strings.Contains(logBuf.String(), item.ID) || !strings.Contains(logBuf.String(), item.WebURL) {
		t.Fatalf("driveItem id/web_url not logged: %s", logBuf.String())
	}
}

func TestCreateWorkbookFilePersistsRotatedToken(t *testing.T) {
	f := newFakeGraph(t)
	f.rotateTo = "rt-rotated"
	keys := testKeys(t)
	st := store.NewMemory()
	if err := graphtoken.Put(context.Background(), st, keys, "u1", "rt-original", time.Now()); err != nil {
		t.Fatal(err)
	}
	h := newGraphHost(t, keys, f.client(), st, nil)

	if _, rec := h.CreateWorkbookFile(context.Background(), "u1", "a.xlsx", nil); rec != nil {
		t.Fatalf("rec = %+v", rec)
	}
	got, err := graphtoken.Get(context.Background(), st, keys, "u1")
	if err != nil || got != "rt-rotated" {
		t.Fatalf("stored token after rotation: %v %q", err, got)
	}
}

func TestCreateWorkbookFileGraphFailureStillPersistsRotation(t *testing.T) {
	f := newFakeGraph(t)
	f.rotateTo = "rt-rotated"
	f.failPut = 403
	keys := testKeys(t)
	st := store.NewMemory()
	if err := graphtoken.Put(context.Background(), st, keys, "u1", "rt-original", time.Now()); err != nil {
		t.Fatal(err)
	}
	h := newGraphHost(t, keys, f.client(), st, nil)

	_, rec := h.CreateWorkbookFile(context.Background(), "u1", "a.xlsx", nil)
	if rec == nil || rec.Code != "graph-create-failed" {
		t.Fatalf("rec = %+v", rec)
	}
	// The refresh succeeded and rotated even though the create failed after
	// it; that new token must not be lost.
	got, err := graphtoken.Get(context.Background(), st, keys, "u1")
	if err != nil || got != "rt-rotated" {
		t.Fatalf("stored token after a create failure: %v %q", err, got)
	}
}

func TestCreateWorkbookFileTokenRefreshFailure(t *testing.T) {
	f := newFakeGraph(t)
	f.failToken = true
	keys := testKeys(t)
	st := store.NewMemory()
	if err := graphtoken.Put(context.Background(), st, keys, "u1", "rt-original", time.Now()); err != nil {
		t.Fatal(err)
	}
	h := newGraphHost(t, keys, f.client(), st, nil)

	_, rec := h.CreateWorkbookFile(context.Background(), "u1", "a.xlsx", nil)
	if rec == nil || rec.Code != "graph-create-failed" {
		t.Fatalf("rec = %+v", rec)
	}
}
