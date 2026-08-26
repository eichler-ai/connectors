// Discovery tool registrations: list_functions, search_functions,
// describe_function (PRD §08), routed through a discovery.Router rather
// than execution.Manager — see discovery.Router's own package doc for why
// these two routing paths are kept independent.
package mcpserver

import (
	"context"
	"encoding/json"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/discovery"
)

const discoverySource = "mcp-server.internal.mcpserver"

// ListFunctionsIn is the input schema for the list_functions tool.
type ListFunctionsIn struct {
	InstanceID string `json:"instance_id,omitempty" jsonschema:"instance_id of the target Revit instance; if omitted, any currently-connected instance is used"`
	Namespace  string `json:"namespace,omitempty" jsonschema:"scope to a namespace, e.g. Autodesk.Revit.DB"`
	TypeName   string `json:"type_name,omitempty" jsonschema:"scope to one fully-qualified type, e.g. Autodesk.Revit.DB.Wall"`
	Cursor     string `json:"cursor,omitempty" jsonschema:"opaque pagination cursor echoed back from a prior response's next_cursor"`
	PageSize   int    `json:"page_size,omitempty" jsonschema:"members per page; default 50"`
}

// SearchFunctionsIn is the input schema for the search_functions tool.
type SearchFunctionsIn struct {
	InstanceID string `json:"instance_id,omitempty" jsonschema:"instance_id of the target Revit instance; if omitted, any currently-connected instance is used"`
	Query      string `json:"query" jsonschema:"fuzzy-matched against member names and XML-doc summary text"`
	Cursor     string `json:"cursor,omitempty" jsonschema:"opaque pagination cursor echoed back from a prior response's next_cursor"`
	TopN       int    `json:"top_n,omitempty" jsonschema:"ranked results per page; default 20"`
}

// DescribeFunctionIn is the input schema for the describe_function tool.
type DescribeFunctionIn struct {
	InstanceID    string `json:"instance_id,omitempty" jsonschema:"instance_id of the target Revit instance; if omitted, any currently-connected instance is used"`
	Member        string `json:"member" jsonschema:"fully-qualified Type.Member, e.g. Autodesk.Revit.DB.Document.Delete"`
	OverloadIndex *int   `json:"overload_index,omitempty" jsonschema:"pick one specific overload by index, when member has more than one"`
	MemberID      string `json:"member_id,omitempty" jsonschema:"an exact XML-doc-id, e.g. M:Autodesk.Revit.DB.Document.Delete(Autodesk.Revit.DB.ElementId), to pick one specific overload"`
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

// ListFunctionsOut is the output schema for the list_functions tool.
type ListFunctionsOut struct {
	Members     []Member     `json:"members,omitempty"`
	NextCursor  string       `json:"next_cursor,omitempty"`
	TotalScoped int          `json:"total_scoped,omitempty"`
	Error       *diag.Record `json:"error,omitempty"`
}

// SearchFunctionsOut is the output schema for the search_functions tool.
type SearchFunctionsOut struct {
	Results      []Member     `json:"results,omitempty"`
	NextCursor   string       `json:"next_cursor,omitempty"`
	TotalMatched int          `json:"total_matched,omitempty"`
	Error        *diag.Record `json:"error,omitempty"`
}

// DescribeFunctionOut is the output schema for the describe_function tool.
// PRD §08 gives this command two genuinely different response shapes (one
// resolved overload, vs. a disambiguation list of overloads) — passed
// through as a flexible map rather than a single fixed struct, plus Error
// for the failure case.
type DescribeFunctionOut struct {
	Result map[string]any `json:"result,omitempty"`
	Error  *diag.Record   `json:"error,omitempty"`
}

// RegisterDiscovery adds list_functions, search_functions, and
// describe_function to s, routed through r.
func RegisterDiscovery(s *mcp.Server, r *discovery.Router) {
	mcp.AddTool(s, &mcp.Tool{
		Name:        "list_functions",
		Description: "List Revit API members, optionally scoped by namespace or type, with pagination. Use to browse the API surface instead of guessing method names.",
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
		raw, drec := r.ListFunctions(ctx, in.InstanceID, params)
		if drec != nil {
			out := ListFunctionsOut{Error: drec}
			return errorCallToolResultFor(out), out, nil
		}
		var out ListFunctionsOut
		if err := json.Unmarshal(raw, &out); err != nil {
			out = ListFunctionsOut{Error: errWireResponseMalformed("list_functions", err)}
			return errorCallToolResultFor(out), out, nil
		}
		return nil, out, nil
	})

	mcp.AddTool(s, &mcp.Tool{
		Name:        "search_functions",
		Description: "Fuzzy-search Revit API members by name and XML-doc summary text, ranked, with pagination. Use when you don't know the exact type/method name to start from.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in SearchFunctionsIn) (*mcp.CallToolResult, SearchFunctionsOut, error) {
		params := map[string]any{"query": in.Query}
		if in.Cursor != "" {
			params["cursor"] = in.Cursor
		}
		if in.TopN > 0 {
			params["top_n"] = in.TopN
		}
		raw, drec := r.SearchFunctions(ctx, in.InstanceID, params)
		if drec != nil {
			out := SearchFunctionsOut{Error: drec}
			return errorCallToolResultFor(out), out, nil
		}
		var out SearchFunctionsOut
		if err := json.Unmarshal(raw, &out); err != nil {
			out = SearchFunctionsOut{Error: errWireResponseMalformed("search_functions", err)}
			return errorCallToolResultFor(out), out, nil
		}
		return nil, out, nil
	})

	mcp.AddTool(s, &mcp.Tool{
		Name:        "describe_function",
		Description: "Full XML-doc entry (summary, params, returns) for one fully-qualified Revit API member. If the member has multiple overloads and neither overload_index nor member_id disambiguates, returns the list of overloads instead.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in DescribeFunctionIn) (*mcp.CallToolResult, DescribeFunctionOut, error) {
		params := map[string]any{"member": in.Member}
		if in.OverloadIndex != nil {
			params["overload_index"] = *in.OverloadIndex
		}
		if in.MemberID != "" {
			params["member_id"] = in.MemberID
		}
		raw, drec := r.DescribeFunction(ctx, in.InstanceID, params)
		if drec != nil {
			out := DescribeFunctionOut{Error: drec}
			return errorCallToolResultFor(out), out, nil
		}
		var result map[string]any
		if err := json.Unmarshal(raw, &result); err != nil {
			out := DescribeFunctionOut{Error: errWireResponseMalformed("describe_function", err)}
			return errorCallToolResultFor(out), out, nil
		}
		return nil, DescribeFunctionOut{Result: result}, nil
	})
}

func errWireResponseMalformed(method string, err error) *diag.Record {
	return diag.New(diag.SeverityError, "wire_response_malformed", discoverySource,
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
