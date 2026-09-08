package files_test

import (
	"bytes"
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/hub/internal/files"
)

// Both Store implementations satisfy the same interface hub.Host holds;
// this is a compile-time check that neither drifted from it.
var (
	_ files.Store = (*files.Temp)(nil)
	_ files.Store = (*files.GCS)(nil)
)

// TestTempStoreContract exercises the Store contract the brief asks for
// against the -dev/tests implementation: Put streams bytes to
// files/{user}/{connector}/{id}.{ext}, SignedURL mints a URL a plain HTTP
// client can fetch with no further auth, and an unknown or expired token is
// refused.
func TestTempStoreContract(t *testing.T) {
	st, err := files.NewTemp("https://hub.example")
	if err != nil {
		t.Fatal(err)
	}
	body := []byte("a,b\r\n1,2\r\n")
	ref, err := st.Put(context.Background(), "u1", "excel", "x1", "csv", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	if ref.Key != "u1/excel/x1.csv" || ref.Bytes != int64(len(body)) {
		t.Fatalf("ref: %+v", ref)
	}

	ts := httptest.NewServer(st.Handler())
	defer ts.Close()

	signed, expires, err := st.SignedURL(context.Background(), ref, time.Minute)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(signed, "https://hub.example/files/local/") {
		t.Fatalf("signed URL: %s", signed)
	}
	if expires.Before(time.Now()) || expires.After(time.Now().Add(2*time.Minute)) {
		t.Fatalf("expires: %s", expires)
	}
	pu, err := url.Parse(signed)
	if err != nil {
		t.Fatal(err)
	}
	resp, err := http.Get(ts.URL + pu.Path)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("GET signed URL: %d", resp.StatusCode)
	}
	got, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatal(err)
	}
	if string(got) != string(body) {
		t.Fatalf("downloaded %q, want %q", got, body)
	}

	// An unknown token is refused.
	resp, err = http.Get(ts.URL + "/files/local/never-issued")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusGone {
		t.Fatalf("unknown token: %d, want %d", resp.StatusCode, http.StatusGone)
	}

	// An expired token is refused, even though it was genuinely issued.
	expiredSigned, _, err := st.SignedURL(context.Background(), ref, -time.Second)
	if err != nil {
		t.Fatal(err)
	}
	pu, _ = url.Parse(expiredSigned)
	resp, err = http.Get(ts.URL + pu.Path)
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusGone {
		t.Fatalf("expired token: %d, want %d", resp.StatusCode, http.StatusGone)
	}
}
