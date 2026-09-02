// Discovery tool registrations: list_functions, search_functions,
// describe_function (PRD §08), routed through a discovery.Router rather
// than execution.Manager — see discovery.Router's own package doc for why
// these two routing paths are kept independent.
package mcpserver

import (
	"context"
	"encoding/json"
	"errors"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/discovery"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch/manager"
)

const discoverySource = "mcp-server.internal.mcpserver"

// ListFunctionsIn is the input schema for the list_functions tool.
type ListFunctionsIn struct {
	InstanceID string `json:"instance_id,omitempty" jsonschema:"instance_id of the target Revit instance; if omitted, any connected instance is used as long as all connected instances share one Revit version -- otherwise this errors and lists the candidates, since results would silently be version-specific"`
	Namespace  string `json:"namespace,omitempty" jsonschema:"omit for the namespace list; provide alone for the type list in that namespace; provide with type_name for that type's member list, e.g. Autodesk.Revit.DB"`
	TypeName   string `json:"type_name,omitempty" jsonschema:"scope to one type within namespace (namespace is required alongside this) -- bare name (Wall) or fully-qualified (Autodesk.Revit.DB.Wall), both work"`
	Cursor     string `json:"cursor,omitempty" jsonschema:"opaque pagination cursor echoed back from a prior response's next_cursor"`
	PageSize   int    `json:"page_size,omitempty" jsonschema:"items per page; default 50"`
}

// SearchFunctionsIn is the input schema for the search_functions tool.
type SearchFunctionsIn struct {
	InstanceID string `json:"instance_id,omitempty" jsonschema:"instance_id of the target Revit instance; if omitted, any connected instance is used as long as all connected instances share one Revit version -- otherwise this errors and lists the candidates, since results would silently be version-specific"`
	Query      string `json:"query" jsonschema:"REQUIRED. One plain sentence describing the task, naming the Revit element type and the operation, e.g. \"move an element to a new location\" or \"get every wall in the document\". Matched by sentence embedding plus keyword fusion and reranked by a cross-encoder, so intent and synonyms match without the exact API name; a suspected type or member name dropped into the sentence also scores through the keyword pass. Avoid bare single keywords."`
	Namespace  string `json:"namespace,omitempty" jsonschema:"scope the search to one namespace, e.g. Autodesk.Revit.DB"`
	Cursor     string `json:"cursor,omitempty" jsonschema:"opaque pagination cursor echoed back from a prior response's next_cursor"`
	TopN       int    `json:"top_n,omitempty" jsonschema:"ranked results per page; default 20"`
}

// DescribeFunctionIn is the input schema for the describe_function tool.
type DescribeFunctionIn struct {
	InstanceID string `json:"instance_id,omitempty" jsonschema:"instance_id of the target Revit instance; if omitted, any connected instance is used as long as all connected instances share one Revit version -- otherwise this errors and lists the candidates, since results would silently be version-specific"`
	Member     string `json:"member,omitempty" jsonschema:"fully-qualified Type.Member, e.g. Autodesk.Revit.DB.Document.Delete. Optional when member_id is given."`
	MemberID   string `json:"member_id,omitempty" jsonschema:"an exact XML-doc-id, e.g. M:Autodesk.Revit.DB.Document.Delete(Autodesk.Revit.DB.ElementId), to pick one specific overload -- the reliable way to disambiguate; returned by search_functions results and by this tool's own overloads[] list when member is ambiguous"`
}

// Member is one reflected API member, the shape shared by list_functions'
// members[] and search_functions' results[] (PRD §08).
type Member struct {
	MemberID      string  `json:"member_id"`
	Kind          string  `json:"kind"`
	Namespace     string  `json:"namespace"`
	DeclaringType string  `json:"declaring_type"`
	Name          string  `json:"name"`
	Signature     string  `json:"signature"`
	Summary       string  `json:"summary,omitempty"`
	Score         float64 `json:"score,omitempty"`
}

// NamespaceEntry is one namespace's entry in list_functions' no-args tier.
type NamespaceEntry struct {
	Namespace string `json:"namespace"`
	TypeCount int    `json:"type_count"`
}

// ListFunctionsOut is the output schema for the list_functions tool.
//
// PRD §08 addendum: list_functions is a strict one-level-at-a-time tree
// (namespaces -> types -> members), not a flat member dump — the three
// tiers below are mutually exclusive per response (only one is ever
// populated), matching the add-in's DiscoveryResultMessage DTOs exactly so
// json.Unmarshal doesn't silently drop fields it doesn't know about.
type ListFunctionsOut struct {
	// No-args tier: namespace names only.
	Namespaces []NamespaceEntry `json:"namespaces,omitempty"`

	// Namespace-scoped tier: type names in that namespace, comma-separated,
	// prefix-stripped. Types is also non-empty here, sharing Namespace with
	// the members tier below.
	Types string `json:"types,omitempty"`

	// Namespace+type-scoped tier: member names of that type, comma-separated,
	// prefix-stripped. describe_function is the only way to get full detail
	// on one of these.
	Type    string `json:"type,omitempty"`
	Members string `json:"members,omitempty"`

	// Namespace is shared by the Types and Members tiers above (empty for
	// the no-args Namespaces tier, which has no single namespace to name).
	Namespace string `json:"namespace,omitempty"`

	NextCursor  string `json:"next_cursor,omitempty"`
	TotalScoped int    `json:"total_scoped,omitempty"`

	// RevitVersion names which connected instance's RevitAPI.dll/.xml this
	// response reflects (PRD §11) -- set from the broker's own instance
	// registry, not round-tripped through the add-in's own response, so a
	// caller that omitted instance_id can tell which version actually
	// answered without a separate list_instances call. Empty on error.
	RevitVersion string       `json:"revit_version,omitempty"`
	Error        *diag.Record `json:"error,omitempty"`
}

// searchManyResults is the total_matched count above which the guidance nudges
// the agent to narrow rather than to widen. A heuristic, not a tuned boundary:
// well past a single default page (top_n 20) so it only fires when the match set
// is genuinely broad enough that the wanted member is plausibly buried. Ranker-
// agnostic -- "too many, narrow it" is sound advice under keyword or embedding
// ranking alike.
const searchManyResults = 50

// searchGuidance returns the hint attached to every search_functions response.
// Ranking is fuzzy and recall is imperfect, so the hint steers the agent by the
// shape of the result set toward the correction most likely to help:
//
//   - nothing matched -> the target is almost never actually absent; reword.
//     This is the moment the wrong "the API isn't there" conclusion is cheapest.
//   - too many matched -> the wanted member is likely buried; narrow, don't widen.
//   - a workable set -> the top hit may still be wrong; the target can rank below
//     what was returned, so a reworded retry is still on the table.
//
// returned is this page's result count; total is total_matched across all pages.
func searchGuidance(returned, total int) string {
	if returned == 0 {
		return "No members matched. This does not mean the API is absent -- ranking is keyword-based, " +
			"so retry with different wording: a synonym, the operation verb, or the domain noun " +
			"(e.g. \"get all walls\" -> try \"collector\" or \"filter elements\"). Or browse the tree " +
			"with list_functions."
	}
	if total > searchManyResults {
		return "Many members matched, so the one you want may be ranked below what was returned. " +
			"Narrow rather than widen: add more context to your query (the specific operation and the " +
			"element type you mean), use a more precise term, or pass namespace to scope the search. " +
			"You can also page with the cursor."
	}
	return "If none of these is the member you want, the target may exist under different wording -- " +
		"retry with a synonym or the operation verb, or browse with list_functions. Ranking is fuzzy; " +
		"a low score or a short list does not mean the API is absent."
}

// SearchFunctionsOut is the output schema for the search_functions tool.
type SearchFunctionsOut struct {
	Results      []Member `json:"results,omitempty"`
	NextCursor   string   `json:"next_cursor,omitempty"`
	TotalMatched int      `json:"total_matched,omitempty"`
	RevitVersion string   `json:"revit_version,omitempty"`
	// Guidance is a broker-added retry hint (searchGuidance), not part of the
	// add-in's response; it tells the agent that fuzzy ranking can miss a member
	// that exists under different wording, so a weak result set warrants a
	// reworded retry rather than "the API isn't there".
	Guidance string `json:"guidance,omitempty"`
	// Ranker says which ranker produced Results: "semantic" (the broker's
	// embedding + keyword + cross-encoder index), "semantic-no-rerank" (the
	// same without the cross-encoder, when its model failed to load),
	// "lexical" (broker index, models not bundled), or "keyword-fallback"
	// (the add-in's own ranker, while the broker index is still building).
	// Guidance explains.
	Ranker string `json:"ranker,omitempty"`
	// Notices carries §01 records about how this response was produced --
	// today, why the broker index did not answer (search-index-building /
	// search-index-build-failed) -- so the reason is structured, not only
	// prose in Guidance.
	Notices []*diag.Record `json:"notices,omitempty"`
	Error   *diag.Record   `json:"error,omitempty"`
}

// DescribeFunctionOut is the output schema for the describe_function tool.
// PRD §08 gives this command two genuinely different response shapes (one
// resolved overload, vs. a disambiguation list of overloads) — passed
// through as a flexible map rather than a single fixed struct, plus Error
// for the failure case.
type DescribeFunctionOut struct {
	Result       map[string]any `json:"result,omitempty"`
	RevitVersion string         `json:"revit_version,omitempty"`
	Error        *diag.Record   `json:"error,omitempty"`
}

// RegisterDiscovery adds list_functions, search_functions, and
// describe_function to s, routed through r. search is the broker-side
// search_functions index (issue #107); nil means every search is forwarded
// to the add-in's keyword ranker, which is also the fallback while an
// instance's index is still building.
func RegisterDiscovery(s *mcp.Server, r *discovery.Router, search *manager.Manager) {
	mcp.AddTool(s, &mcp.Tool{
		Name:        "list_functions",
		Description: "Browse the Revit API as a strict one-level-at-a-time tree, with pagination: no args returns namespace names; +namespace returns type names in it; +namespace+type_name returns member names of it. describe_function gets full signature/summary detail on one specific member.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in ListFunctionsIn) (*mcp.CallToolResult, ListFunctionsOut, error) {
		params := map[string]any{}
		if in.Namespace != "" {
			params["namespace"] = in.Namespace
		}
		if in.TypeName != "" {
			params["type_name"] = in.TypeName
		}
		if in.Cursor != "" {
			params["cursor"] = in.Cursor
		}
		if in.PageSize > 0 {
			params["page_size"] = in.PageSize
		}
		raw, revitVersion, drec := r.ListFunctions(ctx, in.InstanceID, params)
		if drec != nil {
			out := ListFunctionsOut{Error: drec}
			return errorCallToolResultFor(out), out, nil
		}
		var out ListFunctionsOut
		if err := json.Unmarshal(raw, &out); err != nil {
			out = ListFunctionsOut{Error: errWireResponseMalformed("list_functions", err)}
			return errorCallToolResultFor(out), out, nil
		}
		out.RevitVersion = revitVersion
		return nil, out, nil
	})

	mcp.AddTool(s, &mcp.Tool{
		Name:        "search_functions",
		Description: "Semantic search over Revit API members (and loaded add-ins' APIs). Write query as one plain sentence describing the task and naming the element type and operation; ranking fuses a sentence-embedding pass with a keyword pass and reranks with a cross-encoder, so you do not need the exact type/method name. Paginated.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in SearchFunctionsIn) (*mcp.CallToolResult, SearchFunctionsOut, error) {
		if search != nil {
			if out, served := searchViaIndex(ctx, r, search, in); served {
				if out.Error != nil {
					return errorCallToolResultFor(out), out, nil
				}
				return nil, out, nil
			}
		}
		params := map[string]any{"query": in.Query}
		if in.Namespace != "" {
			params["namespace"] = in.Namespace
		}
		if in.Cursor != "" {
			params["cursor"] = in.Cursor
		}
		if in.TopN > 0 {
			params["top_n"] = in.TopN
		}
		raw, revitVersion, drec := r.SearchFunctions(ctx, in.InstanceID, params)
		if drec != nil {
			out := SearchFunctionsOut{Error: drec}
			return errorCallToolResultFor(out), out, nil
		}
		var out SearchFunctionsOut
		if err := json.Unmarshal(raw, &out); err != nil {
			out = SearchFunctionsOut{Error: errWireResponseMalformed("search_functions", err)}
			return errorCallToolResultFor(out), out, nil
		}
		out.RevitVersion = revitVersion
		out.Guidance = searchGuidance(len(out.Results), out.TotalMatched)
		if search != nil {
			out.Ranker = rankerKeywordFallback
			if resolved, _, rdrec := r.ResolveInstance(in.InstanceID); rdrec == nil {
				st := search.Status(resolved)
				out.Guidance = fallbackGuidance(st) + out.Guidance
				out.Notices = append(out.Notices, fallbackNotice(resolved, st))
			}
		}
		return nil, out, nil
	})

	mcp.AddTool(s, &mcp.Tool{
		Name:        "describe_function",
		Description: "Full XML-doc entry (summary, params, returns) for one fully-qualified Revit API member. Requires member and/or member_id. An overloaded member with no member_id returns its overload list to pick from -- re-call with one of those overloads' member_id to get its full detail.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in DescribeFunctionIn) (*mcp.CallToolResult, DescribeFunctionOut, error) {
		if in.Member == "" && in.MemberID == "" {
			drec := diag.New(diag.SeverityError, "missing-required-param", discoverySource,
				"describe_function requires member and/or member_id, but both were empty").
				WithDetail(map[string]any{"params": []string{"member", "member_id"}}).
				WithRemedy("pass member (a fully-qualified Type.Member) or member_id (an exact XML-doc-id from search_functions or a prior describe_function's overloads[] list)")
			out := DescribeFunctionOut{Error: drec}
			return errorCallToolResultFor(out), out, nil
		}
		params := map[string]any{}
		if in.Member != "" {
			params["member"] = in.Member
		}
		if in.MemberID != "" {
			params["member_id"] = in.MemberID
		}
		raw, revitVersion, drec := r.DescribeFunction(ctx, in.InstanceID, params)
		if drec != nil {
			out := DescribeFunctionOut{Error: drec}
			return errorCallToolResultFor(out), out, nil
		}
		var result map[string]any
		if err := json.Unmarshal(raw, &result); err != nil {
			out := DescribeFunctionOut{Error: errWireResponseMalformed("describe_function", err)}
			return errorCallToolResultFor(out), out, nil
		}
		return nil, DescribeFunctionOut{Result: result, RevitVersion: revitVersion}, nil
	})
}

func errWireResponseMalformed(method string, err error) *diag.Record {
	return diag.New(diag.SeverityError, "wire-response-malformed", discoverySource,
		method+" response from the add-in could not be decoded: "+err.Error())
}

// errorCallToolResultFor mirrors tools.go's errorCallToolResult convention
// (MCP tools/call's IsError:true contract, PRD §01's "two channels" note)
// for any out value carrying an Error diag.Record.
func errorCallToolResultFor(out any) *mcp.CallToolResult {
	b, _ := json.MarshalIndent(out, "", "  ")
	return &mcp.CallToolResult{
		IsError: true,
		Content: []mcp.Content{&mcp.TextContent{Text: string(b)}},
	}
}

// searchViaIndex answers search_functions from the broker-side index. served
// is false when the index cannot answer (instance not resolvable is still
// served, as an error; index building/failed is not, so the caller falls back
// to the add-in's ranker).
func searchViaIndex(ctx context.Context, r *discovery.Router, search *manager.Manager, in SearchFunctionsIn) (SearchFunctionsOut, bool) {
	resolved, revitVersion, drec := r.ResolveInstance(in.InstanceID)
	if drec != nil {
		return SearchFunctionsOut{Error: drec}, true
	}
	res, err := search.Search(ctx, resolved, in.Query, in.Namespace)
	if errors.Is(err, manager.ErrNotReady) {
		return SearchFunctionsOut{}, false
	}
	if err != nil {
		return SearchFunctionsOut{Error: diag.New(diag.SeverityError, "search-index-failed", discoverySource,
			"search_functions could not rank against the broker index: "+err.Error()).
			WithRemedy("retry; if it persists, restart the broker and report with its log")}, true
	}
	ranker := rankerName(res.Dense, res.Reranked)
	scope := searchScope(in.Query, in.Namespace, res.Fingerprint, ranker)
	offset, cdrec := parseSearchCursor(in.Cursor, scope, "query and namespace", discoverySource)
	if cdrec != nil {
		return SearchFunctionsOut{Error: cdrec}, true
	}
	page, next := pageHits(res.Hits, offset, clampTopN(in.TopN))
	out := SearchFunctionsOut{
		Results:      page,
		TotalMatched: len(res.Hits),
		RevitVersion: revitVersion,
		Ranker:       ranker,
		Guidance:     semanticGuidance(len(page), len(res.Hits), res.Dense, res.Reranked),
	}
	if next < len(res.Hits) {
		out.NextCursor = buildSearchCursor(next, scope)
	}
	return out, true
}
