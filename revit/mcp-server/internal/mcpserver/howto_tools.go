// submit_howto (revit/docs/howto-seed-plan.md §4b): the agent hands in a
// how-to it just learned. The broker validates it, writes it to the user's
// local corpus, stamps it if the exact script ran successfully in this
// session, and -- only with confirm_submission -- scrubs it and prepares the
// review-queue hand-off. The broker never files the issue: the agent does,
// with its own gh, under its own permission prompt.
package mcpserver

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/discovery"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/execution"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/howto"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
)

const howtoSource = "mcp-server.internal.mcpserver.howto"

// HowToDeps is what submit_howto needs from the rest of the broker.
type HowToDeps struct {
	// LocalDir is the local corpus directory (<app-data>/howto/local).
	LocalDir string
	// Registry supplies the connected instances' Revit versions and open
	// document titles (scrubbed out of every submission).
	Registry *registry.Registry
	// Router resolves instance_id the way the discovery tools do.
	Router *discovery.Router
	// Exec answers "did this exact script succeed in this session".
	Exec *execution.Manager
	// Version is the broker's version line, recorded on a session stamp.
	Version string
	// RepoSlug is the review-queue repository.
	RepoSlug string
	// Bases are corpora an edit's target may live in (the embedded seed,
	// later the shared corpus); the local corpus is loaded per call.
	Bases func() []*howto.Corpus
}

// SubmitHowToIn is the input schema for submit_howto.
type SubmitHowToIn struct {
	InstanceID        string          `json:"instance_id,omitempty" jsonschema:"the Revit instance the script ran on; omitted works when one version is connected (same rule as the discovery tools)"`
	ID                string          `json:"id,omitempty" jsonschema:"to IMPROVE an existing how-to: its id. Only the fields you pass change; pitfalls and queries are merged; the result is the next revision. Requires change_note."`
	Title             string          `json:"title,omitempty" jsonschema:"short noun phrase naming the example (8-120 chars); required for a new how-to"`
	Task              string          `json:"task,omitempty" jsonschema:"one or two plain sentences naming the element type, the operation and the key member nouns of the answer -- this is the search text; required for a new how-to"`
	Script            string          `json:"script,omitempty" jsonschema:"the complete working C# script body in the connector's dialect (reads at top level, writes inside Connector.WithTransaction). Its comments ARE the explanation: label setup as setup, number the steps. Omit for a pitfall-only document."`
	Members           []string        `json:"members,omitempty" jsonschema:"fully-qualified Namespace.Type.Member names the script calls, in call order"`
	Pitfalls          []howto.Pitfall `json:"pitfalls,omitempty" jsonschema:"one entry per mistake avoided: symptom (what the agent sees, error text as recorded), cause, fix (an instruction)"`
	Queries           *howto.Queries  `json:"queries,omitempty" jsonschema:"only phrasings this session actually sent: hit (with rank) and miss (with what surfaced instead); never invented"`
	Tags              []string        `json:"tags,omitempty" jsonschema:"facets such as walls, views, sheets, groups (lower-case kebab)"`
	ChangeNote        string          `json:"change_note,omitempty" jsonschema:"one sentence: what changed and why; required with id"`
	CreditAs          string          `json:"credit_as,omitempty" jsonschema:"opt-in credit: the user's GitHub login or chosen display name; nothing is inferred if omitted"`
	ConfirmSubmission bool            `json:"confirm_submission,omitempty" jsonschema:"false (default): only save to the user's local corpus and return the document for review. true: also scrub it and prepare the review-queue issue (an outward-facing action the user should approve)"`
}

// SubmitHowToSubmission is the review-queue hand-off, present only when
// confirm_submission was true and scrubbing succeeded.
type SubmitHowToSubmission struct {
	ScrubbedDocument *howto.Document `json:"scrubbed_document"`
	OutboxDocument   string          `json:"outbox_document"`
	IssueBodyPath    string          `json:"issue_body_path"`
	IssueURL         string          `json:"issue_url"`
	GhCommand        string          `json:"gh_command"`
	Labels           []string        `json:"labels"`
}

// SubmitHowToOut is the output schema for submit_howto.
type SubmitHowToOut struct {
	Document   *howto.Document        `json:"document,omitempty"`
	LocalPath  string                 `json:"local_path,omitempty"`
	Verified   *howto.Stamp           `json:"verified,omitempty"`
	Submission *SubmitHowToSubmission `json:"submission,omitempty"`
	Notices    []*diag.Record         `json:"notices,omitempty"`
	Guidance   string                 `json:"guidance,omitempty"`
	Error      *diag.Record           `json:"error,omitempty"`
}

// RegisterHowTo adds submit_howto to s.
func RegisterHowTo(s *mcp.Server, deps HowToDeps) {
	mcp.AddTool(s, &mcp.Tool{
		Name:        "submit_howto",
		Description: "Hand in a how-to you just learned (or improve an existing one by id): saved to the user's local how-to corpus immediately, validated against the corpus schema with every problem named, and -- with confirm_submission -- scrubbed of paths/names and prepared as a review-queue issue you then file with the returned gh command. Submit after the script ran successfully, never speculatively.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in SubmitHowToIn) (*mcp.CallToolResult, SubmitHowToOut, error) {
		out := submitHowTo(deps, in)
		if out.Error != nil {
			return errorCallToolResultFor(out), out, nil
		}
		return nil, out, nil
	})
}

func submitHowTo(deps HowToDeps, in SubmitHowToIn) SubmitHowToOut {
	env := howto.Env{
		LocalDir:         deps.LocalDir,
		ConnectorVersion: deps.Version,
		RepoSlug:         deps.RepoSlug,
		Now:              time.Now,
	}
	if env.RepoSlug == "" {
		env.RepoSlug = "eichler-ai/connectors"
	}
	if h, err := os.Hostname(); err == nil {
		env.Hostname = h
	}
	env.Username = firstNonEmpty(os.Getenv("USER"), os.Getenv("USERNAME"))

	// Instance: needed for the Revit version on a session stamp and for the
	// document titles the scrubber removes. Optional when nothing is
	// connected -- a submission can still be saved locally.
	instanceID := ""
	if deps.Router != nil {
		if id, ver, drec := deps.Router.ResolveInstance(in.InstanceID); drec == nil {
			instanceID, env.RevitVersion = id, ver
		} else if in.InstanceID != "" {
			return SubmitHowToOut{Error: drec}
		}
	}
	if deps.Registry != nil {
		for _, inst := range deps.Registry.List() {
			for _, d := range inst.Documents {
				env.DocumentTitles = append(env.DocumentTitles, d.Title)
			}
		}
	}
	if deps.Exec != nil && instanceID != "" {
		env.SessionSucceeded = func(sha string) (time.Time, bool) { return deps.Exec.SucceededRecently(instanceID, sha) }
	}
	// Bases: local corpus (for id lookups and uniqueness) first, then the
	// embedded/shared corpora.
	if local, err := howto.LoadLocalDir(deps.LocalDir); err == nil {
		env.Bases = append(env.Bases, local)
	}
	if deps.Bases != nil {
		env.Bases = append(env.Bases, deps.Bases()...)
	}

	sub := howto.Submission{ID: in.ID, Title: in.Title, Task: in.Task, Script: in.Script, Members: in.Members,
		Pitfalls: in.Pitfalls, Queries: in.Queries, Tags: in.Tags, ChangeNote: in.ChangeNote, CreditAs: in.CreditAs}
	saved, err := howto.Save(env, sub)
	if err != nil {
		var ve *howto.ValidationError
		if errors.As(err, &ve) {
			return SubmitHowToOut{Error: diag.New(diag.SeverityError, "howto-invalid", howtoSource,
				"the submission does not conform to the how-to schema: "+strings.Join(ve.Problems, "; ")).
				WithDetail(map[string]any{"problems": ve.Problems}).
				WithRemedy("fix the named fields and call submit_howto again; nothing was saved. Field rules: id kebab-case; title 8-120 chars; task 20-600 chars naming element type and operation; members fully qualified (Namespace.Type.Member); pitfalls need symptom, cause and fix; queries.hit needs rank, queries.miss needs surfaced")}
		}
		return SubmitHowToOut{Error: diag.New(diag.SeverityError, "howto-save-failed", howtoSource, err.Error()).
			WithRemedy("check the local corpus directory is writable: " + deps.LocalDir)}
	}
	out := SubmitHowToOut{Document: saved.Doc, LocalPath: saved.LocalPath, Verified: saved.Stamp}
	if saved.Replaced {
		out.Notices = append(out.Notices, diag.New(diag.SeverityInfo, "howto-local-replaced", howtoSource,
			fmt.Sprintf("local how-to %s already existed and was replaced", saved.Doc.ID)))
	}
	if saved.Stamp == nil && saved.Doc.Script != "" {
		out.Notices = append(out.Notices, diag.New(diag.SeverityInfo, "howto-script-not-run-this-session", howtoSource,
			"this exact script text has not run successfully in this session, so the document carries no verification stamp").
			WithRemedy("run the script with execute_script first, then submit the identical text"))
	}
	if !in.ConfirmSubmission {
		out.Notices = append(out.Notices, diag.New(diag.SeverityInfo, "howto-submission-confirmation-required", howtoSource,
			"saved locally only; the review-queue submission (an outward-facing action) needs confirm_submission: true").
			WithRemedy("show the user the document above; if they want it submitted, call submit_howto again with the same fields and confirm_submission: true"))
		out.Guidance = "Saved to the local how-to corpus at " + saved.LocalPath + ". Review the document with the user before submitting: it must contain no project paths, document titles, user or machine names."
		return out
	}
	prep, err := howto.Prepare(env, saved, in.ChangeNote)
	if err != nil {
		var un *howto.ErrUnscrubbed
		if errors.As(err, &un) {
			fields := make([]map[string]any, len(un.Residue))
			for i, r := range un.Residue {
				fields[i] = map[string]any{"field": r.Field, "line": r.Line, "kind": r.Kind}
			}
			out.Error = diag.New(diag.SeverityError, "howto-submission-unscrubbed", howtoSource,
				"the document still contains private data after scrubbing; it was saved locally but not prepared for submission").
				WithDetail(map[string]any{"residue": fields}).
				WithRemedy("remove the path, host or address from the named fields and resubmit with confirm_submission: true")
			return out
		}
		out.Error = diag.New(diag.SeverityError, "howto-submission-failed", howtoSource, err.Error())
		return out
	}
	out.Submission = &SubmitHowToSubmission{ScrubbedDocument: prep.Scrubbed, OutboxDocument: prep.OutboxDoc, IssueBodyPath: prep.BodyPath,
		IssueURL: prep.IssueURL, GhCommand: prep.GhCommand, Labels: prep.Labels}
	out.Guidance = "The scrubbed document is what will leave this machine -- read it. To file it, run the gh command (it asks the user's permission and uses their GitHub identity as the issue author): " + prep.GhCommand +
		" -- if gh is unavailable or the repository is not accessible to this account (it is private during development), open " + prep.IssueURL + " and paste the body from " + prep.BodyPath + ", or hand that file to a maintainer."
	return out
}

func firstNonEmpty(vals ...string) string {
	for _, v := range vals {
		if v != "" {
			return v
		}
	}
	return ""
}

// LocalCorpusDir is where submit_howto writes, under the broker's app-data
// directory. The broker indexes it (step 4), so it lives with the broker,
// not with Revit's exchange root, which in remote mode is another machine.
func LocalCorpusDir(dataDir string) string { return filepath.Join(dataDir, "howto", "local") }
