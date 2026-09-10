package hub

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub/diag"
	"github.com/eichler-ai/connectors/hub/internal/bridge"
	"github.com/eichler-ai/connectors/hub/internal/files"
	"github.com/eichler-ai/connectors/hub/internal/graph"
	"github.com/eichler-ai/connectors/hub/internal/graphtoken"
	"github.com/eichler-ai/connectors/hub/internal/registry"
	"github.com/eichler-ai/connectors/hub/internal/store"
	"github.com/eichler-ai/connectors/hub/protocol"
	"github.com/eichler-ai/connectors/internal/auth"
)

// Files is the file exchange seam an export_file-style tool stores into
// (§10, §11): Put lands a stream at files/{user}/{connector}/{id}.{ext},
// SignedURL mints a short-lived URL for it. Aliased from hub/internal/files
// so the GCS and temp-dir implementations live there, next to Registry's and
// Bridge's own internal packages, while every connector package — which
// cannot import hub/internal/files itself, only what hub re-exports — can
// still name the type to build a fixture with a fake Store.
type Files = files.Store

// ObjectRef is what Files.Put returns and Files.SignedURL takes.
type ObjectRef = files.ObjectRef

// Host is the set of hub services a connector's tools call: Exec, Export,
// the registry, and the identity of the calling MCP session. One Host serves
// every connector; the connector slug travels with each call.
type Host struct {
	reg     *registry.Registry
	bridges *bridge.Service
	files   files.Store
	// store is the audit trail's persistence (§11, §12, §13). Nil disables
	// audit writes entirely (a hub built without a store, as some tests
	// construct); production and -dev both pass one (Firestore or Memory).
	store store.Store
	log   *slog.Logger

	// keys decrypts the Microsoft refresh token graphtoken stores (RFC
	// excel/docs/rfc-graph-create-and-open.md §3.1). Nil in hubs/tests that
	// never wire Graph — CreateWorkbookFile then reports graph-unconfigured
	// rather than a nil-pointer panic.
	keys *auth.KeySet
	// graphClient is the Microsoft Graph API path (§3.1, §3.2). Nil for the
	// same reason as keys, and checked alongside it.
	graphClient *graph.Client

	// userOf resolves the MCP session's user. In production it reads the
	// TokenInfo the bearer middleware attached; tests, which drive tools over
	// the SDK's in-memory transport where no HTTP layer exists, substitute a
	// fixed identity. It is the only injection point and exists for that.
	userOf func(req *mcp.CallToolRequest) (string, bool)

	defaultTimeout, maxTimeout time.Duration
}

const (
	// DefaultTimeout applies when a tool call names none; MaxTimeout caps what
	// it may ask for. 30 s matches the POC's measured round trips (§03);
	// 10 min covers a whole-document PDF export with margin and stays well
	// inside Cloud Run's 60-minute request ceiling.
	DefaultTimeout = 30 * time.Second
	MaxTimeout     = 10 * time.Minute
	// ExportURLTTL is how long a signed URL for an exported file stays valid
	// (PRD §11: "short-lived signed URLs"). 15 min covers a slow client
	// fetching a large PDF without leaving the link usable for long.
	ExportURLTTL = 15 * time.Minute
	// Source labels diagnostics raised by the hub itself.
	Source = "hub"
	// AuditRetention is how long an audit row lives (§11 "bounded retention");
	// deploy.sh puts a matching Firestore TTL policy on ExpiresAt.
	AuditRetention = 90 * 24 * time.Hour
	// auditScriptBound caps the script text an audit row keeps (§12): enough
	// for a reviewer to see what ran, never an unbounded blob.
	auditScriptBound = 2 << 10
)

// Instance is one live bridge as tools report it (list_instances, get_status).
type Instance struct {
	InstanceID     string              `json:"instance_id"`
	Host           protocol.Host       `json:"host"`
	BridgeVersion  string              `json:"bridge_version"`
	ConnectedSince time.Time           `json:"connected_since"`
	Documents      []protocol.Document `json:"documents"`
}

func instanceOf(b *registry.Bridge) Instance {
	docs := b.Documents()
	if docs == nil {
		docs = []protocol.Document{}
	}
	return Instance{InstanceID: b.InstanceID, Host: b.Host, BridgeVersion: b.BridgeVersion, ConnectedSince: b.Since, Documents: docs}
}

// Result is a completed exec: the bridge's reply plus where it ran.
type Result struct {
	Reply    protocol.Result
	Instance Instance
	// Document is the document the exec was addressed to (the active one
	// when Target.DocumentID was empty), as the bridge last registered it.
	Document protocol.Document
}

// User returns the calling session's user id, or an `unauthenticated` record.
// Tool handlers call this first; a missing identity is a wiring bug (the
// endpoint is behind the bearer middleware), so it fails loudly.
func (h *Host) User(req *mcp.CallToolRequest) (string, *diag.Record) {
	if uid, ok := h.userOf(req); ok && uid != "" {
		return uid, nil
	}
	return "", diag.New(diag.SeverityError, "unauthenticated", Source, "the MCP session carries no user identity").
		WithRemedy("reconnect the MCP client with a valid bearer token")
}

// SignedInLabel is a human name for the account the MCP session is signed in
// as — email, else display name — so list_instances/get_status can show it and
// the user can compare it against what the pane's own "Signed in as …" shows
// (issue #251: an account mismatch between the pane and the session otherwise
// looks like "no bridge" with no hint why). Best-effort and read-only: a nil
// store or an unknown user returns "" and the caller falls back to generic
// wording. Mirrors authserver.signedInLabel, the consent page's version.
func (h *Host) SignedInLabel(ctx context.Context, user string) string {
	if h.store == nil || user == "" {
		return ""
	}
	u, err := h.store.User(ctx, user)
	if err != nil {
		return ""
	}
	if u.Email != "" {
		return u.Email
	}
	return u.DisplayName
}

// ClientName returns the calling MCP client's declared name
// (Implementation.Name from its initialize call), for a connector's tool
// handler to pass into Target.Client. Empty when the session's initialize
// params are not available — the Streamable HTTP handler runs each request
// as its own stateless session (see Options.Bridge/NewServer), and not every
// client/transport combination surfaces InitializeParams to the handler by
// the time a tool call arrives. Best-effort metadata only; never treated as
// required.
func ClientName(req *mcp.CallToolRequest) string {
	if req == nil || req.Session == nil {
		return ""
	}
	ip := req.Session.InitializeParams()
	if ip == nil || ip.ClientInfo == nil {
		return ""
	}
	return ip.ClientInfo.Name
}

// Log is the hub's logger, for a connector tool that needs to add its own
// structured line beside what a Host method already logs — e.g.
// create_workbook logging the doc_key it constructs next to the driveItem
// CreateWorkbookFile already logged, so the RFC §3.3 correlation spike's two
// halves land in the same log stream.
func (h *Host) Log() *slog.Logger { return h.log }

// Instances lists the user's live bridges for a connector, oldest first.
func (h *Host) Instances(user, connector string) []Instance {
	var out []Instance
	for _, b := range h.reg.List(user, connector) {
		out = append(out, instanceOf(b))
	}
	if out == nil {
		out = []Instance{}
	}
	return out
}

// Resolve picks the bridge a target names, applying the §07 rule: an
// explicit instance_id must exist; no instance_id is fine only when the user
// has exactly one bridge for the connector.
func (h *Host) resolve(user, connector string, target Target) (*registry.Bridge, *diag.Record) {
	if target.InstanceID != "" {
		b, ok := h.reg.Get(user, connector, target.InstanceID)
		if !ok {
			return nil, diag.New(diag.SeverityError, "unknown-instance", Source,
				fmt.Sprintf("no %s bridge with instance_id %q is connected for this user", connector, target.InstanceID)).
				WithRemedy("call list_instances and use one of the instance_id values it returns")
		}
		return b, nil
	}
	all := h.reg.List(user, connector)
	switch len(all) {
	case 1:
		return all[0], nil
	case 0:
		return nil, diag.New(diag.SeverityError, "no-bridge", Source,
			fmt.Sprintf("no %s bridge is connected for this user", connector)).
			WithRemedy("open the connector's task pane in the host application and check it shows Connected",
				"if the pane is already open and shows Connected, it is signed in as a different Microsoft account than this MCP session — use 'Switch account' in the pane to sign in with the same account (list_instances reports which account this session is signed in as)",
				"call list_instances to confirm it appears")
	default:
		ids := make([]string, 0, len(all))
		for _, b := range all {
			ids = append(ids, b.InstanceID)
		}
		return nil, diag.New(diag.SeverityError, "ambiguous-instance", Source,
			fmt.Sprintf("%d %s bridges are connected for this user; instance_id is required", len(all), connector)).
			WithDetail(map[string]any{"instance_ids": ids}).
			WithRemedy("call list_instances and pass the instance_id of the one you mean")
	}
}

// Exec runs script on the connector's bridge for user (§09). Failures the hub
// itself detects come back as a diagnostic record with a stable code; a
// script that threw is a Result with OK=false, which the caller reports as
// an error with the script's own detail.
func (h *Host) Exec(ctx context.Context, user string, c Connector, target Target, script Script) (Result, *diag.Record) {
	if err := c.Validate(ctx, script); err != nil {
		return Result{}, diag.New(diag.SeverityError, "invalid-script", Source, err.Error())
	}
	timeout := script.Timeout
	if timeout <= 0 {
		timeout = h.defaultTimeout
	}
	if timeout > h.maxTimeout {
		return Result{}, diag.New(diag.SeverityError, "invalid-timeout", Source,
			fmt.Sprintf("timeout %s exceeds the maximum of %s", timeout, h.maxTimeout))
	}
	b, rec := h.resolve(user, c.Slug(), target)
	if rec != nil {
		return Result{}, rec
	}
	doc, found := b.Document(target.DocumentID)
	if target.DocumentID != "" && !found {
		// Advertised-but-unhonoured addressing must fail loudly, never fall
		// back to the active document (CONVENTIONS.md).
		return Result{}, diag.New(diag.SeverityError, "unknown-document", Source,
			fmt.Sprintf("bridge %s has no document with id %q", b.InstanceID, target.DocumentID)).
			WithRemedy("call list_instances for the current document ids")
	}

	hash := sha256.Sum256([]byte(script.Source))
	hashHex := hex.EncodeToString(hash[:])
	start := time.Now()
	res, err := h.bridges.Exec(ctx, b, bridge.ExecRequest{DocumentID: target.DocumentID, Script: script.Source, Language: script.Language, Timeout: timeout})
	elapsed := time.Since(start)
	// The audit line (§12): identities, hash, outcome, sizes — never the
	// script or the result.
	attrs := []any{"user", user, "connector", c.Slug(), "instance_id", b.InstanceID, "document", doc.ID,
		"script_sha256", hashHex[:16], "script_bytes", len(script.Source), "elapsed", elapsed.Round(time.Millisecond)}
	// From here on the bridge was reached (a request went out on the wire),
	// which is the §13 boundary for writing an audit row: everything above
	// (invalid-script, invalid-timeout, resolve/unknown-document) short-
	// circuits before anything executed, so there is nothing to audit.
	if err != nil {
		h.log.Info("exec: failed", append(attrs, "err", err)...)
		rec := execError(err, b, timeout)
		if !script.Internal {
			h.putAudit(store.AuditRow{UserID: user, Connector: c.Slug(), Instance: b.InstanceID, Document: doc.ID, Action: "exec",
				ScriptSHA256: hashHex, ScriptBounded: boundScript(script.Source), Language: script.Language,
				OK: false, Code: rec.Code, DurationMs: elapsed.Milliseconds(), Client: target.Client})
		}
		return Result{}, rec
	}
	code := ""
	if res.Error != nil {
		code = res.Error.Code
		if code == "" {
			code = res.Error.Name
		}
	}
	h.log.Info("exec: done", append(attrs, "ok", res.OK, "code", code, "result_bytes", len(res.Result), "truncated", res.Truncated, "notices", len(res.Notices))...)
	// An internal probe (get_status) is a fixed hub script, not agent code, so
	// it is not audited (§13 audits arbitrary execution) — but it is still
	// logged above, so nothing is lost from the operational view.
	if !script.Internal {
		h.putAudit(store.AuditRow{UserID: user, Connector: c.Slug(), Instance: b.InstanceID, Document: doc.ID, Action: "exec",
			ScriptSHA256: hashHex, ScriptBounded: boundScript(script.Source), Language: script.Language,
			OK: res.OK, Code: code, DurationMs: elapsed.Milliseconds(), ResultBytes: len(res.Result), Client: target.Client})
	}
	return Result{Reply: res, Instance: instanceOf(b), Document: doc}, nil
}

// ExportRequest is what a connector asks the hub to have the bridge produce
// as a file (§10, §11).
type ExportRequest struct {
	// Format is a connector-defined tag (Excel: csv, xlsx, pdf) that becomes
	// the stored object's extension — trusted because it travels through
	// bridge.Service's pending-export record, not because the eventual
	// uploader says so (see bridge.Service.UploadAuthorize).
	Format     string
	DocumentID string
	Timeout    time.Duration
}

// ExportResult is a completed export: where the bytes ended up.
type ExportResult struct {
	Instance  Instance
	Document  protocol.Document
	URL       string
	Bytes     int64
	ExpiresAt time.Time
}

// uploadPayload is the JSON the files upload handler puts in the synthetic
// `result` it delivers through bridge.Service.CompleteExport; Export decodes
// it back out. It never leaves this process — it is not part of the wire
// protocol between hub and bridge, only between the upload handler and Export.
type uploadPayload struct {
	URL       string    `json:"url"`
	Bytes     int64     `json:"bytes"`
	ExpiresAt time.Time `json:"expires_at"`
}

// Export asks the connector's bridge to produce a file and land it in the
// file store, and waits for either the bridge's own failure `result` (it
// never got as far as uploading — e.g. a failed getFileAsync) or the files
// upload handler's success delivery (§10, §11). Files must be configured;
// a connector with Capabilities().Bridge but no export tool simply never
// calls this.
func (h *Host) Export(ctx context.Context, user string, c Connector, target Target, req ExportRequest) (ExportResult, *diag.Record) {
	if h.files == nil {
		return ExportResult{}, diag.New(diag.SeverityError, "files-unconfigured", Source, "the hub has no file store configured; exports are unavailable")
	}
	timeout := req.Timeout
	if timeout <= 0 {
		timeout = h.defaultTimeout
	}
	if timeout > h.maxTimeout {
		return ExportResult{}, diag.New(diag.SeverityError, "invalid-timeout", Source,
			fmt.Sprintf("timeout %s exceeds the maximum of %s", timeout, h.maxTimeout))
	}
	b, rec := h.resolve(user, c.Slug(), target)
	if rec != nil {
		return ExportResult{}, rec
	}
	doc, found := b.Document(target.DocumentID)
	if target.DocumentID != "" && !found {
		return ExportResult{}, diag.New(diag.SeverityError, "unknown-document", Source,
			fmt.Sprintf("bridge %s has no document with id %q", b.InstanceID, target.DocumentID)).
			WithRemedy("call list_instances for the current document ids")
	}

	start := time.Now()
	res, err := h.bridges.Export(ctx, b, bridge.ExportRequest{DocumentID: target.DocumentID, Format: req.Format, Timeout: timeout})
	elapsed := time.Since(start)
	attrs := []any{"user", user, "connector", c.Slug(), "instance_id", b.InstanceID, "document", doc.ID,
		"format", req.Format, "elapsed", elapsed.Round(time.Millisecond)}
	// Same boundary as Exec: the bridge was reached from here on, so every
	// path below writes a row.
	if err != nil {
		h.log.Info("export: failed", append(attrs, "err", err)...)
		rec := execError(err, b, timeout)
		h.putAudit(store.AuditRow{UserID: user, Connector: c.Slug(), Instance: b.InstanceID, Document: doc.ID, Action: "export",
			OK: false, Code: rec.Code, DurationMs: elapsed.Milliseconds(), Format: req.Format, Client: target.Client})
		return ExportResult{}, rec
	}
	if !res.OK {
		code := exportErrorCode(res.Error)
		h.log.Info("export: failed", append(attrs, "code", code)...)
		h.putAudit(store.AuditRow{UserID: user, Connector: c.Slug(), Instance: b.InstanceID, Document: doc.ID, Action: "export",
			OK: false, Code: code, DurationMs: elapsed.Milliseconds(), Format: req.Format, Client: target.Client})
		return ExportResult{}, exportError(res.Error)
	}
	var payload uploadPayload
	if err := json.Unmarshal(res.Result, &payload); err != nil {
		rec := diag.New(diag.SeverityError, "bad-upload", Source, "the upload handler's outcome was not the expected shape: "+err.Error())
		h.log.Info("export: failed", append(attrs, "code", rec.Code)...)
		h.putAudit(store.AuditRow{UserID: user, Connector: c.Slug(), Instance: b.InstanceID, Document: doc.ID, Action: "export",
			OK: false, Code: rec.Code, DurationMs: elapsed.Milliseconds(), Format: req.Format, Client: target.Client})
		return ExportResult{}, rec
	}
	// The audit line (§12): identities, format, size — never the bytes.
	h.log.Info("export: done", append(attrs, "bytes", payload.Bytes)...)
	h.putAudit(store.AuditRow{UserID: user, Connector: c.Slug(), Instance: b.InstanceID, Document: doc.ID, Action: "export",
		OK: true, DurationMs: elapsed.Milliseconds(), FileBytes: payload.Bytes, Format: req.Format, Client: target.Client})
	return ExportResult{Instance: instanceOf(b), Document: doc, URL: payload.URL, Bytes: payload.Bytes, ExpiresAt: payload.ExpiresAt}, nil
}

// graphFolder is where create_workbook puts files (RFC §3.2): a visible
// folder rather than a hidden app folder, since the broad Files.ReadWrite
// scope (§5) means the connector isn't confined to one anyway.
const graphFolder = "Eichler Connectors"

// DriveItem is what a completed CreateWorkbookFile upload returns from
// Microsoft Graph: the subset a connector's create_workbook tool needs to
// build its response and the pane-matching doc_key (RFC §3.2, §3.3).
// Defined here rather than aliasing hub/internal/graph.DriveItem so a
// connector package — which cannot import hub/internal/* — can still name
// the type, the same reasoning as Files/ObjectRef above.
type DriveItem struct {
	ID     string
	Name   string
	WebURL string
	// DriveID is the CID segment of ID (before "!") — half of the RFC §3.3
	// spike's pane-matching identity: a personal OneDrive file's
	// Office.context.document.url is
	// https://d.docs.live.net/<CID>/<Folder>/<Name> (no <Folder> segment for
	// a root-level file — live-verified 2026-09-08, see graph.DriveItem.Folder).
	DriveID string
	// Folder is the other half: the OneDrive folder path (no leading/
	// trailing slash), or "" for a root-level file.
	Folder string
}

// CreateWorkbookFile uploads content as name into the user's OneDrive via
// Microsoft Graph (RFC excel/docs/rfc-graph-create-and-open.md §3.1, §3.2):
// decrypts their stored Microsoft refresh token, asks the Graph client to
// mint a fresh access token and PUT the file, and persists whatever refresh
// token Graph hands back (rotated or not) before reporting the outcome —
// so a token Microsoft rotated mid-call is never dropped even when the
// upload itself then fails. A user who never granted Files access (or was
// revoked) gets graph-not-connected, not a Graph error; a hub with no Graph
// client wired at all (see NewServer) gets graph-unconfigured.
//
// sheets is how many worksheets content holds, for the audit row only (§5:
// the broad Graph scope's writes must be audited). Once the Graph PUT is
// attempted — the same §13 boundary Exec/Export/Import use — every outcome
// writes one "create" row (name, drive-item id, sheet count, byte size; never
// the content or sheet names); the pre-attempt refusals above it write none.
// c is taken (rather than a hardcoded slug) only so the audit row is stamped
// with the calling connector, exactly as Exec/Export/Import stamp theirs.
func (h *Host) CreateWorkbookFile(ctx context.Context, user string, c Connector, name string, content []byte, sheets int) (DriveItem, *diag.Record) {
	if h.graphClient == nil || h.keys == nil {
		return DriveItem{}, diag.New(diag.SeverityError, "graph-unconfigured", Source,
			"this hub has no Microsoft Graph client configured").
			WithRemedy("this is a server configuration issue, not something the caller can fix")
	}
	refreshToken, err := graphtoken.Get(ctx, h.store, h.keys, user)
	if errors.Is(err, store.ErrNotFound) {
		return DriveItem{}, diag.New(diag.SeverityError, "graph-not-connected", Source,
			"no Microsoft file access is on record for you").
			WithRemedy("sign in again to grant file access (sign-in now asks for it)")
	}
	if err != nil {
		return DriveItem{}, diag.New(diag.SeverityError, "graph-token-unreadable", Source,
			"the stored Microsoft token could not be read: "+err.Error())
	}
	start := time.Now()
	item, nextRefresh, createErr := h.graphClient.CreateFileInFolder(ctx, refreshToken, graphFolder, name, content)
	elapsed := time.Since(start)
	if nextRefresh != "" && nextRefresh != refreshToken {
		if perr := graphtoken.Put(ctx, h.store, h.keys, user, nextRefresh, time.Now()); perr != nil {
			// Best-effort like putAudit: the create itself already
			// succeeded or failed on its own terms, and the old refresh
			// token generally still works until Microsoft expires it.
			h.log.Error("graph: could not persist a rotated refresh token", "user", user, "err", perr)
		}
	}
	if createErr != nil {
		h.log.Info("graph: create file failed", "user", user, "err", createErr)
		rec := diag.New(diag.SeverityError, "graph-create-failed", Source, createErr.Error())
		h.putAudit(store.AuditRow{UserID: user, Connector: c.Slug(), Action: "create",
			OK: false, Code: rec.Code, DurationMs: elapsed.Milliseconds(), Name: name, Sheets: sheets, FileBytes: int64(len(content)), Format: "xlsx"})
		return DriveItem{}, rec
	}
	out := DriveItem{ID: item.ID, Name: item.Name, WebURL: item.WebURL, DriveID: item.DriveID(), Folder: item.Folder()}
	// The audit line (§5, §12): who created what file, how big, how many
	// sheets — never the content or sheet names. Document is the drive-item id
	// so a row is traceable to the OneDrive file it made.
	h.putAudit(store.AuditRow{UserID: user, Connector: c.Slug(), Document: out.ID, Action: "create",
		OK: true, DurationMs: elapsed.Milliseconds(), Name: name, Sheets: sheets, FileBytes: int64(len(content)), Format: "xlsx"})
	// The correlation spike's observability (RFC §3.3, §6): both what Graph
	// actually returned and (by the caller logging doc_key alongside this)
	// what create_workbook constructed from it, so a mismatch is visible
	// without re-running the live test.
	h.log.Info("graph: created file", "user", user, "drive_item_id", out.ID, "web_url", out.WebURL, "drive_id", out.DriveID, "folder", out.Folder)
	return out, nil
}

func exportErrorCode(e *protocol.ScriptError) string {
	if e == nil {
		return "export-failed"
	}
	if e.Code != "" {
		return e.Code
	}
	return e.Name
}

// exportError turns the bridge's failure result into a diagnostic record.
// Connector-specific error shaping (Office.js codes, etc.) happens in the
// connector's own tool, same as execute_script's scriptError; this is the
// generic fallback for the hub's own Export path.
func exportError(e *protocol.ScriptError) *diag.Record {
	if e == nil {
		return diag.New(diag.SeverityError, "export-failed", Source, "the export failed without an error object")
	}
	code := exportErrorCode(e)
	detail := map[string]any{"name": e.Name}
	if len(e.DebugInfo) > 0 {
		detail["debug_info"] = e.DebugInfo
	}
	if e.Stack != "" {
		detail["stack"] = e.Stack
	}
	return diag.New(diag.SeverityError, code, Source, e.Message).WithDetail(detail)
}

// boundScript returns the first auditScriptBound bytes of s, so a reviewer
// can see what ran without the row carrying an unbounded blob (§12).
func boundScript(s string) string {
	if len(s) <= auditScriptBound {
		return s
	}
	return s[:auditScriptBound]
}

// randomAuditID mints an audit row id; same shape as the other hub-minted
// ids (randomImportID).
func randomAuditID() (string, error) {
	b := make([]byte, 8)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return "aud_" + hex.EncodeToString(b), nil
}

// putAudit writes one audit row (§11, §12, §13's compensating control).
// Best-effort and synchronous: it runs on its own short-lived context (not
// the caller's, which may already be near its deadline after a long exec)
// so a slow-to-cancel request doesn't also lose its audit row, but a
// Firestore failure is only logged — at Error, with the stable
// "audit-write-failed" code an alert can key on — and never turned into a
// failure of the user's operation. h.store is nil in tests/hubs that never
// configure one; that is not a failure either.
func (h *Host) putAudit(row store.AuditRow) {
	if h.store == nil {
		return
	}
	if row.ID == "" {
		id, err := randomAuditID()
		if err != nil {
			h.log.Error("audit-write-failed", "code", "audit-write-failed", "user", row.UserID, "connector", row.Connector, "action", row.Action, "err", err)
			return
		}
		row.ID = id
	}
	if row.Timestamp.IsZero() {
		row.Timestamp = time.Now()
	}
	if row.ExpiresAt.IsZero() {
		row.ExpiresAt = row.Timestamp.Add(AuditRetention)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := h.store.PutAuditRow(ctx, row); err != nil {
		h.log.Error("audit-write-failed", "code", "audit-write-failed", "user", row.UserID, "connector", row.Connector, "action", row.Action, "err", err)
	}
}

func execError(err error, b *registry.Bridge, timeout time.Duration) *diag.Record {
	switch {
	case errors.Is(err, bridge.ErrTimeout):
		return diag.New(diag.SeverityError, "timeout", Source,
			fmt.Sprintf("bridge %s did not reply within %s; a cancel was sent but the host may be unable to interrupt the script", b.InstanceID, timeout)).
			WithDetail(map[string]any{"timeout_ms": timeout.Milliseconds()}).
			WithRemedy("if the script was legitimately long, retry with a larger timeout_ms",
				"if the host is hung, reload its task pane; a late reply from this run will be discarded")
	case errors.Is(err, bridge.ErrBridgeGone):
		return diag.New(diag.SeverityError, "bridge-reconnected", Source,
			fmt.Sprintf("bridge %s disconnected or reconnected while the script was running; its outcome is unknown", b.InstanceID)).
			WithRemedy("call list_instances, inspect the document state, and re-run only if the script is safe to repeat")
	case errors.Is(err, context.Canceled):
		return diag.New(diag.SeverityError, "cancelled", Source, "the MCP request was cancelled before the script replied")
	default:
		return diag.New(diag.SeverityError, "bridge-send-failed", Source, err.Error())
	}
}
