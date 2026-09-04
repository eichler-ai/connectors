package howto

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"
)

// This file is the broker-side half of submit_howto (seed plan §4b): build a
// schema-valid document from what an agent hands in, write it to the user's
// LOCAL corpus first, stamp it if the exact script just ran successfully in
// this session, and -- only with confirmation -- scrub it and prepare the
// review-queue hand-off (an outbox file and a prefilled issue URL). The
// broker never files the issue and holds no token; the agent's own `gh` does.

// Submission is what the tool receives. For an improvement to an existing
// how-to, ID names it and only the changed fields need to be set.
type Submission struct {
	ID         string
	Title      string
	Task       string
	Script     string
	Members    []string
	Pitfalls   []Pitfall
	Queries    *Queries
	Tags       []string
	ChangeNote string
	CreditAs   string
}

// Env is what the broker knows that the document needs.
type Env struct {
	// LocalDir is the local corpus directory (documents as <id>.json, the
	// session sidecar as verified.jsonl). OutboxDir holds prepared
	// submissions; empty means a sibling "outbox" directory of LocalDir.
	LocalDir  string
	OutboxDir string
	// Bases are the corpora an edit's target is looked up in, local first.
	Bases []*Corpus
	// RevitVersion and ConnectorVersion label a session stamp.
	RevitVersion, ConnectorVersion string
	// SessionSucceeded reports whether this session ran exactly this script
	// text successfully, and when.
	SessionSucceeded func(scriptSHA256 string) (time.Time, bool)
	// Scrub inputs: names that must never leave the machine.
	DocumentTitles []string
	Hostname       string
	Username       string
	// RepoSlug for the prefilled issue URL, e.g. eichler-ai/connectors.
	RepoSlug string
	Now      func() time.Time
}

// Bounds.
const (
	// MaxLocalDocuments bounds the local corpus directory (seed plan §5).
	MaxLocalDocuments = 2_000
	// SessionSidecarName is the local sidecar file.
	SessionSidecarName = "verified.jsonl"
	// IssueTemplate is the Issue Form that applies the queue label for any author.
	IssueTemplate = "howto-submission.yml"
	// MaxStampConnectorVersionLen mirrors connector_version.maxLength in
	// schema/howto-verification-schema.json. The broker's full version line
	// ("v0.1.2 (revision abc1234 committed ...)") is longer than this, so
	// Save cuts the label to fit rather than let a label cost a stamp.
	MaxStampConnectorVersionLen = 40
)

// Saved is the outcome of the non-gated half.
type Saved struct {
	Doc       *Document
	LocalPath string
	// Stamp is the session verification written to the local sidecar, or nil
	// when the exact script did not run successfully in this session.
	Stamp *Stamp
	// Replaced is set when the local file already held this id.
	Replaced bool
	// ScriptChanged is set on an edit whose script differs from the base's.
	ScriptChanged bool
}

// Save builds the document and writes it to the local corpus. It never
// touches the network. The document is validated through the same path the
// corpus loader uses, so the tool cannot write a file the loader would skip.
func Save(env Env, sub Submission) (*Saved, error) {
	if env.Now == nil {
		env.Now = time.Now
	}
	now := env.Now().UTC().Truncate(time.Second)
	var d *Document
	if sub.ID != "" {
		base := findBase(env.Bases, sub.ID)
		if base == nil {
			return nil, &ValidationError{What: "submission", Problems: []string{fmt.Sprintf("no how-to %q exists to improve; omit id to submit a new one", sub.ID)}}
		}
		if sub.ChangeNote == "" {
			return nil, &ValidationError{What: "submission", Problems: []string{"change_note is required when improving an existing how-to"}}
		}
		d = nextRevision(base, sub, now)
	} else {
		d = newDocument(sub, now)
		if err := ensureUniqueID(d, env); err != nil {
			return nil, err
		}
	}
	if sub.CreditAs != "" {
		role := RoleAuthor
		if d.Rev > 1 {
			role = RoleContributor
		}
		at := now
		d.Contributors = append(d.Contributors, Contributor{Handle: sub.CreditAs, Role: role, Rev: d.Rev, At: &at})
	}
	raw, err := MarshalDocument(d)
	if err != nil {
		return nil, err
	}
	if _, err := ValidateDocument(raw); err != nil {
		return nil, err
	}
	if err := os.MkdirAll(env.LocalDir, 0o755); err != nil {
		return nil, fmt.Errorf("howto: creating local corpus dir: %w", err)
	}
	if n, err := countLocalDocuments(env.LocalDir); err == nil && n >= MaxLocalDocuments {
		return nil, fmt.Errorf("howto: local corpus at %s holds %d documents, the %d bound; remove some before adding more", env.LocalDir, n, MaxLocalDocuments)
	}
	path := filepath.Join(env.LocalDir, d.ID+".json")
	_, statErr := os.Stat(path)
	pretty, _ := json.MarshalIndent(json.RawMessage(raw), "", "  ")
	if err := writeAtomic(path, append(pretty, '\n')); err != nil {
		return nil, err
	}
	saved := &Saved{Doc: d, LocalPath: path, Replaced: statErr == nil, ScriptChanged: sub.ID != "" && sub.Script != "" && sub.Script != findBaseScript(env.Bases, sub.ID)}
	if env.SessionSucceeded != nil && d.Script != "" {
		if at, ok := env.SessionSucceeded(ScriptSHA256(d.Script)); ok {
			st := Stamp{ID: d.ID, Rev: d.Rev, ScriptSHA256: ScriptSHA256(d.Script), RevitVersion: env.RevitVersion,
				Status: StampPassed, At: at.UTC().Truncate(time.Second), By: BySession, ConnectorVersion: env.ConnectorVersion}
			if env.RevitVersion == "" {
				st.RevitVersion = "0000"
			}
			// The label is the broker's version line, which is longer than the
			// schema's connector_version bound; a label must never cost the
			// submitter the stamp the run earned.
			if len(st.ConnectorVersion) > MaxStampConnectorVersionLen {
				st.ConnectorVersion = st.ConnectorVersion[:MaxStampConnectorVersionLen]
			}
			raw, err := json.Marshal(st)
			if err != nil {
				return nil, fmt.Errorf("howto: encoding the session stamp: %w", err)
			}
			// Every field here is the broker's own; a stamp that fails its own
			// schema is a bug, and it used to be a silent one -- the stamp was
			// dropped and the tool blamed the submitter for not running the
			// script (howto-script-not-run-this-session). Say so instead.
			if _, err := ValidateStamp(raw); err != nil {
				return nil, fmt.Errorf("howto: the session stamp the broker built fails its own schema (a broker bug, not a submission problem): %w", err)
			}
			if err := appendStamp(filepath.Join(env.LocalDir, SessionSidecarName), st, raw); err != nil {
				return nil, fmt.Errorf("howto: writing the session stamp: %w", err)
			}
			saved.Stamp = &st
		}
	}
	return saved, nil
}

func findBase(bases []*Corpus, id string) *Document {
	for _, c := range bases {
		if c == nil {
			continue
		}
		if d, _, ok := c.Get(id); ok {
			return d
		}
	}
	return nil
}

func newDocument(sub Submission, now time.Time) *Document {
	kind := KindHowTo
	if strings.TrimSpace(sub.Script) == "" {
		kind = KindPitfall
	}
	d := &Document{
		SchemaVersion: SchemaVersion,
		ID:            Slug(sub.Title),
		Rev:           1,
		Kind:          kind,
		Title:         strings.TrimSpace(sub.Title),
		Task:          strings.TrimSpace(sub.Task),
		Members:       dedupe(sub.Members),
		Script:        sub.Script,
		Pitfalls:      sub.Pitfalls,
		Queries:       sub.Queries,
		Tags:          dedupe(sub.Tags),
		Provenance:    Provenance{Kind: ProvenanceLocal},
		CreatedAt:     now,
		UpdatedAt:     now,
	}
	if d.Script != "" {
		d.ScriptLang = "csharp-script"
	}
	if d.Members == nil {
		d.Members = []string{}
	}
	return d
}

// nextRevision overlays the changed fields on the base and bumps rev.
// queries and pitfalls are MERGED (appended, de-duplicated by text) because
// they are evidence, not prose (seed plan §4b).
func nextRevision(base *Document, sub Submission, now time.Time) *Document {
	d := *base
	d.Rev = base.Rev + 1
	d.UpdatedAt = now
	d.Provenance = Provenance{Kind: ProvenanceLocal}
	d.Contributors = append([]Contributor(nil), base.Contributors...)
	d.Absorbs = nil
	if t := strings.TrimSpace(sub.Title); t != "" {
		d.Title = t
	}
	if t := strings.TrimSpace(sub.Task); t != "" {
		d.Task = t
	}
	if sub.Script != "" {
		d.Script = sub.Script
		d.ScriptLang = "csharp-script"
		if d.Kind == KindPitfall {
			d.Kind = KindHowTo
		}
	}
	if len(sub.Members) > 0 {
		d.Members = dedupe(sub.Members)
	}
	if len(sub.Tags) > 0 {
		d.Tags = dedupe(append(append([]string(nil), base.Tags...), sub.Tags...))
	}
	if len(sub.Pitfalls) > 0 {
		seen := map[string]bool{}
		for _, p := range base.Pitfalls {
			seen[strings.TrimSpace(p.Symptom)] = true
		}
		d.Pitfalls = append([]Pitfall(nil), base.Pitfalls...)
		for _, p := range sub.Pitfalls {
			if !seen[strings.TrimSpace(p.Symptom)] {
				d.Pitfalls = append(d.Pitfalls, p)
				seen[strings.TrimSpace(p.Symptom)] = true
			}
		}
	}
	if sub.Queries != nil {
		merged := Queries{}
		if base.Queries != nil {
			merged.Hit = append(merged.Hit, base.Queries.Hit...)
			merged.Miss = append(merged.Miss, base.Queries.Miss...)
		}
		merged.Hit = mergeQueries(merged.Hit, sub.Queries.Hit)
		merged.Miss = mergeQueries(merged.Miss, sub.Queries.Miss)
		d.Queries = &merged
	}
	return &d
}

func mergeQueries(have, add []Query) []Query {
	seen := map[string]bool{}
	for _, q := range have {
		seen[strings.ToLower(strings.TrimSpace(q.Text))] = true
	}
	for _, q := range add {
		k := strings.ToLower(strings.TrimSpace(q.Text))
		if !seen[k] {
			have = append(have, q)
			seen[k] = true
		}
	}
	return have
}

func dedupe(in []string) []string {
	if in == nil {
		return nil
	}
	seen := map[string]bool{}
	out := []string{}
	for _, s := range in {
		s = strings.TrimSpace(s)
		if s == "" || seen[s] {
			continue
		}
		seen[s] = true
		out = append(out, s)
	}
	return out
}

var nonSlug = regexp.MustCompile(`[^a-z0-9]+`)

// Slug derives a lineage id from a title: lower-case, kebab, 3-80 chars.
func Slug(title string) string {
	s := strings.ToLower(strings.TrimSpace(title))
	s = nonSlug.ReplaceAllString(s, "-")
	s = strings.Trim(s, "-")
	if len(s) > 80 {
		s = strings.TrimRight(s[:80], "-")
	}
	if len(s) < 3 {
		sum := ScriptSHA256(title)
		s = "howto-" + sum[:8]
	}
	return s
}

// ensureUniqueID suffixes -2, -3 … when the slug is taken in any base corpus
// or already on disk locally (seed plan §3e guideline 1).
func ensureUniqueID(d *Document, env Env) error {
	taken := func(id string) bool {
		// The same submission sent again -- the gate's own remedy is "call
		// submit_howto again with the same fields and confirm_submission:
		// true" -- replaces its earlier local save rather than minting
		// <id>-2 beside it (found by the step-6 batch verifier). Same title
		// and script is the test; anything else is a different document.
		// Checked before the bases, which include the local corpus itself.
		if raw, err := os.ReadFile(filepath.Join(env.LocalDir, id+".json")); err == nil {
			if prev, perr := ValidateDocument(raw); perr == nil && prev.Title == d.Title && prev.Script == d.Script {
				return false
			}
			return true
		}
		return findBase(env.Bases, id) != nil
	}
	if !taken(d.ID) {
		return nil
	}
	base := d.ID
	for i := 2; i < 1000; i++ {
		cand := fmt.Sprintf("%s-%d", base, i)
		if len(cand) > 80 {
			cand = fmt.Sprintf("%s-%d", strings.TrimRight(base[:80-len(fmt.Sprint(i))-1], "-"), i)
		}
		if !taken(cand) {
			d.ID = cand
			return nil
		}
	}
	return fmt.Errorf("howto: could not find a free id for %q", base)
}

func countLocalDocuments(dir string) (int, error) {
	m, err := filepath.Glob(filepath.Join(dir, "*.json"))
	return len(m), err
}

func writeAtomic(path string, b []byte) error {
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

// --- scrub ---------------------------------------------------------------------

// Residue names a private pattern still present after scrubbing: the
// submission is refused rather than sent.
type Residue struct {
	Field string
	Line  int
	Kind  string
}

// ErrUnscrubbed is returned by Prepare when a private pattern survives.
type ErrUnscrubbed struct{ Residue []Residue }

func (e *ErrUnscrubbed) Error() string {
	parts := make([]string, len(e.Residue))
	for i, r := range e.Residue {
		parts[i] = fmt.Sprintf("%s line %d (%s)", r.Field, r.Line, r.Kind)
	}
	return "howto: submission still contains private data after scrubbing: " + strings.Join(parts, ", ")
}

// pathTail is the rest of a path once its anchor matched: anything up to a
// quote, a line end, or a delimiter that cannot be part of a path. Spaces are
// allowed (Windows paths have them) -- over-scrubbing the tail of a sentence
// is the safe side of that trade.
const pathTail = `[^"'\n\r;,)\]]*`

// scrubPatterns run in order; each replaces what it matches. The Windows
// drive pattern runs before UNC so a C# literal's escaped backslashes
// ("C:\\Projects\\x") are consumed as one path rather than split into a
// drive and a bogus UNC share.
var scrubPatterns = []struct {
	kind string
	re   *regexp.Regexp
	repl string
}{
	{"windows-path", regexp.MustCompile(`\b[A-Za-z]:(?:\\|/)` + pathTail), "<path>"},
	{"unc-path", regexp.MustCompile(`(?:^|[^A-Za-z0-9:\\])((?:\\\\){1,2}(?:\?(?:\\\\){1,2})?[A-Za-z0-9._$-]+\\` + pathTail + `)`), "<path>"},
	{"file-url", regexp.MustCompile(`file://` + pathTail), "<path>"},
	{"posix-path", regexp.MustCompile(`(?:^|[^A-Za-z0-9])(/(?:Users|home|tmp|var|private|Volumes|mnt|opt)/` + pathTail + `)`), "<path>"},
	{"home-path", regexp.MustCompile(`(?:^|[^A-Za-z0-9])(~/` + pathTail + `)`), "<path>"},
	{"env-path", regexp.MustCompile(`%[A-Za-z_]+%(?:\\|/)` + pathTail), "<path>"},
	{"email", regexp.MustCompile(`[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}`), "<email>"},
	{"ipv4", regexp.MustCompile(`\b(?:\d{1,3}\.){3}\d{1,3}\b`), "<host>"},
	{"ipv6", regexp.MustCompile(`\b(?:[0-9A-Fa-f]{1,4}:){2,7}[0-9A-Fa-f]{1,4}\b`), "<host>"},
}

// residueDetectors are a SEPARATE, broader set run after scrubbing. They look
// for the shapes the replacements should have removed, plus ones no
// replacement handles (a project file name on its own), so a leak the
// patterns above missed is refused rather than sent.
var residueDetectors = []struct {
	kind string
	re   *regexp.Regexp
}{
	{"drive-path", regexp.MustCompile(`\b[A-Za-z]:[\\/]`)},
	{"unc-path", regexp.MustCompile(`\\\\[A-Za-z0-9._$?-]+\\`)},
	{"posix-path", regexp.MustCompile(`/(?:Users|home|Volumes)/`)},
	{"email", regexp.MustCompile(`[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}`)},
	{"ipv4", regexp.MustCompile(`\b(?:\d{1,3}\.){3}\d{1,3}\b`)},
	{"project-file", regexp.MustCompile(`(?i)[A-Za-z0-9 _()-]+\.rvt\b`)},
}

// Scrub rewrites private patterns in every text field, the script included
// (comments and string literals are where project names get typed), and
// returns the scrubbed copy plus any residue the detectors still find.
// Document titles, the machine name and the user name are removed as whole
// words (case-insensitive) before the generic patterns run; names shorter
// than four characters are skipped because a boundary match on "max" or
// "ann" would rewrite ordinary identifiers.
func Scrub(d *Document, env Env) (*Document, []Residue) {
	out := *d
	type name struct {
		re   *regexp.Regexp
		repl string
	}
	var names []name
	add := func(word, repl string) {
		word = strings.TrimSpace(word)
		// Four-letter names collide with API identifiers (a document titled
		// "Wall" would eat Wall.Create), so only longer names are scrubbed.
		if len(word) < 5 {
			return
		}
		names = append(names, name{regexp.MustCompile(`(?i)\b` + regexp.QuoteMeta(word) + `\b`), repl})
	}
	titles := append([]string(nil), env.DocumentTitles...)
	sort.Slice(titles, func(i, j int) bool { return len(titles[i]) > len(titles[j]) })
	for _, t := range titles {
		add(t, "<document>")
	}
	add(env.Hostname, "<host>")
	add(env.Username, "<user>")
	var residue []Residue
	scrub := func(field, text string) string {
		if text == "" {
			return text
		}
		for _, n := range names {
			text = n.re.ReplaceAllString(text, n.repl)
		}
		for _, p := range scrubPatterns {
			re, repl := p.re, p.repl
			if re.NumSubexp() == 1 {
				// patterns with a leading-context group keep that context
				text = re.ReplaceAllStringFunc(text, func(m string) string {
					sub := re.FindStringSubmatchIndex(m)
					return m[:sub[2]] + repl
				})
			} else {
				text = re.ReplaceAllString(text, repl)
			}
		}
		for _, det := range residueDetectors {
			if loc := det.re.FindStringIndex(text); loc != nil {
				residue = append(residue, Residue{Field: field, Line: 1 + strings.Count(text[:loc[0]], "\n"), Kind: det.kind})
			}
		}
		return text
	}
	out.Title = scrub("title", d.Title)
	out.Task = scrub("task", d.Task)
	out.Script = scrub("script", d.Script)
	out.Pitfalls = make([]Pitfall, len(d.Pitfalls))
	for i, p := range d.Pitfalls {
		out.Pitfalls[i] = Pitfall{Symptom: scrub(fmt.Sprintf("pitfalls[%d].symptom", i), p.Symptom), Cause: scrub(fmt.Sprintf("pitfalls[%d].cause", i), p.Cause),
			Fix: scrub(fmt.Sprintf("pitfalls[%d].fix", i), p.Fix), Members: p.Members}
	}
	if d.Queries != nil {
		q := Queries{}
		for i, x := range d.Queries.Hit {
			x.Text = scrub(fmt.Sprintf("queries.hit[%d].text", i), x.Text)
			q.Hit = append(q.Hit, x)
		}
		for i, x := range d.Queries.Miss {
			x.Text = scrub(fmt.Sprintf("queries.miss[%d].text", i), x.Text)
			x.Surfaced = scrub(fmt.Sprintf("queries.miss[%d].surfaced", i), x.Surfaced)
			q.Miss = append(q.Miss, x)
		}
		out.Queries = &q
	}
	// Local provenance never leaves the machine as "local": the reviewer sets
	// submission provenance on acceptance.
	out.Provenance = Provenance{Kind: ProvenanceSubmission, Ref: "pending: not yet filed; awaiting hand-off to the review queue"}
	return &out, residue
}

// FiledIssue is the result of filing a prepared submission over GitHub's
// REST API with the user's own token.
type FiledIssue struct {
	URL    string
	Number int
	// LabelsApplied is what GitHub reports back: a non-collaborator's labels
	// are dropped silently, and the reviewer needs to know the issue is not
	// yet in the queue.
	LabelsApplied []string
}

// FileIssue POSTs the prepared submission to the repository's issues API.
// The token is the USER's (opt-in, from the broker's environment); the
// broker never carries a maintainer credential. client may be nil.
func FileIssue(ctx context.Context, client *http.Client, apiBase, repoSlug, token string, prep *Prepared) (*FiledIssue, error) {
	if token == "" {
		return nil, fmt.Errorf("howto: no GitHub token configured")
	}
	if client == nil {
		client = &http.Client{Timeout: 20 * time.Second}
	}
	if apiBase == "" {
		apiBase = "https://api.github.com"
	}
	payload, _ := json.Marshal(map[string]any{"title": prep.Title, "body": prep.Body, "labels": prep.Labels})
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, apiBase+"/repos/"+repoSlug+"/issues", bytes.NewReader(payload))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", "application/vnd.github+json")
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-GitHub-Api-Version", "2022-11-28")
	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("howto: filing the issue: %w", err)
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(resp.Body, 64*1024))
	if resp.StatusCode != http.StatusCreated {
		msg := strings.TrimSpace(string(raw))
		if len(msg) > 300 {
			msg = msg[:300] + "…"
		}
		return nil, fmt.Errorf("howto: GitHub returned %d creating the issue: %s", resp.StatusCode, msg)
	}
	var created struct {
		HTMLURL string `json:"html_url"`
		Number  int    `json:"number"`
		Labels  []struct {
			Name string `json:"name"`
		} `json:"labels"`
	}
	if err := json.Unmarshal(raw, &created); err != nil {
		return nil, fmt.Errorf("howto: decoding GitHub's response: %w", err)
	}
	out := &FiledIssue{URL: created.HTMLURL, Number: created.Number}
	for _, l := range created.Labels {
		out.LabelsApplied = append(out.LabelsApplied, l.Name)
	}
	return out, nil
}

// Prepared is the review-queue hand-off.
type Prepared struct {
	Scrubbed  *Document
	OutboxDoc string // <LocalDir>/outbox/<id>.json -- the scrubbed document
	BodyPath  string // <LocalDir>/outbox/<id>.md   -- the issue body
	IssueURL  string // prefilled new-issue URL (title + template); the body is pasted from BodyPath
	GhCommand string // equivalent gh invocation, for agents that have that CLI
	Title     string // issue title, for an agent filing through its own GitHub tool
	Body      string // issue body (same text as BodyPath)
	Labels    []string
}

// Prepare scrubs the saved document and writes the outbox files. It refuses
// when a private pattern survives scrubbing.
func Prepare(env Env, saved *Saved, changeNote string) (*Prepared, error) {
	scrubbed, residue := Scrub(saved.Doc, env)
	if len(residue) > 0 {
		return nil, &ErrUnscrubbed{Residue: residue}
	}
	raw, err := MarshalDocument(scrubbed)
	if err != nil {
		return nil, err
	}
	if _, err := ValidateDocument(raw); err != nil {
		return nil, fmt.Errorf("howto: scrubbed document no longer validates: %w", err)
	}
	dir := env.OutboxDir
	if dir == "" {
		dir = filepath.Join(filepath.Dir(filepath.Clean(env.LocalDir)), "outbox")
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	pretty, _ := json.MarshalIndent(json.RawMessage(raw), "", "  ")
	docPath := filepath.Join(dir, scrubbed.ID+".json")
	if err := writeAtomic(docPath, append(pretty, '\n')); err != nil {
		return nil, err
	}
	labels := []string{"howto-submission"}
	if scrubbed.Rev > 1 {
		labels = append(labels, "howto-edit")
	}
	body := issueBody(scrubbed, saved, env, changeNote, string(pretty))
	bodyPath := filepath.Join(dir, scrubbed.ID+".md")
	if err := writeAtomic(bodyPath, []byte(body)); err != nil {
		return nil, err
	}
	title := scrubbed.Title
	if scrubbed.Rev > 1 {
		title = fmt.Sprintf("%s (rev %d)", scrubbed.Title, scrubbed.Rev)
	}
	q := url.Values{}
	q.Set("template", IssueTemplate)
	q.Set("title", title)
	issueURL := fmt.Sprintf("https://github.com/%s/issues/new?%s", env.RepoSlug, q.Encode())
	// gh refuses --template together with --body-file, so the command carries
	// the labels itself. GitHub drops labels from a non-collaborator's
	// request (seed plan §4e); the web URL keeps the template, which applies
	// the label for any author.
	gh := fmt.Sprintf("gh issue create --repo %s --title %q --body-file %q --label %s", env.RepoSlug, title, bodyPath, strings.Join(labels, ","))
	return &Prepared{Scrubbed: scrubbed, OutboxDoc: docPath, BodyPath: bodyPath, IssueURL: issueURL, GhCommand: gh, Labels: labels, Title: title, Body: body}, nil
}

func issueBody(d *Document, saved *Saved, env Env, changeNote, prettyJSON string) string {
	var b strings.Builder
	if changeNote != "" {
		fmt.Fprintf(&b, "**Change:** %s\n\n", changeNote)
	}
	fmt.Fprintf(&b, "**How-to:** `%s` rev %d — %s\n\n", d.ID, d.Rev, d.Title)
	fmt.Fprintf(&b, "**Task:** %s\n\n", d.Task)
	verified := "not run in the submitting session"
	if saved.Stamp != nil {
		verified = fmt.Sprintf("script ran successfully in the submitting session on Revit %s (connector %s, session stamp — weaker than a harness run)", saved.Stamp.RevitVersion, saved.Stamp.ConnectorVersion)
	}
	fmt.Fprintf(&b, "**Verification:** %s\n\n", verified)
	b.WriteString("**Reviewer checklist** (`/triage-howto-submission`): schema valid · scrubbed (read every field) · script ran on a disposable fixture · not a duplicate · prose edited to the §3e guidelines\n\n")
	b.WriteString("```json\n")
	b.WriteString(prettyJSON)
	b.WriteString("\n```\n")
	return b.String()
}

func findBaseScript(bases []*Corpus, id string) string {
	if d := findBase(bases, id); d != nil {
		return d.Script
	}
	return ""
}

// appendStamp appends one line to the local sidecar unless an identical
// stamp (id, rev, hash, version, by) is already there, and refuses beyond
// MaxStamps lines so a re-saving session cannot grow the file without bound.
func appendStamp(path string, st Stamp, raw []byte) error {
	if f, err := os.Open(path); err == nil {
		side, lerr := LoadSidecar(f)
		f.Close()
		if lerr == nil {
			if len(side.Stamps) >= MaxStamps {
				return fmt.Errorf("session sidecar %s holds %d stamps, the bound", path, MaxStamps)
			}
			for _, have := range side.Stamps {
				if have.ID == st.ID && have.Rev == st.Rev && have.ScriptSHA256 == st.ScriptSHA256 && have.RevitVersion == st.RevitVersion && have.By == st.By {
					return nil
				}
			}
		}
	}
	f, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	defer f.Close()
	_, err = f.Write(append(raw, '\n'))
	return err
}
