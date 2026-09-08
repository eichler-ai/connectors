package hub

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net/url"
	"time"

	"github.com/eichler-ai/connectors/hub/diag"
	"github.com/eichler-ai/connectors/hub/internal/bridge"
	"github.com/eichler-ai/connectors/hub/internal/fetch"
	"github.com/eichler-ai/connectors/hub/protocol"
)

const (
	// maxImportBytes bounds a decoded/fetched xlsx before it ever reaches the
	// file store or the bridge (PRD §10 reversed: "Bounded (reject over
	// ~10 MiB decoded)"). Generous over anything a chat-composed workbook
	// needs; far under maxUploadBytes, which sizes a whole-document export
	// (a rendered PDF can legitimately be much larger than an xlsx a caller
	// hands in).
	maxImportBytes = 10 << 20
	// ImportURLTTL is how long the signed URL for a staged import stays
	// valid — the same figure as ExportURLTTL (§11): long enough for a slow
	// pane fetch, short enough that a leaked link is not useful for long.
	ImportURLTTL = ExportURLTTL
)

// xlsxMagic is the ZIP local-file-header signature every xlsx (a ZIP
// container) starts with. Checked before anything is stored or handed to a
// pane — POC finding (2026-09-08): insertWorksheetsFromBase64 rejects a
// malformed/minimal xlsx with InvalidArgument, so a clear diagnostic here
// beats a confusing one from Office.js three hops later.
var xlsxMagic = []byte("PK\x03\x04")

// ImportSource is exactly one of the two ways a caller gets bytes to the hub
// (PRD §10 reversed): inline, for the common case where the agent already
// has or just produced a small file, or a URL the hub fetches server-side
// (SSRF-guarded, §11's "reuse the CIMD guard's private-range refusal").
type ImportSource struct {
	ContentBase64 string
	SourceURL     string
}

// resolve is the "get bytes into the hub" step: decode or fetch, bounded,
// then validated as a ZIP container. Shared by every connector's
// import-shaped tool, so a second connector that grows one gets the same
// bound and the same SSRF guard for free.
func (s ImportSource) resolve(ctx context.Context) ([]byte, *diag.Record) {
	switch {
	case s.ContentBase64 != "" && s.SourceURL != "":
		return nil, diag.New(diag.SeverityError, "invalid-source", Source, "content_base64 and source_url are mutually exclusive; pass exactly one")
	case s.ContentBase64 != "":
		// A generous pre-decode estimate (base64 is ~4/3 the size of its
		// decoded bytes) catches an absurd payload before spending the decode
		// on it; the real cap is enforced on the decoded length below.
		if len(s.ContentBase64) > maxImportBytes*4/3+4 {
			return nil, diag.New(diag.SeverityError, "too-large", Source, fmt.Sprintf("content_base64 decodes to more than the %d-byte limit", maxImportBytes))
		}
		b, err := base64.StdEncoding.DecodeString(s.ContentBase64)
		if err != nil {
			return nil, diag.New(diag.SeverityError, "invalid-source", Source, "content_base64 is not valid base64: "+err.Error())
		}
		if len(b) > maxImportBytes {
			return nil, diag.New(diag.SeverityError, "too-large", Source, fmt.Sprintf("content_base64 decodes to %d bytes, over the %d-byte limit", len(b), maxImportBytes))
		}
		return validateXLSX(b)
	case s.SourceURL != "":
		if u, err := url.Parse(s.SourceURL); err != nil || u.Scheme != "https" || u.Host == "" {
			return nil, diag.New(diag.SeverityError, "invalid-source", Source, "source_url must be an https URL")
		}
		b, err := fetch.Bytes(ctx, s.SourceURL, maxImportBytes)
		if err != nil {
			return nil, diag.New(diag.SeverityError, "fetch-failed", Source, "could not fetch source_url: "+err.Error()).
				WithRemedy("check the URL is public, reachable and under the size limit", "or pass content_base64 instead")
		}
		return validateXLSX(b)
	default:
		return nil, diag.New(diag.SeverityError, "invalid-source", Source, "exactly one of content_base64 or source_url is required")
	}
}

func validateXLSX(b []byte) ([]byte, *diag.Record) {
	if len(b) < len(xlsxMagic) || string(b[:len(xlsxMagic)]) != string(xlsxMagic) {
		return nil, diag.New(diag.SeverityError, "not-an-xlsx", Source, "the file is not a well-formed .xlsx (it does not start with the ZIP signature)").
			WithRemedy("pass a genuine .xlsx produced by a real writer, not a hand-assembled file")
	}
	return b, nil
}

// ImportRequest is what a connector asks the hub to insert into the open
// workbook (PRD §10/§11, reversed): the source bytes, the target document,
// and the Office insert options.
type ImportRequest struct {
	Source     ImportSource
	Options    protocol.ImportOptions
	DocumentID string
	Timeout    time.Duration
}

// ImportResult is a completed import: which sheets landed and the full sheet
// list afterward, so the caller is never guessing a name-collision's
// resulting name (§10 reversed: Office renames the incoming sheet on a
// collision).
type ImportResult struct {
	Instance    Instance
	Document    protocol.Document
	Bytes       int64
	AddedSheets []string
	AllSheets   []string
}

// importPayload is the JSON the pane's `result` carries for a successful
// import; unwrapped from protocol.Result.Result.
type importPayload struct {
	AddedSheets []string `json:"added_sheets"`
	AllSheets   []string `json:"all_sheets"`
}

// Import gets the source bytes into the file store, mints a signed URL,
// asks the connector's bridge to fetch and insert it, and waits for the
// pane's outcome (PRD §10/§11, reversed — the mirror image of Export).
// Files must be configured, exactly as Export requires.
func (h *Host) Import(ctx context.Context, user string, c Connector, target Target, req ImportRequest) (ImportResult, *diag.Record) {
	if h.files == nil {
		return ImportResult{}, diag.New(diag.SeverityError, "files-unconfigured", Source, "the hub has no file store configured; import is unavailable")
	}
	timeout := req.Timeout
	if timeout <= 0 {
		timeout = h.defaultTimeout
	}
	if timeout > h.maxTimeout {
		return ImportResult{}, diag.New(diag.SeverityError, "invalid-timeout", Source,
			fmt.Sprintf("timeout %s exceeds the maximum of %s", timeout, h.maxTimeout))
	}
	b, rec := h.resolve(user, c.Slug(), target)
	if rec != nil {
		return ImportResult{}, rec
	}
	doc, found := b.Document(target.DocumentID)
	if target.DocumentID != "" && !found {
		return ImportResult{}, diag.New(diag.SeverityError, "unknown-document", Source,
			fmt.Sprintf("bridge %s has no document with id %q", b.InstanceID, target.DocumentID)).
			WithRemedy("call list_instances for the current document ids")
	}

	// Fail fast on a bad source before ever touching the file store or the
	// bridge — an expect-mismatch-shaped safety on the input, not just the
	// tool's own expect.workbook check above it.
	content, rec := req.Source.resolve(ctx)
	if rec != nil {
		return ImportResult{}, rec
	}

	id, err := randomImportID()
	if err != nil {
		return ImportResult{}, diag.New(diag.SeverityError, "internal", Source, "could not generate an id: "+err.Error())
	}
	ref, err := h.files.Put(ctx, user, c.Slug(), id, "xlsx", bytes.NewReader(content))
	if err != nil {
		h.log.Error("import: put failed", "user", user, "connector", c.Slug(), "id", id, "err", err)
		return ImportResult{}, diag.New(diag.SeverityError, "internal", Source, "could not stage the file: "+err.Error())
	}
	signedURL, _, err := h.files.SignedURL(ctx, ref, ImportURLTTL)
	if err != nil {
		h.log.Error("import: sign failed", "user", user, "connector", c.Slug(), "id", id, "err", err)
		return ImportResult{}, diag.New(diag.SeverityError, "internal", Source, "could not mint a download link: "+err.Error())
	}

	start := time.Now()
	res, err := h.bridges.Import(ctx, b, bridge.ImportRequest{DocumentID: target.DocumentID, URL: signedURL, Options: req.Options, Timeout: timeout})
	attrs := []any{"user", user, "connector", c.Slug(), "instance_id", b.InstanceID, "document", doc.ID,
		"bytes", ref.Bytes, "elapsed", time.Since(start).Round(time.Millisecond)}
	if err != nil {
		h.log.Info("import: failed", append(attrs, "err", err)...)
		return ImportResult{}, execError(err, b, timeout)
	}
	if !res.OK {
		h.log.Info("import: failed", append(attrs, "code", exportErrorCode(res.Error))...)
		return ImportResult{}, exportError(res.Error)
	}
	var payload importPayload
	if err := json.Unmarshal(res.Result, &payload); err != nil {
		rec := diag.New(diag.SeverityError, "bad-import-result", Source, "the pane's outcome was not the expected shape: "+err.Error())
		h.log.Info("import: failed", append(attrs, "code", rec.Code)...)
		return ImportResult{}, rec
	}
	// The audit line (§12): identities, document, size, sheet count — never
	// the bytes and never the sheet names (may carry user data).
	h.log.Info("import: done", append(attrs, "added_sheets", len(payload.AddedSheets))...)
	return ImportResult{Instance: instanceOf(b), Document: doc, Bytes: ref.Bytes, AddedSheets: payload.AddedSheets, AllSheets: payload.AllSheets}, nil
}

func randomImportID() (string, error) {
	b := make([]byte, 8)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return "imp_" + hex.EncodeToString(b), nil
}
