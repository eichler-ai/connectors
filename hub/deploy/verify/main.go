// verify is the operator check deploy.sh runs after every deploy: call the
// deployed hub's get_skills tool over its real MCP endpoint (the same path a
// client uses, not an internal shortcut) and print hub_version, so a bad
// token or a broken deploy fails the deploy loudly instead of silently at
// the next real client connection.
//
//	HUB_DEV_TOKEN=<token> go run ./hub/deploy/verify <mcp-url>
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"os"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

func main() {
	if len(os.Args) != 2 {
		fmt.Fprintln(os.Stderr, "usage: HUB_DEV_TOKEN=<token> go run ./hub/deploy/verify <mcp-url>")
		os.Exit(2)
	}
	url := os.Args[1]
	token := os.Getenv("HUB_DEV_TOKEN")
	if token == "" {
		fmt.Fprintln(os.Stderr, "verify: HUB_DEV_TOKEN is not set")
		os.Exit(2)
	}

	tr := &mcp.StreamableClientTransport{
		Endpoint:   url,
		HTTPClient: &http.Client{Transport: bearer{token}},
	}
	ctx := context.Background()
	cs, err := mcp.NewClient(&mcp.Implementation{Name: "deploy-verify", Version: "0"}, nil).Connect(ctx, tr, nil)
	if err != nil {
		fmt.Fprintf(os.Stderr, "verify: connect %s: %v\n", url, err)
		os.Exit(1)
	}
	defer cs.Close()

	res, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "get_skills", Arguments: map[string]any{}})
	if err != nil {
		fmt.Fprintf(os.Stderr, "verify: get_skills: %v\n", err)
		os.Exit(1)
	}
	if res.IsError {
		fmt.Fprintln(os.Stderr, "verify: get_skills returned an error result")
		os.Exit(1)
	}

	b, _ := json.Marshal(res.StructuredContent)
	var out struct {
		HubVersion string `json:"hub_version"`
	}
	if err := json.Unmarshal(b, &out); err != nil || out.HubVersion == "" {
		fmt.Fprintf(os.Stderr, "verify: no hub_version in get_skills result: %s\n", b)
		os.Exit(1)
	}
	fmt.Println(out.HubVersion)
}

type bearer struct{ token string }

func (b bearer) RoundTrip(r *http.Request) (*http.Response, error) {
	r.Header.Set("Authorization", "Bearer "+b.token)
	return http.DefaultTransport.RoundTrip(r)
}
