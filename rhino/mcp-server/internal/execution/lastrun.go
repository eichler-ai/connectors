package execution

// LastRun is the connector's last completed run on a document (PRD §05 "observability only"):
// the plug-in records it and reports it in register (registry.Document) and on every execution result.
type LastRun struct {
	ExecutionID     string `json:"execution_id"`
	AgentClientID   string `json:"agent_client_id"`
	FinishedAt      string `json:"finished_at"`
	Status          string `json:"status"`
	Label           string `json:"label,omitempty"`
	ChangedDocument bool   `json:"changed_document"`
}
