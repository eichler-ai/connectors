// Package mcpserver registers the agent-facing MCP tools. Phase 1 PR 1:
// list_instances only. Error reporting follows the Revit server: a failed tool
// call is a normal result with IsError set and the diagnostic record as text.
package mcpserver

import (
	"context"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/registry"
)

// ListInstancesIn takes no arguments.
type ListInstancesIn struct{}

// DocumentOut is one open document (PRD §05, §12).
type DocumentOut struct {
	DocumentID string `json:"document_id"`
	Title      string `json:"title"`
	Path       string `json:"path,omitempty"`
	Active     bool   `json:"active"`
	// LastRun is the connector's last completed run on this document, from any server (PRD §05).
	LastRun *registry.LastRun `json:"last_run,omitempty"`
}

// InstanceOut is one connected Rhino (PRD §05 "Instance discovery").
type InstanceOut struct {
	InstanceID     string                 `json:"instance_id"`
	RhinoVersion   string                 `json:"rhino_version"`
	Platform       string                 `json:"platform"`
	BridgeVersion  string                 `json:"bridge_version"`
	PID            int                    `json:"pid"`
	ConnectedSince time.Time              `json:"connected_since"`
	Status         string                 `json:"status"`
	Memory         *registry.MemorySample `json:"memory,omitempty"`
	Documents      []DocumentOut          `json:"documents"`
}

// ListInstancesOut is the tool's result.
type ListInstancesOut struct {
	Instances []InstanceOut `json:"instances"`
	// Guidance is the one-line orientation an agent needs when the list is empty.
	Guidance string `json:"guidance,omitempty"`
}

// StatusFunc answers the execution-state half of an instance's status
// (idle/pending/busy/unrecoverable); nil until the executor exists, which reads idle.
type StatusFunc func(instanceID string) string

// RegisterInstances adds list_instances.
func RegisterInstances(s *mcp.Server, reg *registry.Registry, execStatus StatusFunc, now func() time.Time) {
	if now == nil {
		now = time.Now
	}
	mcp.AddTool(s, &mcp.Tool{
		Name: "list_instances",
		Description: "List the running Rhino instances this server is connected to, with their open documents. " +
			"Every script call targets {instance_id, document_id} from here. Answers instantly from the server's registry; " +
			"a Rhino that is starting up appears within a few seconds -- wait and re-check rather than reporting a failure.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in ListInstancesIn) (*mcp.CallToolResult, ListInstancesOut, error) {
		t := now()
		var out ListInstancesOut
		for _, inst := range reg.List() {
			status := "idle"
			if inst.ExecutionState != "" {
				status = inst.ExecutionState // the plug-in owns busy state (PRD §05)
			}
			if execStatus != nil {
				status = execStatus(inst.InstanceID)
			}
			if !reg.IsResponsive(inst.InstanceID, t) {
				status = "unresponsive"
			}
			docs := make([]DocumentOut, 0, len(inst.Documents))
			for _, d := range inst.Documents {
				docs = append(docs, DocumentOut{DocumentID: d.ID, Title: d.Title, Path: d.Path, Active: d.Active, LastRun: d.LastRun})
			}
			out.Instances = append(out.Instances, InstanceOut{
				InstanceID: inst.InstanceID, RhinoVersion: inst.RhinoVersion, Platform: inst.Platform, BridgeVersion: inst.BridgeVersion,
				PID: inst.PID, ConnectedSince: inst.ConnectedSince, Status: status, Memory: inst.Memory, Documents: docs,
			})
		}
		if out.Instances == nil {
			out.Instances = []InstanceOut{}
			out.Guidance = "No Rhino is connected. Rhino 8 with the MCP Bridge plug-in loaded publishes itself within seconds of starting; " +
				"if it is running, run the MCPBridgeStatus command in Rhino to see whether the bridge started."
		}
		return nil, out, nil
	})
}
