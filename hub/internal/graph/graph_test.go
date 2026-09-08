package graph

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// fakeMicrosoft stands in for the token endpoint and Graph itself: enough to
// exercise refresh + rotation + PUT .../content without a live Microsoft
// call (RFC's "no live Microsoft calls in tests").
type fakeMicrosoft struct {
	srv *httptest.Server

	wantRefreshToken string
	rotateTo         string // "" means don't rotate
	tokenErr         string // non-empty makes /token fail

	lastAuth    string
	lastPath    string
	lastBody    []byte
	putResponse DriveItem
	putStatus   int
}

func newFakeMicrosoft(t *testing.T) *fakeMicrosoft {
	t.Helper()
	f := &fakeMicrosoft{putStatus: http.StatusCreated}
	mux := http.NewServeMux()
	mux.HandleFunc("POST /token", func(w http.ResponseWriter, r *http.Request) {
		_ = r.ParseForm()
		if r.PostForm.Get("grant_type") != "refresh_token" || r.PostForm.Get("scope") != "Files.ReadWrite offline_access" {
			http.Error(w, "bad refresh request", 400)
			return
		}
		if f.wantRefreshToken != "" && r.PostForm.Get("refresh_token") != f.wantRefreshToken {
			http.Error(w, "wrong refresh token", 400)
			return
		}
		if f.tokenErr != "" {
			w.WriteHeader(400)
			_ = json.NewEncoder(w).Encode(map[string]string{"error": f.tokenErr, "error_description": "fake failure"})
			return
		}
		resp := map[string]string{"access_token": "at-123", "token_type": "Bearer"}
		if f.rotateTo != "" {
			resp["refresh_token"] = f.rotateTo
		}
		_ = json.NewEncoder(w).Encode(resp)
	})
	mux.HandleFunc("PUT /me/drive/root:/", func(w http.ResponseWriter, r *http.Request) {
		f.lastAuth = r.Header.Get("Authorization")
		f.lastPath = r.URL.Path
		f.lastBody, _ = io.ReadAll(r.Body)
		w.WriteHeader(f.putStatus)
		_ = json.NewEncoder(w).Encode(f.putResponse)
	})
	f.srv = httptest.NewServer(mux)
	t.Cleanup(f.srv.Close)
	return f
}

func (f *fakeMicrosoft) client(t *testing.T) *Client {
	return &Client{
		ClientID:      "client-1",
		ClientSecret:  "secret-1",
		HTTPClient:    f.srv.Client(),
		TokenEndpoint: f.srv.URL + "/token",
		APIBase:       f.srv.URL,
	}
}

func TestCreateFileInFolder(t *testing.T) {
	f := newFakeMicrosoft(t)
	f.wantRefreshToken = "rt-old"
	f.putResponse = DriveItem{ID: "953169F03C1B112C!123", Name: "Budget-ab12cd.xlsx", WebURL: "https://onedrive.live.com/x"}
	f.putResponse.ParentReference.Path = "/drive/root:/Eichler Connectors"
	c := f.client(t)

	item, next, err := c.CreateFileInFolder(context.Background(), "rt-old", "Eichler Connectors", "Budget-ab12cd.xlsx", []byte("xlsx-bytes"))
	if err != nil {
		t.Fatal(err)
	}
	if next != "rt-old" {
		t.Fatalf("refresh token to persist = %q, want unchanged rt-old (Microsoft didn't rotate)", next)
	}
	if item.WebURL != "https://onedrive.live.com/x" || item.DriveID() != "953169F03C1B112C" || item.Folder() != "Eichler Connectors" {
		t.Fatalf("driveItem: %+v (drive id %q, folder %q)", item, item.DriveID(), item.Folder())
	}
	if f.lastAuth != "Bearer at-123" {
		t.Fatalf("authorization header: %q", f.lastAuth)
	}
	if !strings.Contains(f.lastPath, "Eichler Connectors") || !strings.Contains(f.lastPath, "Budget-ab12cd.xlsx") {
		t.Fatalf("upload path: %q", f.lastPath)
	}
	if string(f.lastBody) != "xlsx-bytes" {
		t.Fatalf("uploaded body: %q", f.lastBody)
	}
}

func TestCreateFileInFolderRotatesRefreshToken(t *testing.T) {
	f := newFakeMicrosoft(t)
	f.rotateTo = "rt-new"
	f.putResponse = DriveItem{ID: "CID!1"}
	c := f.client(t)

	_, next, err := c.CreateFileInFolder(context.Background(), "rt-old", "Eichler Connectors", "a.xlsx", nil)
	if err != nil {
		t.Fatal(err)
	}
	if next != "rt-new" {
		t.Fatalf("refresh token to persist = %q, want the rotated rt-new", next)
	}
}

func TestCreateFileInFolderRefreshFailure(t *testing.T) {
	f := newFakeMicrosoft(t)
	f.tokenErr = "invalid_grant"
	c := f.client(t)

	_, next, err := c.CreateFileInFolder(context.Background(), "rt-old", "Eichler Connectors", "a.xlsx", nil)
	if err == nil {
		t.Fatal("want an error when the refresh fails")
	}
	if next != "rt-old" {
		t.Fatalf("refresh token to persist on refresh failure = %q, want the original rt-old", next)
	}
	if !strings.Contains(err.Error(), "invalid_grant") {
		t.Fatalf("error should carry Microsoft's error code: %v", err)
	}
}

func TestCreateFileInFolderPutFailure(t *testing.T) {
	f := newFakeMicrosoft(t)
	f.putStatus = http.StatusForbidden
	c := f.client(t)

	_, next, err := c.CreateFileInFolder(context.Background(), "rt-old", "Eichler Connectors", "a.xlsx", nil)
	if err == nil {
		t.Fatal("want an error on a non-2xx create response")
	}
	// The refresh succeeded even though the create failed: the caller must
	// still persist whatever token came back (none rotated here, but a real
	// mid-call rotation must not be dropped just because the PUT failed).
	if next != "rt-old" {
		t.Fatalf("refresh token to persist on create failure = %q", next)
	}
}

func TestCreateFileInFolderOverSizeLimit(t *testing.T) {
	c := &Client{}
	big := make([]byte, maxSimpleUploadBytes+1)
	if _, _, err := c.CreateFileInFolder(context.Background(), "rt", "f", "n.xlsx", big); err == nil {
		t.Fatal("want an error over the simple-upload limit")
	}
}

func TestDriveIDFallsBackToParentReference(t *testing.T) {
	var d DriveItem
	d.ID = "no-bang-here"
	d.ParentReference.DriveID = "PARENT-CID"
	if got := d.DriveID(); got != "PARENT-CID" {
		t.Fatalf("DriveID() = %q, want the parentReference fallback", got)
	}
}

// TestFolder covers the live finding (2026-09-08, follow-up to the RFC §3.3
// spike): a root file's parentReference.path has nothing after "root:", but
// a file in a folder carries the folder name there, and the pane's doc_key
// must include it.
func TestFolder(t *testing.T) {
	cases := []struct {
		name, path, want string
	}{
		{"root file", "/drive/root:", ""},
		{"root file trailing slash", "/drive/root:/", ""},
		{"one folder", "/drive/root:/Eichler Connectors", "Eichler Connectors"},
		{"nested folders", "/drive/root:/A/B/C", "A/B/C"},
		{"percent-encoded space decoded", "/drive/root:/Eichler%20Connectors", "Eichler Connectors"},
		{"no root marker at all", "", ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			var d DriveItem
			d.ParentReference.Path = c.path
			if got := d.Folder(); got != c.want {
				t.Errorf("Folder() for path %q = %q, want %q", c.path, got, c.want)
			}
		})
	}
}
