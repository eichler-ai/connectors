package auth

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestDevToken(t *testing.T) {
	if _, err := NewDevToken("short"); err == nil {
		t.Fatal("a short token was accepted")
	}
	d, err := NewDevToken("correct-horse-battery-staple")
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	p, err := d.VerifyAccessToken(ctx, "correct-horse-battery-staple")
	if err != nil || p.UserID == "" {
		t.Fatalf("valid token: %v %+v", err, p)
	}
	pb, err := d.VerifyBridgeToken(ctx, "correct-horse-battery-staple")
	if err != nil || pb.UserID != p.UserID {
		t.Fatalf("bridge and access identities differ: %+v %+v", p, pb)
	}
	for _, bad := range []string{"", "correct-horse-battery-stapl", "correct-horse-battery-staple "} {
		if _, err := d.VerifyAccessToken(ctx, bad); !errors.Is(err, ErrInvalidToken) {
			t.Fatalf("token %q: %v", bad, err)
		}
	}
	// Same token, same user; a different token, a different user.
	d2, _ := NewDevToken("correct-horse-battery-staple")
	p2, _ := d2.VerifyAccessToken(ctx, "correct-horse-battery-staple")
	d3, _ := NewDevToken("another-token-entirely-1")
	p3, _ := d3.VerifyAccessToken(ctx, "another-token-entirely-1")
	if p2.UserID != p.UserID || p3.UserID == p.UserID {
		t.Fatalf("user ids: %s %s %s", p.UserID, p2.UserID, p3.UserID)
	}
}

func TestRequireBearer(t *testing.T) {
	d, _ := NewDevToken("correct-horse-battery-staple")
	var seen string
	h := RequireBearer(d)(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		seen = UserID(r.Context())
	}))
	cases := []struct {
		header string
		want   int
	}{
		{"", http.StatusUnauthorized},
		{"Bearer wrong", http.StatusUnauthorized},
		{"Basic correct-horse-battery-staple", http.StatusUnauthorized},
		{"Bearer correct-horse-battery-staple", http.StatusOK},
	}
	for _, tc := range cases {
		seen = ""
		req := httptest.NewRequest("POST", "/excel/mcp", nil)
		if tc.header != "" {
			req.Header.Set("Authorization", tc.header)
		}
		rr := httptest.NewRecorder()
		h.ServeHTTP(rr, req)
		if rr.Code != tc.want {
			t.Fatalf("%q: status %d, want %d", tc.header, rr.Code, tc.want)
		}
		if tc.want == http.StatusOK && seen == "" {
			t.Fatal("handler saw no user")
		}
	}
}
