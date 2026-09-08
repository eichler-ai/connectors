package fetch_test

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/eichler-ai/connectors/hub/internal/fetch"
)

// TestRefusesLoopback is the SSRF guard the import path relies on
// (source_url, hub PRD §10/§11 reversed): a URL whose address resolves to
// loopback is refused at dial time, before a single byte of the handler's
// response is read.
func TestRefusesLoopback(t *testing.T) {
	srv := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Error("fetched a loopback address")
	}))
	defer srv.Close()
	if _, err := fetch.Bytes(context.Background(), srv.URL+"/x.xlsx", 1<<20); err == nil {
		t.Fatal("loopback URL was fetched")
	}
}

func TestRefusesNonHTTPS(t *testing.T) {
	if _, err := fetch.Bytes(context.Background(), "http://example.com/x.xlsx", 1<<20); err == nil {
		t.Fatal("http URL was accepted")
	}
	if _, err := fetch.Bytes(context.Background(), "not a url", 1<<20); err == nil {
		t.Fatal("garbage URL was accepted")
	}
}
