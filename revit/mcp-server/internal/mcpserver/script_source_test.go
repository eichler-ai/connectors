package mcpserver

import (
	"context"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

const sampleScript = `var walls = new FilteredElementCollector(Document); return walls;`

func TestResolveScript_Inline(t *testing.T) {
	got, rec := resolveScript(context.Background(), sampleScript, "")
	if rec != nil {
		t.Fatalf("unexpected record: %+v", rec)
	}
	if got != sampleScript {
		t.Fatalf("got %q, want %q", got, sampleScript)
	}
}

func TestResolveScript_InlineTrimmed(t *testing.T) {
	got, rec := resolveScript(context.Background(), "  \n"+sampleScript+"\n  ", "")
	if rec != nil {
		t.Fatalf("unexpected record: %+v", rec)
	}
	if got != sampleScript {
		t.Fatalf("got %q, want trimmed %q", got, sampleScript)
	}
}

func TestResolveScript_BothProvided(t *testing.T) {
	_, rec := resolveScript(context.Background(), sampleScript, "/tmp/x.cs")
	if rec == nil || rec.Code != "script-source-ambiguous" {
		t.Fatalf("want script-source-ambiguous, got %+v", rec)
	}
}

func TestResolveScript_NeitherProvided(t *testing.T) {
	_, rec := resolveScript(context.Background(), "  ", "  ")
	if rec == nil || rec.Code != "script-source-required" {
		t.Fatalf("want script-source-required, got %+v", rec)
	}
}

func TestResolveScript_LocalFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "walls.cs") // t.TempDir is absolute on every OS
	if err := os.WriteFile(path, []byte(sampleScript), 0o600); err != nil {
		t.Fatal(err)
	}
	got, rec := resolveScript(context.Background(), "", path)
	if rec != nil {
		t.Fatalf("unexpected record: %+v", rec)
	}
	if got != sampleScript {
		t.Fatalf("got %q, want %q", got, sampleScript)
	}
}

func TestResolveScript_LocalFileStripsBOM(t *testing.T) {
	path := filepath.Join(t.TempDir(), "bom.cs")
	body := append(append([]byte{}, utf8BOM...), []byte(sampleScript)...)
	if err := os.WriteFile(path, body, 0o600); err != nil {
		t.Fatal(err)
	}
	got, rec := resolveScript(context.Background(), "", path)
	if rec != nil {
		t.Fatalf("unexpected record: %+v", rec)
	}
	if got != sampleScript {
		t.Fatalf("BOM not stripped: got %q", got)
	}
}

func TestResolveScript_RelativePathRejected(t *testing.T) {
	_, rec := resolveScript(context.Background(), "", filepath.Join("relative", "walls.cs"))
	if rec == nil || rec.Code != "script-path-not-absolute" {
		t.Fatalf("want script-path-not-absolute, got %+v", rec)
	}
}

func TestResolveScript_MissingFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "nope.cs")
	_, rec := resolveScript(context.Background(), "", path)
	if rec == nil || rec.Code != "script-file-unreadable" {
		t.Fatalf("want script-file-unreadable, got %+v", rec)
	}
}

func TestResolveScript_EmptyFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "empty.cs")
	if err := os.WriteFile(path, []byte("   \n\t"), 0o600); err != nil {
		t.Fatal(err)
	}
	_, rec := resolveScript(context.Background(), "", path)
	if rec == nil || rec.Code != "script-empty" {
		t.Fatalf("want script-empty, got %+v", rec)
	}
}

func TestResolveScript_OversizeFile(t *testing.T) {
	orig := maxScriptSourceBytes
	maxScriptSourceBytes = 16
	defer func() { maxScriptSourceBytes = orig }()

	path := filepath.Join(t.TempDir(), "big.cs")
	if err := os.WriteFile(path, []byte("0123456789ABCDEFGHIJ"), 0o600); err != nil { // 20 > 16
		t.Fatal(err)
	}
	_, rec := resolveScript(context.Background(), "", path)
	if rec == nil || rec.Code != "script-source-too-large" {
		t.Fatalf("want script-source-too-large, got %+v", rec)
	}
}

func TestResolveScript_URL(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(sampleScript))
	}))
	defer srv.Close()

	got, rec := resolveScript(context.Background(), "", srv.URL) // httptest is http on loopback → allowed
	if rec != nil {
		t.Fatalf("unexpected record: %+v", rec)
	}
	if got != sampleScript {
		t.Fatalf("got %q, want %q", got, sampleScript)
	}
}

func TestResolveScript_URLStripsBOM(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write(append(append([]byte{}, utf8BOM...), []byte(sampleScript)...))
	}))
	defer srv.Close()

	got, rec := resolveScript(context.Background(), "", srv.URL)
	if rec != nil {
		t.Fatalf("unexpected record: %+v", rec)
	}
	if got != sampleScript {
		t.Fatalf("BOM not stripped from URL body: got %q", got)
	}
}

func TestResolveScript_URLNon200(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Error(w, "gone", http.StatusNotFound)
	}))
	defer srv.Close()

	_, rec := resolveScript(context.Background(), "", srv.URL)
	if rec == nil || rec.Code != "script-url-fetch-failed" {
		t.Fatalf("want script-url-fetch-failed, got %+v", rec)
	}
}

func TestResolveScript_HTTPNonLoopbackBlocked(t *testing.T) {
	_, rec := resolveScript(context.Background(), "", "http://example.com/walls.cs")
	if rec == nil || rec.Code != "script-url-scheme-blocked" {
		t.Fatalf("want script-url-scheme-blocked, got %+v", rec)
	}
}

func TestResolveScript_URLOversize(t *testing.T) {
	orig := maxScriptSourceBytes
	maxScriptSourceBytes = 8
	defer func() { maxScriptSourceBytes = orig }()

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte("this body is definitely longer than eight bytes"))
	}))
	defer srv.Close()

	_, rec := resolveScript(context.Background(), "", srv.URL)
	if rec == nil || rec.Code != "script-source-too-large" {
		t.Fatalf("want script-source-too-large, got %+v", rec)
	}
}

// A Windows drive-letter path must be treated as a file path, not a URL with
// scheme "c". We can't read C:\ on this host, but the error proves it took the
// file branch (unreadable) rather than the URL branch.
func TestResolveScript_DriveLetterIsNotURL(t *testing.T) {
	if isHTTPRef(`C:\scripts\walls.cs`) {
		t.Fatal("drive-letter path misclassified as URL")
	}
}
