// verify is the operator check deploy.sh runs after every deploy: the hub
// answers /health with the deployed revision, serves the authorization
// server metadata at the issuer root, serves the connector's protected
// resource metadata, and challenges an unauthenticated MCP request with the
// WWW-Authenticate header Claude clients follow. It prints the Hub-Version
// so the script can compare it with the revision it built. A real MCP call
// needs a real sign-in and stays a live test.
//
//	go run ./hub/deploy/verify <public-url> <connector-slug>
package main

import (
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"strings"
	"time"
)

func main() {
	if len(os.Args) != 3 {
		fmt.Fprintln(os.Stderr, "usage: go run ./hub/deploy/verify <public-url> <connector-slug>")
		os.Exit(2)
	}
	base, slug := strings.TrimSuffix(os.Args[1], "/"), os.Args[2]
	client := &http.Client{Timeout: 15 * time.Second}
	fail := func(format string, args ...any) {
		fmt.Fprintf(os.Stderr, "verify: "+format+"\n", args...)
		os.Exit(1)
	}

	resp, err := client.Get(base + "/health")
	if err != nil || resp.StatusCode != 200 {
		fail("GET /health: %v (status %v)", err, resp)
	}
	resp.Body.Close()
	version := resp.Header.Get("Hub-Version")
	if version == "" {
		fail("GET /health: no Hub-Version header")
	}

	var asm struct {
		Issuer                string   `json:"issuer"`
		AuthorizationEndpoint string   `json:"authorization_endpoint"`
		TokenEndpoint         string   `json:"token_endpoint"`
		JWKSURI               string   `json:"jwks_uri"`
		CodeChallengeMethods  []string `json:"code_challenge_methods_supported"`
	}
	getJSON(client, base+"/.well-known/oauth-authorization-server", &asm, fail)
	if asm.Issuer != base || asm.AuthorizationEndpoint == "" || asm.TokenEndpoint == "" || asm.JWKSURI == "" || len(asm.CodeChallengeMethods) == 0 {
		fail("authorization server metadata is incomplete or names another issuer: %+v", asm)
	}
	var jwks struct {
		Keys []map[string]any `json:"keys"`
	}
	getJSON(client, asm.JWKSURI, &jwks, fail)
	if len(jwks.Keys) == 0 {
		fail("JWKS has no keys")
	}
	var prm struct {
		Resource             string   `json:"resource"`
		AuthorizationServers []string `json:"authorization_servers"`
	}
	getJSON(client, base+"/"+slug+"/.well-known/oauth-protected-resource", &prm, fail)
	if prm.Resource != base+"/"+slug+"/mcp" || len(prm.AuthorizationServers) != 1 || prm.AuthorizationServers[0] != base {
		fail("protected resource metadata: %+v", prm)
	}

	resp, err = client.Post(base+"/"+slug+"/mcp", "application/json", strings.NewReader("{}"))
	if err != nil {
		fail("POST /%s/mcp: %v", slug, err)
	}
	resp.Body.Close()
	challenge := resp.Header.Get("WWW-Authenticate")
	if resp.StatusCode != 401 || !strings.Contains(challenge, `resource_metadata="`+base+"/"+slug+`/.well-known/oauth-protected-resource"`) {
		fail("POST /%s/mcp without a token: %d %q, want 401 with resource_metadata", slug, resp.StatusCode, challenge)
	}
	fmt.Println(version)
}

func getJSON(client *http.Client, url string, v any, fail func(string, ...any)) {
	resp, err := client.Get(url)
	if err != nil || resp.StatusCode != 200 {
		fail("GET %s: %v (status %v)", url, err, resp)
	}
	defer resp.Body.Close()
	if err := json.NewDecoder(resp.Body).Decode(v); err != nil {
		fail("GET %s: not JSON: %v", url, err)
	}
}
