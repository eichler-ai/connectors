package authserver

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"net/http"
	"strings"
	"time"
)

// The authorization server's own session: who is signed in to the consent
// pages. Not an OAuth artefact and never sent to a client. `__Host-` pins
// the cookie to this exact origin, Secure + HttpOnly + SameSite=Lax keep it
// off the wire and out of scripts, and a one-hour lifetime means a shared
// browser forgets the sign-in soon (§13 cookie flags). The value is signed
// with a key derived from the JWT signing key so no second secret exists.
const sessionCookie = "__Host-hub_session"

type session struct {
	UserID  string `json:"u"`
	Expires int64  `json:"e"`
	Nonce   string `json:"n"`
}

func (s *Server) sessionKey() []byte { return s.o.Keys.Secret("session-cookie") }

func (s *Server) setSession(w http.ResponseWriter, userID string) {
	body, _ := json.Marshal(session{UserID: userID, Expires: s.now().Add(sessionTTL).Unix(), Nonce: randomToken()[:16]})
	enc := base64.RawURLEncoding.EncodeToString(body)
	mac := hmac.New(sha256.New, s.sessionKey())
	mac.Write([]byte(enc))
	http.SetCookie(w, &http.Cookie{
		Name:     sessionCookie,
		Value:    enc + "." + base64.RawURLEncoding.EncodeToString(mac.Sum(nil)),
		Path:     "/",
		Secure:   true,
		HttpOnly: true,
		SameSite: http.SameSiteLaxMode,
		MaxAge:   int(sessionTTL / time.Second),
	})
}

func (s *Server) clearSession(w http.ResponseWriter) {
	http.SetCookie(w, &http.Cookie{Name: sessionCookie, Value: "", Path: "/", Secure: true, HttpOnly: true, SameSite: http.SameSiteLaxMode, MaxAge: -1})
}

// sessionUser returns the signed-in user, or "" when there is no valid
// session.
func (s *Server) sessionUser(r *http.Request) string {
	c, err := r.Cookie(sessionCookie)
	if err != nil {
		return ""
	}
	enc, sig, ok := strings.Cut(c.Value, ".")
	if !ok {
		return ""
	}
	want, err := base64.RawURLEncoding.DecodeString(sig)
	if err != nil {
		return ""
	}
	mac := hmac.New(sha256.New, s.sessionKey())
	mac.Write([]byte(enc))
	if !hmac.Equal(mac.Sum(nil), want) {
		return ""
	}
	body, err := base64.RawURLEncoding.DecodeString(enc)
	if err != nil {
		return ""
	}
	var ses session
	if json.Unmarshal(body, &ses) != nil || ses.UserID == "" || s.now().Unix() >= ses.Expires {
		return ""
	}
	return ses.UserID
}
