package hub_test

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub"
	"github.com/eichler-ai/connectors/hub/bridgetest"
	"github.com/eichler-ai/connectors/hub/internal/bridge"
	"github.com/eichler-ai/connectors/hub/internal/files"
	"github.com/eichler-ai/connectors/hub/internal/store"
	"github.com/eichler-ai/connectors/hub/protocol"
	"github.com/eichler-ai/connectors/internal/auth"
)

// TestAuditExecRow: a successful exec writes exactly one row, with the
// script hash/bounded text/language/outcome — and no result content.
func TestAuditExecRow(t *testing.T) {
	f := newFixture(t, "")
	f.dial(t, "a", protocol.Document{ID: "d1", Active: true})
	ctx := context.Background()
	script := hub.Script{Language: "js", Source: "x", Timeout: time.Second}

	if _, rec := f.srv.Host().Exec(ctx, f.uid, f.stub, hub.Target{InstanceID: "a", Client: "test-client"}, script); rec != nil {
		t.Fatalf("exec: %+v", rec)
	}
	rows, err := f.store.RecentAudit(ctx, f.uid, 10)
	if err != nil || len(rows) != 1 {
		t.Fatalf("audit rows: %v %+v", err, rows)
	}
	row := rows[0]
	if row.Action != "exec" || !row.OK || row.Connector != "stub" || row.Instance != "a" || row.Document != "d1" {
		t.Fatalf("exec row: %+v", row)
	}
	if row.ScriptSHA256 == "" || row.ScriptBounded != "x" || row.Language != "js" || row.Client != "test-client" {
		t.Fatalf("exec row script fields: %+v", row)
	}
	if row.ExpiresAt.Before(row.Timestamp) {
		t.Fatalf("exec row expires_at before timestamp: %+v", row)
	}
	// No result content anywhere on the row (§12: never the script result).
	b, _ := json.Marshal(row)
	if strings.Contains(string(b), `"x"`) && row.ScriptBounded != "x" {
		t.Fatalf("unexpected script content leaked: %s", b)
	}
	if strings.Contains(string(b), "result") && !strings.Contains(string(b), "result_bytes") {
		t.Fatalf("row carries a result field beyond result_bytes: %s", b)
	}
}

// TestAuditExportImportRows: export and import each write exactly one row,
// with sizes and format but never bytes or sheet names.
func TestAuditExportImportRows(t *testing.T) {
	f := newFixture(t, "")
	fake := f.dial(t, "exp1", protocol.Document{ID: "d1", Title: "Doc", Active: true})
	body := []byte("%PDF-1.4 stand-in bytes")
	uploaded := make(chan struct{})
	go func() {
		msgs := fake.WaitFor(protocol.MethodExport, 1, 5*time.Second)
		var ex protocol.Export
		_ = msgs[0].Decode(&ex)
		req, _ := http.NewRequest(http.MethodPost, f.http.URL+"/stub/files?id="+ex.ID, bytes.NewReader(body))
		req.Header.Set("Authorization", "Bearer "+token)
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Error(err)
			return
		}
		resp.Body.Close()
		close(uploaded)
	}()
	ctx := context.Background()
	if _, rec := f.srv.Host().Export(ctx, f.uid, f.stub, hub.Target{InstanceID: "exp1"}, hub.ExportRequest{Format: "pdf", Timeout: 5 * time.Second}); rec != nil {
		t.Fatalf("export: %+v", rec)
	}
	<-uploaded

	xlsx, err := sampleXLSX()
	if err != nil {
		t.Fatal(err)
	}
	fake2 := f.dial(t, "imp1", protocol.Document{ID: "d2", Title: "Doc.xlsx", Active: true})
	done := make(chan struct{})
	go func() {
		msgs := fake2.WaitFor(protocol.MethodImport, 1, 5*time.Second)
		var im protocol.Import
		_ = msgs[0].Decode(&im)
		fake2.Send(protocol.New(protocol.MethodResult, protocol.Result{ID: im.ID, OK: true,
			Result: json.RawMessage(`{"added_sheets":["Data"],"all_sheets":["Sheet1","Data"]}`)}))
		close(done)
	}()
	if _, rec := f.srv.Host().Import(ctx, f.uid, f.stub, hub.Target{InstanceID: "imp1"},
		hub.ImportRequest{Source: hub.ImportSource{ContentBase64: base64.StdEncoding.EncodeToString(xlsx)}, Timeout: 5 * time.Second}); rec != nil {
		t.Fatalf("import: %+v", rec)
	}
	<-done

	rows, err := f.store.RecentAudit(ctx, f.uid, 10)
	if err != nil || len(rows) != 2 {
		t.Fatalf("audit rows: %v %+v", err, rows)
	}
	// Newest first: import, then export.
	imp, exp := rows[0], rows[1]
	if imp.Action != "import" || !imp.OK || imp.FileBytes != int64(len(xlsx)) || imp.Format != "xlsx" {
		t.Fatalf("import row: %+v", imp)
	}
	if exp.Action != "export" || !exp.OK || exp.FileBytes != int64(len(body)) || exp.Format != "pdf" {
		t.Fatalf("export row: %+v", exp)
	}
	// Never the bytes or sheet names.
	b, _ := json.Marshal(rows)
	if strings.Contains(string(b), string(body)) || strings.Contains(string(b), "Sheet1") || strings.Contains(string(b), "\"Data\"") {
		t.Fatalf("audit rows leaked file content or sheet names: %s", b)
	}
}

// TestAuditNoRowOnNoBridge: a hub-level pre-run failure (no bridge connected)
// writes no row — nothing executed, so there is nothing to audit.
func TestAuditNoRowOnNoBridge(t *testing.T) {
	f := newFixture(t, "")
	ctx := context.Background()
	if _, rec := f.srv.Host().Exec(ctx, f.uid, f.stub, hub.Target{}, hub.Script{Language: "js", Source: "x", Timeout: time.Second}); rec == nil || rec.Code != "no-bridge" {
		t.Fatalf("expected no-bridge: %+v", rec)
	}
	rows, err := f.store.RecentAudit(ctx, f.uid, 10)
	if err != nil || len(rows) != 0 {
		t.Fatalf("expected no audit rows for a no-bridge failure: %v %+v", err, rows)
	}
}

// TestAuditSkipsInternalExec: a Script marked Internal (get_status's fixed
// probe) writes no audit row, even though it round-trips to the bridge — the
// audit trail is the §13 control for arbitrary code, not for a hub-authored
// read (#254).
func TestAuditSkipsInternalExec(t *testing.T) {
	f := newFixture(t, "")
	f.dial(t, "a", protocol.Document{ID: "d1", Active: true})
	ctx := context.Background()
	script := hub.Script{Language: "js", Source: "x", Timeout: time.Second, Internal: true}

	if _, rec := f.srv.Host().Exec(ctx, f.uid, f.stub, hub.Target{InstanceID: "a"}, script); rec != nil {
		t.Fatalf("exec: %+v", rec)
	}
	rows, err := f.store.RecentAudit(ctx, f.uid, 10)
	if err != nil || len(rows) != 0 {
		t.Fatalf("an internal exec must write no audit row: %v %+v", err, rows)
	}
}

// failingAuditStore wraps a real store but fails every audit write, to
// prove a Firestore outage never blocks the user's exec.
type failingAuditStore struct{ store.Store }

func (failingAuditStore) PutAuditRow(context.Context, store.AuditRow) error {
	return errors.New("firestore is down")
}

// TestAuditWriteFailureNonBlocking: Exec still returns its result when the
// audit store errors, and the failure is logged with the stable
// audit-write-failed code.
func TestAuditWriteFailureNonBlocking(t *testing.T) {
	dev, err := auth.NewDevToken(token)
	if err != nil {
		t.Fatal(err)
	}
	pemData, _ := auth.GenerateKeyPEM()
	keys, err := auth.ParseKeySet(pemData)
	if err != nil {
		t.Fatal(err)
	}
	publicURL := hub.DevPublicURL
	uid := dev.UserID()
	fs, err := files.NewTemp(publicURL)
	if err != nil {
		t.Fatal(err)
	}
	var logBuf bytes.Buffer
	logger := slog.New(slog.NewTextHandler(&logBuf, nil))
	st := &stub{}
	srv, err := hub.NewServer(hub.Options{
		PublicURL:  publicURL,
		Auth:       auth.Split{Access: &auth.JWTVerifier{Keys: keys, Issuer: publicURL, Audience: publicURL}, Bridge: dev},
		Connectors: []hub.Connector{st},
		Files:      fs,
		Store:      failingAuditStore{store.NewMemory()},
		Logger:     logger,
		Bridge:     bridge.Options{HelloTimeout: time.Second},
		UserOf:     func(*mcp.CallToolRequest) (string, bool) { return uid, true },
	})
	if err != nil {
		t.Fatal(err)
	}
	hs := httptest.NewServer(srv.Handler())
	t.Cleanup(hs.Close)

	fake := bridgetest.Dial(t, bridgetest.WSURL(hs.URL, "/stub/bridge"), bridgetest.Options{Connector: "stub", Token: token, InstanceID: "a"})
	t.Cleanup(fake.Close)
	deadline := time.Now().Add(5 * time.Second)
	for len(srv.Host().Instances(uid, "stub")) < 1 {
		if time.Now().After(deadline) {
			t.Fatal("instance never registered")
		}
		time.Sleep(5 * time.Millisecond)
	}

	res, rec := srv.Host().Exec(context.Background(), uid, st, hub.Target{InstanceID: "a"}, hub.Script{Language: "js", Source: "x", Timeout: time.Second})
	if rec != nil || string(res.Reply.Result) != `"x"` {
		t.Fatalf("exec must still succeed when the audit store errors: %+v %+v", res, rec)
	}
	if !strings.Contains(logBuf.String(), "audit-write-failed") {
		t.Fatalf("expected an audit-write-failed log line, got: %s", logBuf.String())
	}
}
