package authserver

import (
	"net/http"
)

// Server-rendered pages: sign-in, consent, error. They present the service
// as "Eichler Connectors" (CONVENTIONS.md) and stay plain enough to load in
// the small popup Claude Desktop opens and in the browser tab Claude Code
// and claude.ai send the user to. No script, no external assets.
const pages = `
{{define "head"}}<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{.Title}} — Eichler Connectors</title>
<style>
body{margin:0;font:16px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif;background:#f4f4f2;color:#1b1b1b}
main{max-width:26rem;margin:8vh auto;padding:2rem;background:#fff;border-radius:12px;box-shadow:0 1px 3px rgba(0,0,0,.08)}
h1{font-size:1.25rem;margin:0 0 .25rem}.brand{color:#666;font-size:.85rem;margin:0 0 1.5rem}
.btn{display:inline-block;padding:.6rem 1.1rem;border-radius:8px;border:1px solid #1b1b1b;background:#1b1b1b;color:#fff;font:inherit;cursor:pointer;text-decoration:none}
.btn.secondary{background:#fff;color:#1b1b1b}.row{display:flex;gap:.75rem;margin-top:1.5rem}
ul{padding-left:1.2rem}code{background:#f0f0ee;padding:.1em .3em;border-radius:4px}
.warn{background:#fff7e0;border:1px solid #f0d58c;padding:.6rem .8rem;border-radius:8px;font-size:.9rem}
.muted{color:#666;font-size:.9rem}
</style></head><body><main><p class="brand">Eichler Connectors</p>{{end}}
{{define "foot"}}</main></body></html>{{end}}

{{define "signin"}}{{template "head" .}}
<h1>Sign in</h1>
<p><strong>{{.ClientName}}</strong> wants to use your connectors. Sign in to continue.</p>
<p class="row"><a class="btn" href="/login/microsoft?ls={{.LS}}">Sign in with Microsoft</a></p>
<p class="muted">Work, school and personal Microsoft accounts are accepted.</p>
{{template "foot" .}}{{end}}

{{define "consent"}}{{template "head" .}}
<h1>Allow {{.ClientName}}?</h1>
<p>It will be able to run scripts in these applications on your behalf, as you:</p>
<ul>{{range .Scopes}}<li><code>{{.}}</code></li>{{end}}</ul>
<p class="muted">After approval you will be sent back to <code>{{.RedirectHost}}</code>.</p>
{{if .Loopback}}<p class="warn">That address is on this computer. Only approve if you started this from an app you are running yourself.</p>{{end}}
<form method="post" action="/oauth/consent">
<input type="hidden" name="ls" value="{{.LS}}">
<div class="row"><button class="btn" name="decision" value="approve">Approve</button>
<button class="btn secondary" name="decision" value="deny">Deny</button></div>
</form>
{{template "foot" .}}{{end}}

{{define "error"}}{{template "head" .}}
<h1>{{.Title}}</h1>
<p>{{.Message}}</p>
<p class="muted">Close this window and try connecting again from your Claude client.</p>
{{template "foot" .}}{{end}}
`

type pageData struct {
	Title        string
	Message      string
	ClientName   string
	Scopes       []string
	RedirectHost string
	Loopback     bool
	LS           string
	// FormActionOrigins are added to the page's `form-action` CSP source
	// list. Browsers enforce form-action not only on the form's action URL
	// but on the redirect the submission produces: the consent form posts
	// to /oauth/consent, which 302s to the client's redirect_uri, and with
	// `form-action 'self'` alone the browser silently drops that navigation
	// and the client never receives its code (found live with Claude Code).
	// So the consent page, and only it, lists the redirect_uri's origin.
	FormActionOrigins []string
}

func (s *Server) render(w http.ResponseWriter, status int, name string, d pageData) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("X-Frame-Options", "DENY")
	formAction := "'self'"
	for _, o := range d.FormActionOrigins {
		formAction += " " + o
	}
	w.Header().Set("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; form-action "+formAction+"; frame-ancestors 'none'")
	w.Header().Set("Referrer-Policy", "no-referrer")
	w.WriteHeader(status)
	if err := s.tmpl.ExecuteTemplate(w, name, d); err != nil {
		s.log.Error("authserver: render", "page", name, "err", err)
	}
}

// errorPage is for failures that must not redirect (an unknown client or
// redirect URI would make the redirect itself the attack).
func (s *Server) errorPage(w http.ResponseWriter, status int, title, msg string) {
	s.render(w, status, "error", pageData{Title: title, Message: msg})
}
