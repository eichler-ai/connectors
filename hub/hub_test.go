package hub_test

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"io/fs"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"regexp"
	"strings"
	"testing"
	"testing/fstest"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub"
	"github.com/eichler-ai/connectors/hub/bridgetest"
	"github.com/eichler-ai/connectors/hub/internal/bridge"
	"github.com/eichler-ai/connectors/hub/protocol"
	"github.com/eichler-ai/connectors/internal/auth"
)

const token = "hub-test-token-0123456789"

// stub is the smallest connector that exercises every hub seam.
type stub struct{ validateErr error }

func (*stub) Slug() string { return "stub" }
func (*stub) Capabilities() hub.Capabilities {
	return hub.Capabilities{Bridge: true, Languages: []string{"js"}}
}
func (*stub) Tools(reg *hub.ToolRegistry)                  {}
func (*stub) Skill() []byte                                { return []byte("# stub skill\n") }
func (s *stub) Validate(context.Context, hub.Script) error { return s.validateErr }
func (*stub) Static() fs.FS {
	return fstest.MapFS{
		"manifest.xml": {Data: []byte(`<Id>d9cd8bd0-fb31-48f1-bbbd-997f15140cc4</Id><DisplayName DefaultValue="Stub Connector"/>` +
			`<bt:String id="Bridge.Group.Label" DefaultValue="MCP Bridge"/><bt:String id="Bridge.Open.Label" DefaultValue="Open MCP Bridge"/>` +
			`<Source>` + hub.DevPublicURL + `/stub/addin/pane.html</Source>`)},
		"pane.html":     {Data: []byte("<p>pane</p>")},
		"taskpane.html": {Data: []byte("<p>pane</p>")},
	}
}

type fixture struct {
	srv  *hub.Server
	http *httptest.Server
	uid  string
	stub *stub
	keys *auth.KeySet
}

// accessToken mints a JWT for the fixture's user with the given scopes, as
// the authorization server would.
func (f *fixture) accessToken(t *testing.T, scopes ...string) string {
	t.Helper()
	now := time.Now()
	tok, err := f.keys.Sign(auth.Claims{Issuer: f.srv.PublicURL(), Subject: f.uid, Audience: auth.Audience{f.srv.PublicURL()}, Scope: strings.Join(scopes, " "),
		ClientID: "test", JTI: "j", IssuedAt: now.Unix(), Expires: now.Add(auth.AccessTokenTTL).Unix()})
	if err != nil {
		t.Fatal(err)
	}
	return tok
}

// newFixture builds a fixture whose manifest handler behaves like production
// (no Id/DisplayName rewriting), which is what every test predating
// hub.Options.Environment assumed; TestManifestIdentity below exercises
// dev/staging explicitly.
func newFixture(t *testing.T, publicURL string) *fixture {
	t.Helper()
	return newFixtureEnv(t, publicURL, "prod")
}

func newFixtureEnv(t *testing.T, publicURL, environment string) *fixture {
	t.Helper()
	dev, err := auth.NewDevToken(token)
	if err != nil {
		t.Fatal(err)
	}
	pemData, _ := auth.GenerateKeyPEM()
	keys, err := auth.ParseKeySet(pemData)
	if err != nil {
		t.Fatal(err)
	}
	if publicURL == "" {
		publicURL = hub.DevPublicURL
	}
	// Bridges present the dev token; MCP clients present JWTs the fixture
	// mints for the same user id, the split cmd/hub wires in production.
	uid := dev.UserID()
	a := auth.Split{Access: &auth.JWTVerifier{Keys: keys, Issuer: publicURL, Audience: publicURL}, Bridge: dev}
	st := &stub{}
	srv, err := hub.NewServer(hub.Options{
		PublicURL:   publicURL,
		Environment: environment,
		Auth:        a,
		Connectors:  []hub.Connector{st},
		Logger:      slog.New(slog.NewTextHandler(io.Discard, nil)),
		Bridge:      bridge.Options{HelloTimeout: time.Second},
		// Tools driven over the in-memory transport have no bearer token; act
		// as the dev user, the same identity the bridge hello resolves to.
		UserOf: func(*mcp.CallToolRequest) (string, bool) { return uid, true },
	})
	if err != nil {
		t.Fatal(err)
	}
	hs := httptest.NewServer(srv.Handler())
	t.Cleanup(hs.Close)
	return &fixture{srv: srv, http: hs, uid: uid, stub: st, keys: keys}
}

func (f *fixture) dial(t *testing.T, instance string, docs ...protocol.Document) *bridgetest.Fake {
	t.Helper()
	fake := bridgetest.Dial(t, bridgetest.WSURL(f.http.URL, "/stub/bridge"), bridgetest.Options{Connector: "stub", Token: token, InstanceID: instance, Documents: docs})
	deadline := time.Now().Add(5 * time.Second)
	for len(f.srv.Host().Instances(f.uid, "stub")) < 1 || !hasInstance(f.srv.Host().Instances(f.uid, "stub"), instance) {
		if time.Now().After(deadline) {
			t.Fatalf("instance %s never registered", instance)
		}
		time.Sleep(5 * time.Millisecond)
	}
	return fake
}

func hasInstance(list []hub.Instance, id string) bool {
	for _, i := range list {
		if i.InstanceID == id {
			return true
		}
	}
	return false
}

func (f *fixture) inMemoryClient(t *testing.T) *mcp.ClientSession {
	t.Helper()
	ct, st := mcp.NewInMemoryTransports()
	ctx := context.Background()
	if _, err := f.srv.MCPServer("stub").Connect(ctx, st, nil); err != nil {
		t.Fatal(err)
	}
	cs, err := mcp.NewClient(&mcp.Implementation{Name: "test", Version: "0"}, nil).Connect(ctx, ct, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { cs.Close() })
	return cs
}

func TestHealthAndStatic(t *testing.T) {
	f := newFixture(t, "https://connectors.example")
	resp, err := http.Get(f.http.URL + "/health")
	if err != nil || resp.StatusCode != 200 {
		t.Fatalf("health: %v %v", err, resp)
	}
	resp.Body.Close()

	// Entra's publisher-domain check fetches this exact path on the apex domain.
	resp, err = http.Get(f.http.URL + "/.well-known/microsoft-identity-association.json")
	if err != nil || resp.StatusCode != 200 {
		t.Fatalf("identity association: %v %v", err, resp)
	}
	var assoc struct {
		Apps []struct {
			ID string `json:"applicationId"`
		} `json:"associatedApplications"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&assoc); err != nil || len(assoc.Apps) != 1 || assoc.Apps[0].ID == "" {
		t.Fatalf("identity association body: err=%v parsed=%+v", err, assoc)
	}
	resp.Body.Close()

	resp, err = http.Get(f.http.URL + "/stub/manifest.xml")
	if err != nil {
		t.Fatal(err)
	}
	body, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	want := `<Id>d9cd8bd0-fb31-48f1-bbbd-997f15140cc4</Id><DisplayName DefaultValue="Stub Connector"/>` +
		`<bt:String id="Bridge.Group.Label" DefaultValue="MCP Bridge"/><bt:String id="Bridge.Open.Label" DefaultValue="Open MCP Bridge"/>` +
		`<Source>https://connectors.example/stub/addin/pane.html</Source>`
	if resp.Header.Get("Content-Type") != "application/xml" || string(body) != want {
		t.Fatalf("manifest: %s (%s)", body, resp.Header.Get("Content-Type"))
	}

	resp, err = http.Get(f.http.URL + "/stub/addin/pane.html")
	if err != nil {
		t.Fatal(err)
	}
	body, _ = io.ReadAll(resp.Body)
	resp.Body.Close()
	if string(body) != "<p>pane</p>" {
		t.Fatalf("static: %s", body)
	}
}

func TestManifestUnchangedInDevMode(t *testing.T) {
	f := newFixture(t, "")
	resp, _ := http.Get(f.http.URL + "/stub/manifest.xml")
	body, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	if !strings.Contains(string(body), hub.DevPublicURL) {
		t.Fatalf("dev manifest was rewritten: %s", body)
	}
}

var (
	manifestIDRe   = regexp.MustCompile(`<Id>([^<]*)</Id>`)
	manifestNameRe = regexp.MustCompile(`<DisplayName DefaultValue="([^"]*)"`)
)

var (
	manifestGroupLabelRe = regexp.MustCompile(`<bt:String id="Bridge\.Group\.Label" DefaultValue="([^"]*)"`)
	manifestOpenLabelRe  = regexp.MustCompile(`<bt:String id="Bridge\.Open\.Label" DefaultValue="([^"]*)"`)
)

type manifestFields struct {
	id, displayName, groupLabel, openLabel string
}

func readManifestFields(t *testing.T, body []byte) manifestFields {
	t.Helper()
	id := manifestIDRe.FindSubmatch(body)
	name := manifestNameRe.FindSubmatch(body)
	group := manifestGroupLabelRe.FindSubmatch(body)
	open := manifestOpenLabelRe.FindSubmatch(body)
	if id == nil || name == nil || group == nil || open == nil {
		t.Fatalf("manifest missing Id, DisplayName or a ribbon label: %s", body)
	}
	return manifestFields{id: string(id[1]), displayName: string(name[1]), groupLabel: string(group[1]), openLabel: string(open[1])}
}

var uuidRe = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)

// TestManifestIdentity covers the fix for a live bug: Excel for the web keys
// a sideloaded add-in by manifest <Id>, and shows the ribbon group/button
// labels from the manifest's resource strings (not DisplayName). Serving the
// same Id (and identical-looking ribbon labels) for dev, staging and prod
// meant uploading the hosted manifest could silently re-launch a stale dev
// registration, and two sideloads looked identical in the ribbon. Production
// must keep the manifest file's own Id, DisplayName and labels (that's what
// the Store submission carries); every other environment gets a
// deterministic, distinct Id and a " (<environment>)" suffix on the name and
// both ribbon labels.
func TestManifestIdentity(t *testing.T) {
	const fileID = "d9cd8bd0-fb31-48f1-bbbd-997f15140cc4"
	const fileName = "Stub Connector"
	const fileGroupLabel = "MCP Bridge"
	const fileOpenLabel = "Open MCP Bridge"
	const publicURL = "https://connectors.example"

	fetch := func(env string) manifestFields {
		f := newFixtureEnv(t, publicURL, env)
		resp, err := http.Get(f.http.URL + "/stub/manifest.xml")
		if err != nil {
			t.Fatal(err)
		}
		body, _ := io.ReadAll(resp.Body)
		resp.Body.Close()
		return readManifestFields(t, body)
	}

	tests := []struct {
		env               string
		wantID            string // "" means "anything but the file Id or another environment's"
		wantSuffix        string // appended to DisplayName and both ribbon labels; "" means file value unchanged
		wantDeterministic bool   // Id must be a valid v4-shaped UUID
	}{
		{env: "prod", wantID: fileID, wantSuffix: ""},
		{env: "staging", wantSuffix: " (staging)", wantDeterministic: true},
		{env: "dev", wantSuffix: " (dev)", wantDeterministic: true},
	}

	got := map[string]manifestFields{}
	for _, tt := range tests {
		t.Run(tt.env, func(t *testing.T) {
			f := fetch(tt.env)
			got[tt.env] = f
			if tt.wantID != "" && f.id != tt.wantID {
				t.Errorf("id = %s, want unchanged file Id %s", f.id, tt.wantID)
			}
			if tt.wantDeterministic && !uuidRe.MatchString(f.id) {
				t.Errorf("id %s is not a valid v4-shaped UUID", f.id)
			}
			if f.displayName != fileName+tt.wantSuffix {
				t.Errorf("displayName = %q, want %q", f.displayName, fileName+tt.wantSuffix)
			}
			if f.groupLabel != fileGroupLabel+tt.wantSuffix {
				t.Errorf("groupLabel = %q, want %q", f.groupLabel, fileGroupLabel+tt.wantSuffix)
			}
			if f.openLabel != fileOpenLabel+tt.wantSuffix {
				t.Errorf("openLabel = %q, want %q", f.openLabel, fileOpenLabel+tt.wantSuffix)
			}
		})
	}

	if got["dev"].id == got["prod"].id || got["dev"].id == got["staging"].id || got["staging"].id == got["prod"].id {
		t.Fatalf("environments do not have distinct Ids: prod=%s staging=%s dev=%s", got["prod"].id, got["staging"].id, got["dev"].id)
	}

	// Same environment + PublicURL is stable across calls (and servers): a
	// redeploy of the same environment must not churn the add-in's identity
	// and force every user to re-sideload it.
	again := fetch("dev")
	if again != got["dev"] {
		t.Fatalf("dev identity not stable: %+v vs %+v", got["dev"], again)
	}
}

type bearer struct{ token string }

func (b bearer) RoundTrip(r *http.Request) (*http.Response, error) {
	if b.token != "" {
		r.Header.Set("Authorization", "Bearer "+b.token)
	}
	return http.DefaultTransport.RoundTrip(r)
}

func TestMCPEndpointRequiresBearer(t *testing.T) {
	f := newFixture(t, "")
	// No token, garbage, and the bridge's dev token (retired from /mcp):
	// all 401.
	for _, tok := range []string{"", "wrong-token-000000000", token} {
		tr := &mcp.StreamableClientTransport{Endpoint: f.http.URL + "/stub/mcp", HTTPClient: &http.Client{Transport: bearer{tok}}}
		_, err := mcp.NewClient(&mcp.Implementation{Name: "c", Version: "0"}, nil).Connect(context.Background(), tr, nil)
		if err == nil || !strings.Contains(err.Error(), "Unauthorized") {
			t.Fatalf("token %q: connect err %v, want Unauthorized", tok, err)
		}
	}
	// A valid token without this connector's scope: 403.
	tr := &mcp.StreamableClientTransport{Endpoint: f.http.URL + "/stub/mcp", HTTPClient: &http.Client{Transport: bearer{f.accessToken(t, "other")}}}
	if _, err := mcp.NewClient(&mcp.Implementation{Name: "c", Version: "0"}, nil).Connect(context.Background(), tr, nil); err == nil || !strings.Contains(err.Error(), "Forbidden") {
		t.Fatalf("token without scope: connect err %v, want Forbidden", err)
	}
	tr = &mcp.StreamableClientTransport{Endpoint: f.http.URL + "/stub/mcp", HTTPClient: &http.Client{Transport: bearer{f.accessToken(t, "stub")}}}
	cs, err := mcp.NewClient(&mcp.Implementation{Name: "c", Version: "0"}, nil).Connect(context.Background(), tr, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer cs.Close()
	tools, err := cs.ListTools(context.Background(), nil)
	if err != nil {
		t.Fatal(err)
	}
	var names []string
	for _, tl := range tools.Tools {
		names = append(names, tl.Name)
	}
	if strings.Join(names, ",") != "get_skills,list_instances" {
		t.Fatalf("tools: %v", names)
	}
	// Over HTTP the user comes from the token, not the test override: the
	// bridge that hello'd with the same token must be visible.
	f.dial(t, "http-inst")
	res, err := cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "list_instances", Arguments: map[string]any{}})
	if err != nil {
		t.Fatal(err)
	}
	var out hub.ListInstancesOut
	json.Unmarshal(mustJSON(res.StructuredContent), &out)
	if len(out.Instances) != 1 || out.Instances[0].InstanceID != "http-inst" {
		t.Fatalf("list_instances over HTTP: %+v (isError=%v)", out, res.IsError)
	}
}

func mustJSON(v any) []byte {
	b, _ := json.Marshal(v)
	return b
}

func TestGenericToolsInMemory(t *testing.T) {
	f := newFixture(t, "")
	cs := f.inMemoryClient(t)
	ctx := context.Background()

	res, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "get_skills", Arguments: map[string]any{}})
	if err != nil {
		t.Fatal(err)
	}
	if txt := res.Content[0].(*mcp.TextContent).Text; txt != "# stub skill\n" {
		t.Fatalf("get_skills text: %q", txt)
	}
	var sk hub.GetSkillsOut
	json.Unmarshal(mustJSON(res.StructuredContent), &sk)
	if sk.Connector != "stub" || sk.HubVersion != "dev" {
		t.Fatalf("get_skills structured: %+v", sk)
	}

	res, _ = cs.CallTool(ctx, &mcp.CallToolParams{Name: "list_instances", Arguments: map[string]any{}})
	var out hub.ListInstancesOut
	json.Unmarshal(mustJSON(res.StructuredContent), &out)
	if len(out.Instances) != 0 || res.IsError {
		t.Fatalf("empty registry: %+v", out)
	}

	f.dial(t, "i1", protocol.Document{ID: "d", Title: "Doc", Active: true, Detail: map[string]any{"sheet": "S"}})
	res, _ = cs.CallTool(ctx, &mcp.CallToolParams{Name: "list_instances", Arguments: map[string]any{}})
	json.Unmarshal(mustJSON(res.StructuredContent), &out)
	if len(out.Instances) != 1 || out.Instances[0].InstanceID != "i1" || out.Instances[0].Host.App != "Excel" ||
		len(out.Instances[0].Documents) != 1 || out.Instances[0].Documents[0].Detail["sheet"] != "S" {
		t.Fatalf("list_instances: %s", mustJSON(out))
	}
}

func TestExecResolution(t *testing.T) {
	f := newFixture(t, "")
	h := f.srv.Host()
	ctx := context.Background()
	script := hub.Script{Language: "js", Source: "x", Timeout: time.Second}

	if _, rec := h.Exec(ctx, f.uid, f.stub, hub.Target{}, script); rec == nil || rec.Code != "no-bridge" {
		t.Fatalf("no bridge: %+v", rec)
	}
	a := f.dial(t, "a", protocol.Document{ID: "d1", Active: true})
	res, rec := h.Exec(ctx, f.uid, f.stub, hub.Target{}, script)
	if rec != nil || string(res.Reply.Result) != `"x"` || res.Instance.InstanceID != "a" || res.Document.ID != "d1" {
		t.Fatalf("single bridge: %+v %+v", res, rec)
	}
	if _, rec := h.Exec(ctx, f.uid, f.stub, hub.Target{DocumentID: "nope"}, script); rec == nil || rec.Code != "unknown-document" {
		t.Fatalf("unknown document: %+v", rec)
	}
	if _, rec := h.Exec(ctx, f.uid, f.stub, hub.Target{InstanceID: "zzz"}, script); rec == nil || rec.Code != "unknown-instance" {
		t.Fatalf("unknown instance: %+v", rec)
	}
	f.dial(t, "b")
	if _, rec := h.Exec(ctx, f.uid, f.stub, hub.Target{}, script); rec == nil || rec.Code != "ambiguous-instance" {
		t.Fatalf("two bridges: %+v", rec)
	}
	if _, rec := h.Exec(ctx, f.uid, f.stub, hub.Target{InstanceID: "b"}, script); rec != nil {
		t.Fatalf("explicit instance: %+v", rec)
	}
	// Another user sees none of them.
	if _, rec := h.Exec(ctx, "someone-else", f.stub, hub.Target{}, script); rec == nil || rec.Code != "no-bridge" {
		t.Fatalf("other user: %+v", rec)
	}

	f.stub.validateErr = errors.New("too big")
	if _, rec := h.Exec(ctx, f.uid, f.stub, hub.Target{InstanceID: "a"}, script); rec == nil || rec.Code != "invalid-script" {
		t.Fatalf("validate: %+v", rec)
	}
	f.stub.validateErr = nil
	if _, rec := h.Exec(ctx, f.uid, f.stub, hub.Target{InstanceID: "a"}, hub.Script{Source: "x", Timeout: time.Hour}); rec == nil || rec.Code != "invalid-timeout" {
		t.Fatalf("timeout cap: %+v", rec)
	}

	a.Handle = bridgetest.NoReply
	_, rec = h.Exec(ctx, f.uid, f.stub, hub.Target{InstanceID: "a"}, hub.Script{Source: "hang", Timeout: 50 * time.Millisecond})
	if rec == nil || rec.Code != "timeout" || rec.Detail["timeout_ms"] != int64(50) {
		t.Fatalf("timeout: %+v", rec)
	}
	a.Close()
	deadline := time.Now().Add(5 * time.Second)
	for hasInstance(h.Instances(f.uid, "stub"), "a") && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	if _, rec := h.Exec(ctx, f.uid, f.stub, hub.Target{InstanceID: "a"}, script); rec == nil || rec.Code != "unknown-instance" {
		t.Fatalf("after disconnect: %+v", rec)
	}
}

// TestDiscovery: the 401 carries the resource-metadata pointer Claude
// clients follow, and the metadata names this connector's MCP URL as the
// resource, the hub as the authorization server, and the slug as the scope.
func TestDiscovery(t *testing.T) {
	f := newFixture(t, "https://connectors.example")
	resp, err := http.Post(f.http.URL+"/stub/mcp", "application/json", strings.NewReader("{}"))
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	want := `Bearer resource_metadata="https://connectors.example/stub/.well-known/oauth-protected-resource", scope="stub"`
	if resp.StatusCode != 401 || resp.Header.Get("WWW-Authenticate") != want {
		t.Fatalf("401 challenge: %d %q", resp.StatusCode, resp.Header.Get("WWW-Authenticate"))
	}
	for _, path := range []string{"/stub/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/stub/mcp"} {
		resp, err = http.Get(f.http.URL + path)
		if err != nil {
			t.Fatal(err)
		}
		var prm struct {
			Resource             string   `json:"resource"`
			AuthorizationServers []string `json:"authorization_servers"`
			ScopesSupported      []string `json:"scopes_supported"`
		}
		body, _ := io.ReadAll(resp.Body)
		resp.Body.Close()
		if err := json.Unmarshal(body, &prm); err != nil || resp.StatusCode != 200 {
			t.Fatalf("%s: %d %s", path, resp.StatusCode, body)
		}
		if prm.Resource != "https://connectors.example/stub/mcp" || strings.Join(prm.AuthorizationServers, ",") != "https://connectors.example" || strings.Join(prm.ScopesSupported, ",") != "stub" {
			t.Fatalf("%s: %+v", path, prm)
		}
	}
	resp, _ = http.Get(f.http.URL + "/health")
	resp.Body.Close()
	if resp.Header.Get("Hub-Version") != "dev" {
		t.Fatalf("health version header: %q", resp.Header.Get("Hub-Version"))
	}
}

func TestNewServerValidation(t *testing.T) {
	a, _ := auth.NewDevToken(token)
	if _, err := hub.NewServer(hub.Options{Connectors: []hub.Connector{&stub{}}}); err == nil {
		t.Fatal("no auth accepted")
	}
	if _, err := hub.NewServer(hub.Options{Auth: a}); err == nil {
		t.Fatal("no connectors accepted")
	}
	if _, err := hub.NewServer(hub.Options{Auth: a, Connectors: []hub.Connector{&stub{}, &stub{}}}); err == nil {
		t.Fatal("duplicate slug accepted")
	}
	if _, err := hub.NewServer(hub.Options{Auth: a, Connectors: []hub.Connector{&stub{}}, PublicURL: "connectors.example"}); err == nil {
		t.Fatal("relative public URL accepted")
	}
	if _, err := hub.NewServer(hub.Options{Auth: a, Connectors: []hub.Connector{&stub{}}, Environment: "production"}); err == nil {
		t.Fatal("invalid environment accepted")
	}
}
