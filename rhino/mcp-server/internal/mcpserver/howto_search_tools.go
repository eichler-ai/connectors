// search_howtos and describe_howto: the agent-facing read side of the Rhino
// how-to corpus, served from internal/howtosearch. Both require the caller's
// Rhino version -- exactly one of instance_id (resolved through the discovery
// Router) or rhino_version -- because a how-to's verification is per version
// and the agent must never be handed a script without being told whether it
// ran on its version. The version is a preference in ranking and a label on
// every result, never a filter.
package mcpserver

import (
	"context"
	"errors"
	"fmt"
	"regexp"
	"strings"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/internal/servercore/semsearch"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/discovery"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/howto"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/howtosearch"
)

const howtoSource = "mcp-server.internal.mcpserver.howto"

// Paging bounds for search_howtos. A hit is a paragraph, not a one-line
// member, so the default page is short.
const (
	defaultHowToTopN = 5
	maxHowToTopN     = 50
)

// HowToDeps is what the how-to tools need: the index and a way to resolve an
// instance_id to its Rhino version.
type HowToDeps struct {
	Search *howtosearch.Service
	Router *discovery.Router
}

// SearchHowTosIn is the input schema for search_howtos.
type SearchHowTosIn struct {
	Query        string `json:"query" jsonschema:"REQUIRED. The task as one plain sentence naming the Rhino type and the operation (\"add a sphere to the document\", \"draw a circle on a named layer\"); a symptom you hit or a member you suspect also scores."`
	InstanceID   string `json:"instance_id,omitempty" jsonschema:"the Rhino instance you are driving (from list_instances); its version decides which how-tos are marked verified_here. Exactly one of instance_id / rhino_version is required."`
	RhinoVersion string `json:"rhino_version,omitempty" jsonschema:"the Rhino major version to rank and label for, e.g. \"8\"; use this INSTEAD of instance_id when working ahead of a connection. Exactly one of the two is required."`
	Cursor       string `json:"cursor,omitempty" jsonschema:"opaque pagination cursor echoed back from a prior response's next_cursor"`
	TopN         int    `json:"top_n,omitempty" jsonschema:"results per page; default 5, max 50"`
}

// HowToHit is one search_howtos result: enough to choose, not the script
// (describe_howto has that).
type HowToHit struct {
	ID           string   `json:"id"`
	Rev          int      `json:"rev"`
	Title        string   `json:"title"`
	Task         string   `json:"task"`
	Members      []string `json:"members,omitempty"`
	Tags         []string `json:"tags,omitempty"`
	VerifiedOn   []string `json:"verified_on,omitempty"`
	FailedOn     []string `json:"failed_on,omitempty"`
	VerifiedHere bool     `json:"verified_here"`
	Score        float64  `json:"score,omitempty"`
	Source       string   `json:"source"`
}

// SearchHowTosOut is the output schema for search_howtos.
type SearchHowTosOut struct {
	Results      []HowToHit `json:"results,omitempty"`
	NextCursor   string     `json:"next_cursor,omitempty"`
	TotalMatched int        `json:"total_matched,omitempty"`
	// RhinoVersion is the version the call resolved to and ranked for.
	RhinoVersion string `json:"rhino_version,omitempty"`
	// Ranker is the same vocabulary as search_functions: semantic,
	// semantic-no-rerank or lexical.
	Ranker   string         `json:"ranker,omitempty"`
	Guidance string         `json:"guidance,omitempty"`
	Notices  []*diag.Record `json:"notices,omitempty"`
	Error    *diag.Record   `json:"error,omitempty"`
}

// DescribeHowToIn is the input schema for describe_howto.
type DescribeHowToIn struct {
	ID           string `json:"id" jsonschema:"REQUIRED. The how-to's id from a search_howtos result."`
	InstanceID   string `json:"instance_id,omitempty" jsonschema:"the Rhino instance you are driving (from list_instances); its version decides the verification reported. Exactly one of instance_id / rhino_version is required."`
	RhinoVersion string `json:"rhino_version,omitempty" jsonschema:"the Rhino major version to report verification for, e.g. \"8\"; use this INSTEAD of instance_id when working ahead of a connection. Exactly one of the two is required."`
}

// HowToView is the agent-facing document: everything the agent acts on,
// without the maintainer-facing provenance and verify block.
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
	RhinoVersion     string    `json:"rhino_version"`
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
	// RedirectedFrom is set when the requested id was merged into this lineage.
	RedirectedFrom string `json:"redirected_from,omitempty"`
	RhinoVersion   string `json:"rhino_version,omitempty"`
	VerifiedHere   bool   `json:"verified_here"`
	// Verification is the stamp for the resolved version, or nil when the
	// document was never swept on it.
	Verification *HowToVerification `json:"verification,omitempty"`
	VerifiedOn   []string           `json:"verified_on,omitempty"`
	FailedOn     []string           `json:"failed_on,omitempty"`
	// APIWarnings evaluates api_since / api_until against the resolved version.
	APIWarnings []string       `json:"api_warnings,omitempty"`
	Guidance    string         `json:"guidance,omitempty"`
	Notices     []*diag.Record `json:"notices,omitempty"`
	Error       *diag.Record   `json:"error,omitempty"`
}

// RegisterHowTo adds search_howtos and describe_howto to s. A nil deps.Search
// (no index wired) makes both tools report the corpus unavailable.
func RegisterHowTo(s *mcp.Server, deps HowToDeps) {
	mcp.AddTool(s, &mcp.Tool{
		Name:        "search_howtos",
		Description: "Find a worked, harness-verified how-to for a Rhino task before writing a script from scratch: each document is one task with a complete execute_script body, the members it uses and the pitfalls it avoids. Ranked like search_functions; documents verified on your Rhino version lead and every hit says whether it was (verified_here). Requires exactly one of instance_id or rhino_version. Then describe_howto for the script.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in SearchHowTosIn) (*mcp.CallToolResult, SearchHowTosOut, error) {
		out := searchHowTos(ctx, deps, in)
		if out.Error != nil {
			return errorCallToolResultFor(out), out, nil
		}
		return nil, out, nil
	})
	mcp.AddTool(s, &mcp.Tool{
		Name:        "describe_howto",
		Description: "One how-to in full -- script, members, pitfalls -- with its verification for your Rhino version (stamp status, who ran it, when) and api_since/api_until warnings. Requires exactly one of instance_id or rhino_version. Read the pitfalls before running the script.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in DescribeHowToIn) (*mcp.CallToolResult, DescribeHowToOut, error) {
		out := describeHowTo(ctx, deps, in)
		if out.Error != nil {
			return errorCallToolResultFor(out), out, nil
		}
		return nil, out, nil
	})
}

// rhinoVersionRe accepts a major version, with or without a trailing
// dotted service release (the registry may report "8" or "8.35"); the major
// part is what a stamp and a how-to record, since Rhino's API-compat boundary
// is the major version.
var rhinoVersionRe = regexp.MustCompile(`^([1-9][0-9]?)(\.\d+)*$`)

// majorVersion reduces a reported version ("8.35") to its major ("8").
func majorVersion(v string) string {
	if i := strings.IndexByte(v, '.'); i >= 0 {
		return v[:i]
	}
	return v
}

// resolveHowToVersion applies the exactly-one rule and returns the major
// version to rank and label for.
func resolveHowToVersion(deps HowToDeps, instanceID, rhinoVersion string) (string, *diag.Record) {
	remedy := "pass instance_id (from list_instances) so the answer matches the Rhino you are driving, or rhino_version (e.g. \"8\") when working ahead of a connection -- one of the two, not both"
	switch {
	case instanceID == "" && rhinoVersion == "":
		return "", diag.New(diag.SeverityError, "howto-version-required", howtoSource,
			"how-tos are verified per Rhino version, so the call must say which version it is for: neither instance_id nor rhino_version was given").
			WithRemedy(remedy)
	case instanceID != "" && rhinoVersion != "":
		return "", diag.New(diag.SeverityError, "howto-version-required", howtoSource,
			"instance_id and rhino_version were both given; the instance's own version would be used, so pass only one").
			WithDetail(map[string]any{"instance_id": instanceID, "rhino_version": rhinoVersion}).
			WithRemedy(remedy)
	case rhinoVersion != "":
		if !rhinoVersionRe.MatchString(rhinoVersion) {
			return "", diag.New(diag.SeverityError, "howto-version-invalid", howtoSource,
				fmt.Sprintf("rhino_version %q is not a Rhino version number", rhinoVersion)).
				WithDetail(map[string]any{"rhino_version": rhinoVersion}).
				WithRemedy("pass the major version, e.g. \"8\"")
		}
		return majorVersion(rhinoVersion), nil
	}
	if deps.Router == nil {
		return "", diag.New(diag.SeverityError, "no-instance-connected", howtoSource, "no Rhino instance registry is available to resolve instance_id").
			WithRemedy("pass rhino_version instead")
	}
	_, ver, drec := deps.Router.ResolveInstance(instanceID)
	if drec != nil {
		return "", drec
	}
	if ver == "" {
		return "", diag.New(diag.SeverityError, "instance-not-found", howtoSource, "instance "+instanceID+" reports no Rhino version").
			WithRemedy("call list_instances and pass a current instance_id, or pass rhino_version")
	}
	return majorVersion(ver), nil
}

// howToFailed maps a Service error: a load failure is a build problem (not
// retryable), anything else is a ranking failure (a model call) worth one retry.
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
		WithRemedy("this is a broker build problem, not something to retry; fall back to search_functions and report it")
}

// corpusNotices reports what the corpus loader had to flag, on every response.
func corpusNotices(st howtosearch.Status) []*diag.Record {
	var out []*diag.Record
	if st.NewerThanBroker > 0 {
		out = append(out, diag.New(diag.SeverityInfo, "howto-corpus-newer-than-broker", howtoSource,
			fmt.Sprintf("some how-tos declare schema_version %d, newer than this broker's %d; their known fields are served and unknown ones ignored", st.NewerThanBroker, howto.SchemaVersion)).
			WithRemedy("update the connector to read them in full"))
	}
	return out
}

func searchHowTos(ctx context.Context, deps HowToDeps, in SearchHowTosIn) SearchHowTosOut {
	ver, drec := resolveHowToVersion(deps, in.InstanceID, in.RhinoVersion)
	if drec != nil {
		return SearchHowTosOut{Error: drec}
	}
	if strings.TrimSpace(in.Query) == "" {
		return SearchHowTosOut{RhinoVersion: ver, Error: diag.New(diag.SeverityError, "invalid-params", howtoSource, "query is required").
			WithRemedy("describe the task in one plain sentence naming the Rhino type and the operation")}
	}
	if deps.Search == nil {
		return SearchHowTosOut{RhinoVersion: ver, Error: howToUnavailable(errors.New("no how-to index is wired into this broker"))}
	}
	res, err := deps.Search.Search(ctx, in.Query, ver)
	if err != nil {
		return SearchHowTosOut{RhinoVersion: ver, Error: howToFailed(err)}
	}
	ranker := rankerName(res.Dense, res.Reranked)
	scope := searchScope(in.Query, ver, res.Fingerprint, ranker)
	offset, drec := parseSearchCursor(in.Cursor, scope, "query and rhino_version (or instance_id)", howtoSource)
	if drec != nil {
		return SearchHowTosOut{RhinoVersion: ver, Ranker: ranker, Error: drec}
	}
	topN := in.TopN
	if topN <= 0 {
		topN = defaultHowToTopN
	}
	if topN > maxHowToTopN {
		topN = maxHowToTopN
	}
	out := SearchHowTosOut{RhinoVersion: ver, Ranker: ranker, TotalMatched: len(res.Hits), Notices: corpusNotices(res.Status)}
	if offset > len(res.Hits) {
		offset = len(res.Hits)
	}
	end := offset + topN
	if end > len(res.Hits) {
		end = len(res.Hits)
	}
	for _, h := range res.Hits[offset:end] {
		out.Results = append(out.Results, howToHit(h, ver))
	}
	if end < len(res.Hits) {
		out.NextCursor = buildSearchCursor(end, scope)
	}
	out.Guidance = howToSearchGuidance(len(out.Results), len(res.Hits), res.Status.Documents, ver, res.Dense, res.Reranked)
	return out
}

func howToHit(h semsearch.HitOf[howtosearch.Entry], ver string) HowToHit {
	e := h.Doc
	return HowToHit{ID: e.Doc.ID, Rev: e.Doc.Rev, Title: e.Doc.Title, Task: e.Doc.Task, Members: e.Doc.Members, Tags: e.Doc.Tags,
		VerifiedOn: e.Verified.Passed, FailedOn: e.Verified.Failed, VerifiedHere: e.VerifiedOn(ver), Score: h.Score,
		Source: e.Source}
}

func howToSearchGuidance(returned, total, corpus int, ver string, dense, reranked bool) string {
	if total == 0 {
		return fmt.Sprintf("No how-to matched: the corpus holds %d documents, one per Rhino task or mechanism, so a miss usually means the topic is not covered yet rather than the wording. Try once more as a one-sentence task naming the type and the verb; then use search_functions and write the script yourself.", corpus)
	}
	how := "Ranking fused a keyword pass with a sentence-embedding pass over title, task, recorded queries, pitfalls and members, then a cross-encoder re-read your query against the top candidates. "
	switch {
	case !dense:
		how = "Ranking is keyword-only in this build (the embedding models were not bundled). "
	case !reranked:
		how = "Ranking fused a keyword pass with a sentence-embedding pass (the cross-encoder reranker is unavailable in this broker). "
	}
	s := how + "Rank matters more than score. Documents verified on Rhino " + ver + " (verified_here: true) lead the top of the list; one that is not is still usually the right starting point on your version -- read its pitfalls and run it. Call describe_howto with the id for the script and pitfalls."
	if total > returned {
		s += " Further candidates are on next_cursor, in decreasing relevance."
	}
	return s
}

func describeHowTo(ctx context.Context, deps HowToDeps, in DescribeHowToIn) DescribeHowToOut {
	ver, drec := resolveHowToVersion(deps, in.InstanceID, in.RhinoVersion)
	if drec != nil {
		return DescribeHowToOut{Error: drec}
	}
	if strings.TrimSpace(in.ID) == "" {
		return DescribeHowToOut{RhinoVersion: ver, Error: diag.New(diag.SeverityError, "invalid-params", howtoSource, "id is required").
			WithRemedy("pass the id of a search_howtos result")}
	}
	if deps.Search == nil {
		return DescribeHowToOut{RhinoVersion: ver, Error: howToUnavailable(errors.New("no how-to index is wired into this broker"))}
	}
	e, from, st, ok, err := deps.Search.Describe(ctx, in.ID)
	if err != nil {
		return DescribeHowToOut{RhinoVersion: ver, Error: howToFailed(err)}
	}
	out := DescribeHowToOut{RhinoVersion: ver, Notices: corpusNotices(st)}
	if !ok {
		out.Error = diag.New(diag.SeverityError, "howto-not-found", howtoSource, "no how-to has the id "+in.ID).
			WithDetail(map[string]any{"id": in.ID}).
			WithRemedy("ids come from search_howtos results; search for the task and use the id it returns")
		return out
	}
	d := e.Doc
	out.Document = &HowToView{ID: d.ID, Rev: d.Rev, Kind: d.Kind, Title: d.Title, Task: d.Task, Members: d.Members, Script: d.Script,
		ScriptLang: d.ScriptLang, Pitfalls: d.Pitfalls, Tags: d.Tags, APISince: d.APISince, APIUntil: d.APIUntil, Absorbs: d.Absorbs, UpdatedAt: d.UpdatedAt}
	out.Source, out.RedirectedFrom = e.Source, from
	out.VerifiedOn, out.FailedOn, out.VerifiedHere = e.Verified.Passed, e.Verified.Failed, e.VerifiedOn(ver)
	if stamp, has := e.Verified.ByVersion[ver]; has {
		out.Verification = &HowToVerification{RhinoVersion: ver, Status: stamp.Status, By: stamp.By, At: stamp.At, ConnectorVersion: stamp.ConnectorVersion, Diagnostic: stamp.Diagnostic}
	}
	if d.APISince != "" && d.APISince > ver {
		out.APIWarnings = append(out.APIWarnings, fmt.Sprintf("api_since %s: the members this how-to uses are declared to appear in Rhino %s, after your %s", d.APISince, d.APISince, ver))
	}
	if d.APIUntil != "" && d.APIUntil < ver {
		out.APIWarnings = append(out.APIWarnings, fmt.Sprintf("api_until %s: the members this how-to uses are declared to disappear after Rhino %s, before your %s", d.APIUntil, d.APIUntil, ver))
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
		s = fmt.Sprintf("This script ran successfully on Rhino %s in the maintainers' harness (%s). Run it as-is with execute_script against your document; its comments are the explanation.", ver, stamp.At.Format("2006-01-02"))
	case stamp != nil && stamp.Status == howto.StampPassed:
		s = fmt.Sprintf("This script ran successfully on Rhino %s in a session on this machine (%s), not in the maintainers' harness. Read it before running it.", ver, stamp.At.Format("2006-01-02"))
	case stamp != nil:
		s = fmt.Sprintf("This script FAILED on Rhino %s when last swept (%s); the diagnostic is in verification. Expect to adapt it.", ver, stamp.At.Format("2006-01-02"))
	default:
		on := "never"
		if len(e.Verified.Passed) > 0 {
			on = "Rhino " + strings.Join(e.Verified.Passed, ", ")
		}
		s = fmt.Sprintf("Not verified on Rhino %s (verified on: %s). It is usually still the right starting point: read the pitfalls and run it.", ver, on)
	}
	if apiWarned {
		s += " api_warnings names a declared version boundary; check the members with describe_function before running."
	}
	if len(e.Doc.Pitfalls) > 0 {
		s += " The pitfalls are the part written for you: each is a mistake this script avoids, with the symptom you would have seen."
	}
	return s
}
