// search_howtos and describe_howto (revit/docs/howto-corpus-design.md §3-§4):
// the agent-facing read side of the how-to corpus, served from
// internal/howtosearch. Both require the caller's Revit version -- exactly
// one of instance_id (resolved through the registry) or revit_version --
// because a how-to's verification is per version and the agent must never
// be handed a script without being told whether it ran on its version.
// The version is a preference in ranking and a label on every result,
// never a filter.
package mcpserver

import (
	"context"
	"errors"
	"fmt"
	"regexp"
	"strings"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/howto"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/howtosearch"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch"
)

// Paging bounds for search_howtos. A hit is a paragraph, not a one-line
// member, so the default page is short; the cap keeps a page under the
// host's output limit even with long tasks.
const (
	defaultHowToTopN = 5
	maxHowToTopN     = 50
)

// SearchHowTosIn is the input schema for search_howtos.
type SearchHowTosIn struct {
	Query        string `json:"query" jsonschema:"REQUIRED. The task as one plain sentence naming the element type and the operation (\"create a floor from a closed loop of lines on a level\"); a symptom you hit or a member you suspect also scores."`
	InstanceID   string `json:"instance_id,omitempty" jsonschema:"the Revit instance you are driving (from list_instances); its version decides which how-tos are marked verified_here. Exactly one of instance_id / revit_version is required."`
	RevitVersion string `json:"revit_version,omitempty" jsonschema:"the Revit version to rank and label for, e.g. \"2025\"; use this INSTEAD of instance_id when working ahead of a connection. Exactly one of the two is required."`
	Cursor       string `json:"cursor,omitempty" jsonschema:"opaque pagination cursor echoed back from a prior response's next_cursor"`
	TopN         int    `json:"top_n,omitempty" jsonschema:"results per page; default 5, max 50"`
}

// HowToHit is one search_howtos result: enough to choose, not the script
// (describe_howto has that).
type HowToHit struct {
	ID      string   `json:"id"`
	Rev     int      `json:"rev"`
	Title   string   `json:"title"`
	Task    string   `json:"task"`
	Members []string `json:"members,omitempty"`
	Tags    []string `json:"tags,omitempty"`
	// VerifiedOn / FailedOn are the Revit versions with a current passing /
	// failing stamp; VerifiedHere is whether the resolved version is in
	// VerifiedOn.
	VerifiedOn   []string `json:"verified_on,omitempty"`
	FailedOn     []string `json:"failed_on,omitempty"`
	VerifiedHere bool     `json:"verified_here"`
	Score        float64  `json:"score,omitempty"`
	// Source is seed (embedded in the broker, reviewed and harness-verified)
	// or local (the user's own, unreviewed).
	Source string `json:"source"`
	// SharedRev is set when a local document shadows a seed lineage: the
	// seed's revision, so the agent can see how far it has moved.
	SharedRev int `json:"shared_rev,omitempty"`
}

// SearchHowTosOut is the output schema for search_howtos.
type SearchHowTosOut struct {
	Results      []HowToHit `json:"results,omitempty"`
	NextCursor   string     `json:"next_cursor,omitempty"`
	TotalMatched int        `json:"total_matched,omitempty"`
	// RevitVersion is the version the call resolved to (from instance_id
	// or revit_version) and ranked for.
	RevitVersion string `json:"revit_version,omitempty"`
	// Ranker is the same vocabulary as search_functions: semantic,
	// semantic-no-rerank or lexical (there is no add-in fallback here).
	Ranker   string         `json:"ranker,omitempty"`
	Guidance string         `json:"guidance,omitempty"`
	Notices  []*diag.Record `json:"notices,omitempty"`
	Error    *diag.Record   `json:"error,omitempty"`
}

// DescribeHowToIn is the input schema for describe_howto.
type DescribeHowToIn struct {
	ID           string `json:"id" jsonschema:"REQUIRED. The how-to's id from a search_howtos result."`
	InstanceID   string `json:"instance_id,omitempty" jsonschema:"the Revit instance you are driving (from list_instances); its version decides the verification reported. Exactly one of instance_id / revit_version is required."`
	RevitVersion string `json:"revit_version,omitempty" jsonschema:"the Revit version to report verification for, e.g. \"2025\"; use this INSTEAD of instance_id when working ahead of a connection. Exactly one of the two is required."`
}

// HowToView is the agent-facing document: everything the agent acts on,
// without the maintainer-facing provenance, verify block and credits.
type HowToView struct {
	ID         string          `json:"id"`
	Rev        int             `json:"rev"`
	Kind       string          `json:"kind"`
	Title      string          `json:"title"`
	Task       string          `json:"task"`
	Members    []string        `json:"members,omitempty"`
	Script     string          `json:"script,omitempty"`
	ScriptLang string          `json:"script_language,omitempty"`
	Pitfalls   []howto.Pitfall `json:"pitfalls,omitempty"`
	Tags       []string        `json:"tags,omitempty"`
	APISince   string          `json:"api_since,omitempty"`
	APIUntil   string          `json:"api_until,omitempty"`
	Absorbs    []string        `json:"absorbs,omitempty"`
	UpdatedAt  time.Time       `json:"updated_at"`
}

// HowToVerification is the winning stamp for the resolved version.
type HowToVerification struct {
	RevitVersion     string    `json:"revit_version"`
	Status           string    `json:"status"`
	By               string    `json:"by"`
	At               time.Time `json:"at"`
	ConnectorVersion string    `json:"connector_version,omitempty"`
	Diagnostic       string    `json:"diagnostic,omitempty"`
}

// DescribeHowToOut is the output schema for describe_howto.
type DescribeHowToOut struct {
	Document *HowToView `json:"document,omitempty"`
	Source   string     `json:"source,omitempty"`
	// RedirectedFrom is set when the requested id was merged into this
	// lineage (absorbs).
	RedirectedFrom string `json:"redirected_from,omitempty"`
	RevitVersion   string `json:"revit_version,omitempty"`
	VerifiedHere   bool   `json:"verified_here"`
	// Verification is the stamp for the resolved version, or nil when the
	// document was never swept on it.
	Verification *HowToVerification `json:"verification,omitempty"`
	VerifiedOn   []string           `json:"verified_on,omitempty"`
	FailedOn     []string           `json:"failed_on,omitempty"`
	// APIWarnings evaluates api_since / api_until against the resolved
	// version; declared hints, not verification.
	APIWarnings []string       `json:"api_warnings,omitempty"`
	SharedRev   int            `json:"shared_rev,omitempty"`
	Guidance    string         `json:"guidance,omitempty"`
	Notices     []*diag.Record `json:"notices,omitempty"`
	Error       *diag.Record   `json:"error,omitempty"`
}

func registerHowToSearch(s *mcp.Server, deps HowToDeps) {
	mcp.AddTool(s, &mcp.Tool{
		Name:        "search_howtos",
		Description: "Find a worked, harness-verified how-to for a Revit task before writing a script from scratch: each document is one feature or connector mechanism with a complete script, the members it uses and the pitfalls it avoids. Ranked like search_functions; documents verified on your Revit version lead and every hit says whether it was (verified_here). Requires exactly one of instance_id or revit_version. Then describe_howto for the script.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in SearchHowTosIn) (*mcp.CallToolResult, SearchHowTosOut, error) {
		out := searchHowTos(ctx, deps, in)
		if out.Error != nil {
			return errorCallToolResultFor(out), out, nil
		}
		return nil, out, nil
	})
	mcp.AddTool(s, &mcp.Tool{
		Name:        "describe_howto",
		Description: "One how-to in full -- script, members, pitfalls -- with its verification for your Revit version (stamp status, who ran it, when) and api_since/api_until warnings. Requires exactly one of instance_id or revit_version. Read the pitfalls before running the script; a local (unreviewed) document's script must be read in full first.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in DescribeHowToIn) (*mcp.CallToolResult, DescribeHowToOut, error) {
		out := describeHowTo(ctx, deps, in)
		if out.Error != nil {
			return errorCallToolResultFor(out), out, nil
		}
		return nil, out, nil
	})
}

var revitVersionRe = regexp.MustCompile(`^\d{4}$`)

// resolveHowToVersion applies the exactly-one rule and returns the version
// to rank and label for.
func resolveHowToVersion(deps HowToDeps, instanceID, revitVersion string) (string, *diag.Record) {
	remedy := "pass instance_id (from list_instances) so the answer matches the Revit you are driving, or revit_version (e.g. \"2025\") when working ahead of a connection -- one of the two, not both"
	switch {
	case instanceID == "" && revitVersion == "":
		return "", diag.New(diag.SeverityError, "howto-version-required", howtoSource,
			"how-tos are verified per Revit version, so the call must say which version it is for: neither instance_id nor revit_version was given").
			WithRemedy(remedy)
	case instanceID != "" && revitVersion != "":
		return "", diag.New(diag.SeverityError, "howto-version-required", howtoSource,
			"instance_id and revit_version were both given; the instance's own version would be used, so pass only one").
			WithDetail(map[string]any{"instance_id": instanceID, "revit_version": revitVersion}).
			WithRemedy(remedy)
	case revitVersion != "":
		if !revitVersionRe.MatchString(revitVersion) {
			return "", diag.New(diag.SeverityError, "howto-version-invalid", howtoSource,
				fmt.Sprintf("revit_version %q is not a four-digit Revit release year", revitVersion)).
				WithDetail(map[string]any{"revit_version": revitVersion}).
				WithRemedy("pass the release year as list_instances reports it, e.g. \"2025\" or \"2027\"")
		}
		return revitVersion, nil
	}
	if deps.Router == nil {
		return "", diag.New(diag.SeverityError, "no-instance-connected", howtoSource, "no Revit instance registry is available to resolve instance_id").
			WithRemedy("pass revit_version instead")
	}
	_, ver, drec := deps.Router.ResolveInstance(instanceID)
	if drec != nil {
		return "", drec
	}
	if ver == "" {
		return "", diag.New(diag.SeverityError, "instance-not-found", howtoSource, "instance "+instanceID+" reports no Revit version").
			WithRemedy("call list_instances and pass a current instance_id, or pass revit_version")
	}
	return ver, nil
}

// howToFailed maps a Service error: a load failure is a build or
// local-corpus problem (not retryable), anything else is a ranking failure
// (a model call) worth one retry.
func howToFailed(err error) *diag.Record {
	var le *howtosearch.LoadError
	if errors.As(err, &le) || err == nil {
		return howToUnavailable(err)
	}
	return diag.New(diag.SeverityError, "howto-search-failed", howtoSource, "ranking the how-to corpus failed: "+err.Error()).
		WithRemedy("retry once; if it fails again, fall back to search_functions and report it")
}

func howToUnavailable(err error) *diag.Record {
	msg := "the how-to corpus could not be loaded"
	if err != nil {
		msg += ": " + err.Error()
	}
	return diag.New(diag.SeverityError, "howto-corpus-unavailable", howtoSource, msg).
		WithRemedy("this is a broker build or local-corpus problem, not something to retry; fall back to search_functions and report it")
}

// corpusNotices reports what the corpus loader had to skip or flag, on
// every response (design note §5: never silently).
func corpusNotices(st howtosearch.Status, localDir string) []*diag.Record {
	var out []*diag.Record
	if len(st.LocalProblems) > 0 || st.LocalSkipped > 0 {
		out = append(out, diag.New(diag.SeverityWarning, "howto-local-corpus-problems", howtoSource,
			fmt.Sprintf("the local how-to corpus under %s has %d problem(s), %d file(s) or stamp(s) skipped: %s", localDir, len(st.LocalProblems), st.LocalSkipped, strings.Join(st.LocalProblems, "; "))).
			WithDetail(map[string]any{"problems": st.LocalProblems, "skipped": st.LocalSkipped}).
			WithRemedy("fix or delete the named files; the rest of the corpus is served"))
	}
	if st.LocalTruncated {
		out = append(out, diag.New(diag.SeverityWarning, "howto-local-corpus-truncated", howtoSource,
			fmt.Sprintf("the local how-to directory %s exceeds %d documents; the rest are not indexed", localDir, howto.MaxLocalDocuments)).
			WithRemedy("move documents the user no longer needs out of the directory; the first files by name are the ones served"))
	}
	if st.NewerThanBroker > 0 {
		out = append(out, diag.New(diag.SeverityInfo, "howto-corpus-newer-than-broker", howtoSource,
			fmt.Sprintf("some how-tos declare schema_version %d, newer than this broker's %d; their known fields are served and unknown ones ignored", st.NewerThanBroker, howto.SchemaVersion)).
			WithRemedy("update the connector to read them in full"))
	}
	return out
}

func overrideNotice(e howtosearch.Entry) *diag.Record {
	if e.Override.Code == "" {
		return nil
	}
	return diag.New(diag.SeverityInfo, e.Override.Code, howtoSource, e.Override.Notice)
}

func searchHowTos(ctx context.Context, deps HowToDeps, in SearchHowTosIn) SearchHowTosOut {
	ver, drec := resolveHowToVersion(deps, in.InstanceID, in.RevitVersion)
	if drec != nil {
		return SearchHowTosOut{Error: drec}
	}
	if strings.TrimSpace(in.Query) == "" {
		return SearchHowTosOut{RevitVersion: ver, Error: diag.New(diag.SeverityError, "invalid-params", howtoSource, "query is required").
			WithRemedy("describe the task in one plain sentence naming the element type and the operation")}
	}
	if deps.Search == nil {
		return SearchHowTosOut{RevitVersion: ver, Error: howToUnavailable(errors.New("no how-to index is wired into this broker"))}
	}
	res, err := deps.Search.Search(ctx, in.Query, ver)
	if err != nil {
		return SearchHowTosOut{RevitVersion: ver, Error: howToFailed(err)}
	}
	ranker := rankerName(res.Dense, res.Reranked)
	scope := searchScope(in.Query, ver, res.Fingerprint, ranker)
	offset, drec := parseSearchCursor(in.Cursor, scope, "query and revit_version (or instance_id)", howtoSource)
	if drec != nil {
		return SearchHowTosOut{RevitVersion: ver, Ranker: ranker, Error: drec}
	}
	topN := in.TopN
	if topN <= 0 {
		topN = defaultHowToTopN
	}
	if topN > maxHowToTopN {
		topN = maxHowToTopN
	}
	out := SearchHowTosOut{RevitVersion: ver, Ranker: ranker, TotalMatched: len(res.Hits), Notices: corpusNotices(res.Status, deps.LocalDir)}
	if offset > len(res.Hits) {
		offset = len(res.Hits)
	}
	end := offset + topN
	if end > len(res.Hits) {
		end = len(res.Hits)
	}
	anyLocal := false
	for _, h := range res.Hits[offset:end] {
		out.Results = append(out.Results, howToHit(h, ver))
		anyLocal = anyLocal || h.Doc.Source == howto.SourceLocal
		if n := overrideNotice(h.Doc); n != nil {
			out.Notices = append(out.Notices, n)
		}
	}
	if end < len(res.Hits) {
		out.NextCursor = buildSearchCursor(end, scope)
	}
	out.Guidance = howToSearchGuidance(len(out.Results), len(res.Hits), res.Status.Documents, ver, anyLocal, res.Dense, res.Reranked)
	return out
}

func howToHit(h semsearch.HitOf[howtosearch.Entry], ver string) HowToHit {
	e := h.Doc
	return HowToHit{ID: e.Doc.ID, Rev: e.Doc.Rev, Title: e.Doc.Title, Task: e.Doc.Task, Members: e.Doc.Members, Tags: e.Doc.Tags,
		VerifiedOn: e.Verified.Passed, FailedOn: e.Verified.Failed, VerifiedHere: e.VerifiedOn(ver), Score: h.Score,
		Source: e.Source, SharedRev: e.Override.SharedRev}
}

func howToSearchGuidance(returned, total, corpus int, ver string, anyLocal, dense, reranked bool) string {
	if total == 0 {
		return fmt.Sprintf("No how-to matched: the corpus holds %d documents, one per Revit feature or connector mechanism, so a miss usually means the topic is not covered yet rather than the wording. Try once more as a one-sentence task naming the element type and the verb; then use search_functions and write the script yourself -- and if it took a reworded query or a pitfall to get there, submit_howto so the next agent finds it here.", corpus)
	}
	how := "Ranking fused a keyword pass with a sentence-embedding pass over title, task, recorded queries, pitfalls and members, then a cross-encoder re-read your query against the top candidates. "
	switch {
	case !dense:
		how = "Ranking is keyword-only in this build (the embedding models were not bundled). "
	case !reranked:
		how = "Ranking fused a keyword pass with a sentence-embedding pass (the cross-encoder reranker is unavailable in this broker). "
	}
	s := how + "Rank matters more than score. Documents verified on Revit " + ver + " (verified_here: true) lead the top of the list; one that is not is still usually the right starting point on your version -- read its pitfalls, run it, and if it fails on " + ver + " submit the fix with submit_howto (id + change_note). Call describe_howto with the id for the script and pitfalls."
	if anyLocal {
		s += " Results with source \"local\" are the user's own unreviewed documents: read the whole script before running it."
	}
	if total > returned {
		s += " Further candidates are on next_cursor, in decreasing relevance."
	}
	return s
}

func describeHowTo(ctx context.Context, deps HowToDeps, in DescribeHowToIn) DescribeHowToOut {
	ver, drec := resolveHowToVersion(deps, in.InstanceID, in.RevitVersion)
	if drec != nil {
		return DescribeHowToOut{Error: drec}
	}
	if strings.TrimSpace(in.ID) == "" {
		return DescribeHowToOut{RevitVersion: ver, Error: diag.New(diag.SeverityError, "invalid-params", howtoSource, "id is required").
			WithRemedy("pass the id of a search_howtos result")}
	}
	if deps.Search == nil {
		return DescribeHowToOut{RevitVersion: ver, Error: howToUnavailable(errors.New("no how-to index is wired into this broker"))}
	}
	e, from, st, ok, err := deps.Search.Describe(ctx, in.ID)
	if err != nil {
		return DescribeHowToOut{RevitVersion: ver, Error: howToFailed(err)}
	}
	out := DescribeHowToOut{RevitVersion: ver, Notices: corpusNotices(st, deps.LocalDir)}
	if !ok {
		out.Error = diag.New(diag.SeverityError, "howto-not-found", howtoSource, "no how-to has the id "+in.ID).
			WithDetail(map[string]any{"id": in.ID}).
			WithRemedy("ids come from search_howtos results; search for the task and use the id it returns")
		return out
	}
	d := e.Doc
	out.Document = &HowToView{ID: d.ID, Rev: d.Rev, Kind: d.Kind, Title: d.Title, Task: d.Task, Members: d.Members, Script: d.Script,
		ScriptLang: d.ScriptLang, Pitfalls: d.Pitfalls, Tags: d.Tags, APISince: d.APISince, APIUntil: d.APIUntil, Absorbs: d.Absorbs, UpdatedAt: d.UpdatedAt}
	out.Source, out.RedirectedFrom, out.SharedRev = e.Source, from, e.Override.SharedRev
	out.VerifiedOn, out.FailedOn, out.VerifiedHere = e.Verified.Passed, e.Verified.Failed, e.VerifiedOn(ver)
	if stamp, has := e.Verified.ByVersion[ver]; has {
		out.Verification = &HowToVerification{RevitVersion: ver, Status: stamp.Status, By: stamp.By, At: stamp.At, ConnectorVersion: stamp.ConnectorVersion, Diagnostic: stamp.Diagnostic}
	}
	if d.APISince != "" && d.APISince > ver {
		out.APIWarnings = append(out.APIWarnings, fmt.Sprintf("api_since %s: the members this how-to uses are declared to appear in Revit %s, after your %s", d.APISince, d.APISince, ver))
	}
	if d.APIUntil != "" && d.APIUntil < ver {
		out.APIWarnings = append(out.APIWarnings, fmt.Sprintf("api_until %s: the members this how-to uses are declared to disappear after Revit %s, before your %s", d.APIUntil, d.APIUntil, ver))
	}
	if n := overrideNotice(e); n != nil {
		out.Notices = append(out.Notices, n)
	}
	if from != "" {
		out.Notices = append(out.Notices, diag.New(diag.SeverityInfo, "howto-redirected", howtoSource,
			fmt.Sprintf("how-to %s was merged into %s; this is the surviving document", from, d.ID)))
	}
	out.Guidance = howToDescribeGuidance(e, ver, out.Verification, len(out.APIWarnings) > 0)
	return out
}

func howToDescribeGuidance(e howtosearch.Entry, ver string, stamp *HowToVerification, apiWarned bool) string {
	var s string
	switch {
	case stamp != nil && stamp.Status == howto.StampPassed && stamp.By == howto.ByHarness:
		s = fmt.Sprintf("This script ran successfully on Revit %s in the maintainers' harness (%s). Run it as-is with execute_script against your document; its comments are the explanation.", ver, stamp.At.Format("2006-01-02"))
	case stamp != nil && stamp.Status == howto.StampPassed:
		s = fmt.Sprintf("This script ran successfully on Revit %s in a session on this machine (%s), not in the maintainers' harness. Read it before running it.", ver, stamp.At.Format("2006-01-02"))
	case stamp != nil:
		s = fmt.Sprintf("This script FAILED on Revit %s when last swept (%s); the diagnostic is in verification. Expect to adapt it, and submit the fix with submit_howto (id + change_note) once it runs.", ver, stamp.At.Format("2006-01-02"))
	default:
		on := "never"
		if len(e.Verified.Passed) > 0 {
			on = "Revit " + strings.Join(e.Verified.Passed, ", ")
		}
		s = fmt.Sprintf("Not verified on Revit %s (verified on: %s). It is usually still the right starting point: read the pitfalls, run it, and if it fails on %s submit the fix with submit_howto (id + change_note).", ver, on, ver)
	}
	if apiWarned {
		s += " api_warnings names a declared version boundary; check the members with describe_function before running."
	}
	if e.Source == howto.SourceLocal {
		s += " This is a LOCAL document: the user's own, unreviewed. Read the whole script before running it."
	}
	if len(e.Doc.Pitfalls) > 0 {
		s += " The pitfalls are the part written for you: each is a mistake this script avoids, with the symptom you would have seen."
	}
	return s
}
