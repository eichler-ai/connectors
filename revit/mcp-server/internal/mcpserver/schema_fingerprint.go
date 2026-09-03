package mcpserver

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"sort"
	"strings"

	"github.com/google/jsonschema-go/jsonschema"
)

// toolInputTypes lists every tool this package registers, paired with the Go
// type whose inferred JSON Schema is that tool's input contract. It is the one
// source the fingerprint reads; TestSchemaFingerprintCoversEveryRegisteredTool
// stands up the real server and fails if a tool is added or renamed without
// updating this list, so it cannot silently drift out of sync with Register*.
//
// The value is a func returning the inferred schema rather than the type
// itself, because Go generics cannot be stored heterogeneously; each entry
// closes over jsonschema.For[ThatInputType].
var toolInputTypes = []struct {
	name   string
	schema func() (*jsonschema.Schema, error)
}{
	{"execute_script", schemaFor[ExecuteScriptIn]},
	{"poll_execution", schemaFor[PollExecutionIn]},
	{"cancel_execution", schemaFor[CancelExecutionIn]},
	{"undo", schemaFor[UndoRedoIn]},
	{"redo", schemaFor[UndoRedoIn]},
	{"list_instances", schemaFor[ListInstancesIn]},
	{"list_functions", schemaFor[ListFunctionsIn]},
	{"search_functions", schemaFor[SearchFunctionsIn]},
	{"describe_function", schemaFor[DescribeFunctionIn]},
	{"search_howtos", schemaFor[SearchHowTosIn]},
	{"describe_howto", schemaFor[DescribeHowToIn]},
	{"submit_howto", schemaFor[SubmitHowToIn]},
	{"get_skills", schemaFor[GetSkillsIn]},
}

func schemaFor[T any]() (*jsonschema.Schema, error) { return jsonschema.For[T](nil) }

// ToolSchemaFingerprint is a stable hash of this broker's agent-facing tool
// CONTRACT: the set of tool names and, for each, the JSON Schema of its
// arguments. It exists so a secondary broker can tell whether the primary it
// is about to proxy through validates tool calls the same way it would --
// two builds whose fingerprints match accept and reject exactly the same
// argument shapes, whatever else differs between them (issue #197: a secondary
// silently proxying through an OLDER primary made valid calls fail against the
// primary's stale schema, e.g. a `label` the primary's build predated).
//
// Deliberately excludes tool DESCRIPTIONS and output schemas: neither changes
// whether an argument is accepted, so folding them in would fire the
// compatibility check on cosmetic edits (a reworded description) that cannot
// cause the failure this guards against. Input schema plus tool-name set are
// exactly the surface a call is validated against.
func ToolSchemaFingerprint() (string, error) {
	lines := make([]string, 0, len(toolInputTypes))
	for _, t := range toolInputTypes {
		s, err := t.schema()
		if err != nil {
			return "", fmt.Errorf("mcpserver: inferring input schema for %q: %w", t.name, err)
		}
		// json.Marshal sorts object keys, so the encoding is canonical for a
		// given schema regardless of field declaration order.
		b, err := json.Marshal(s)
		if err != nil {
			return "", fmt.Errorf("mcpserver: encoding input schema for %q: %w", t.name, err)
		}
		lines = append(lines, t.name+"\x00"+string(b))
	}
	sort.Strings(lines)
	sum := sha256.Sum256([]byte(strings.Join(lines, "\n")))
	return hex.EncodeToString(sum[:]), nil
}

// registeredToolNames is the set of names in toolInputTypes, for the drift test.
func fingerprintedToolNames() map[string]bool {
	m := make(map[string]bool, len(toolInputTypes))
	for _, t := range toolInputTypes {
		m[t.name] = true
	}
	return m
}
