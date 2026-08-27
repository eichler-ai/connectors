// list_instances (PRD §05): the broker's live instance registry, with
// status merged from two independent sources it doesn't otherwise share a
// struct with — registry.Registry (connection/document bookkeeping) and
// execution.Manager (per-instance execution state) — composed here rather
// than having either package import the other, the same way broker.Broker
// itself already holds both as sibling fields with no cross-import.
package mcpserver

import (
	"context"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/execution"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
)

// ListInstancesIn is the input schema for the list_instances tool — no
// arguments, it always returns the broker's full current registry.
type ListInstancesIn struct{}

// InstanceDocument is one entry of an instance's documents[] in the
// list_instances response (PRD §05).
type InstanceDocument struct {
	DocumentID string `json:"document_id"`
	Title      string `json:"title"`
	Path       string `json:"path"`
	Workshared bool   `json:"workshared"`
	Active     bool   `json:"active"`
}

// InstanceEntry is one instance's entry in the list_instances response,
// matching the PRD §05 field table exactly.
type InstanceEntry struct {
	InstanceID     string             `json:"instance_id"`
	RevitVersion   string             `json:"revit_version"`
	PID            int                `json:"pid"`
	ConnectedSince time.Time          `json:"connected_since"`
	Status         string             `json:"status"`
	Documents      []InstanceDocument `json:"documents"`
}

// ListInstancesOut is the output schema for the list_instances tool.
type ListInstancesOut struct {
	Instances []InstanceEntry `json:"instances"`
}

// RegisterInstances adds list_instances to s, merging state from reg and
// mgr.
func RegisterInstances(s *mcp.Server, reg *registry.Registry, mgr *execution.Manager) {
	mcp.AddTool(s, &mcp.Tool{
		Name:        "list_instances",
		Description: "List every Revit instance currently connected to the broker, with each instance's live status and open documents. Call this before targeting {instance_id, document_id} in execute_script if you don't already have them from a recent register/reconnect.",
	}, func(ctx context.Context, req *mcp.CallToolRequest, in ListInstancesIn) (*mcp.CallToolResult, ListInstancesOut, error) {
		out := ListInstancesOut{Instances: []InstanceEntry{}}
		for _, inst := range reg.List() {
			out.Instances = append(out.Instances, InstanceEntry{
				InstanceID:     inst.InstanceID,
				RevitVersion:   inst.RevitVersion,
				PID:            inst.PID,
				ConnectedSince: inst.ConnectedSince,
				Status:         string(mergedStatus(reg, mgr, inst.InstanceID)),
				Documents:      instanceDocuments(inst.Documents),
			})
		}
		return nil, out, nil
	})
}

// mergedStatus applies the resolved precedence (PRD §05 spec review):
// unrecoverable always wins (terminal, needs a Revit restart); below that,
// a missed heartbeat overrides busy/pending/idle, since the ping is sent
// from the add-in's background connection thread independent of the UI
// thread — a missed one means something more severe than "a script is
// running," and the caller shouldn't just poll-and-wait on the strength of
// a busy/pending status that may itself be stale.
func mergedStatus(reg *registry.Registry, mgr *execution.Manager, instanceID string) execution.Status {
	execStatus := mgr.StatusForInstance(instanceID)
	if execStatus == execution.StatusUnrecoverable {
		return execStatus
	}
	if !reg.IsResponsive(instanceID, time.Now()) {
		return statusUnresponsive
	}
	return execStatus
}

// statusUnresponsive is list_instances-only (PRD §05) — it's derived from
// heartbeat liveness, not execution state, so it doesn't belong in
// execution.Status's own enum.
const statusUnresponsive execution.Status = "unresponsive"

// unsavedPathSentinel is the PRD §05 documented value for a document with
// no on-disk path yet (new/unsaved/detached-not-yet-saved) — an empty
// string would otherwise require an agent to special-case "" as meaning
// unsaved rather than reading the documented sentinel.
const unsavedPathSentinel = "unsaved"

func instanceDocuments(docs []registry.Document) []InstanceDocument {
	out := make([]InstanceDocument, 0, len(docs))
	for _, d := range docs {
		path := d.Path
		if path == "" {
			path = unsavedPathSentinel
		}
		out = append(out, InstanceDocument{
			DocumentID: d.ID,
			Title:      d.Title,
			Path:       path,
			Workshared: d.Workshared,
			Active:     d.Active,
		})
	}
	return out
}
