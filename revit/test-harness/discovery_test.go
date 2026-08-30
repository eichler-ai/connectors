//go:build harness

// TestDescribeFunctionDisambiguation is the tier-2 coverage for the
// describe_function MCP tool (PRD §08), which had none before this file --
// list_functions/search_functions/describe_function appeared only in
// comments elsewhere in this suite. describe_function crosses the
// broker<->add-in wire the same way execute_script does, and this repo has
// already been bitten once by the Go broker's structs silently dropping
// fields the add-in sent (json.Unmarshal into a stale shape, no error, empty
// results -- see this package's own change history) -- so this is worth
// pinning live, not just at the mcp-server unit-test tier.
//
// This specifically pins issue #64's contract change: overload_index is
// gone, member is now OPTIONAL when member_id is given, and member_id alone
// must resolve the EXACT overload it names -- not always the first one in
// the list. That last part is the whole point of the change, and is
// TestDescribeFunctionDisambiguation/MemberIdAloneResolvesThatExactOverload's
// job to prove: it would fail if member were still silently required, and it
// deliberately resolves BOTH overloads from the disambiguation list (not
// just one) so a broker/add-in bug that always answers with the first
// overload regardless of which member_id was sent cannot pass by accident.
package harness_test

import (
	"encoding/json"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/test-harness/mcpclient"
)

// describeFunctionResult mirrors mcpserver.DescribeFunctionOut's success
// shape. Result is deliberately a flexible map, matching the broker's own
// struct (PRD §08 gives describe_function two genuinely different response
// shapes -- one resolved member, or a disambiguation overloads[] list --
// passed through as map[string]any rather than a fixed struct).
type describeFunctionResult struct {
	Result       map[string]any `json:"result,omitempty"`
	RevitVersion string         `json:"revit_version,omitempty"`
}

// describeFunctionRejection mirrors the PRD §01 diagnostic record a refused
// describe_function call comes back as -- structurally the same
// {"error":{...}} envelope execute_script's rejectedScript type (in
// harness_test.go) reads, just without that type's Output field, which is
// specific to a script run and does not exist here.
type describeFunctionRejection struct {
	Text  string
	Error struct {
		Code    string   `json:"code"`
		Message string   `json:"message"`
		Remedy  []string `json:"remedy"`
	} `json:"error"`
}

// callDescribeFunction issues one describe_function call and returns the raw
// tool result, success or error alike -- the caller decides how to decode it,
// same division of labor as callExecuteScript/callExecuteScriptWith.
func callDescribeFunction(t *testing.T, c *mcpclient.Client, args map[string]any) json.RawMessage {
	t.Helper()
	raw, err := c.CallTool("describe_function", args, 15*time.Second)
	if err != nil {
		t.Fatalf("describe_function: %v", err)
	}
	return raw
}

// describeFunctionSuccess decodes a describe_function call expected to
// SUCCEED, via the shared decodeToolResult generic (harness_test.go) --
// which itself fatals with the tool's own error text if the call was
// unexpectedly rejected, so a caller here never needs its own IsError check.
func describeFunctionSuccess(t *testing.T, raw json.RawMessage) describeFunctionResult {
	t.Helper()
	return decodeToolResult[describeFunctionResult](t, raw)
}

// describeFunctionRejectionOf decodes a describe_function call expected to be
// REJECTED. Mirrors rejectionOf (harness_test.go) exactly, just against
// describeFunctionRejection's narrower shape -- describe_function's error
// envelope has no Output field to carry.
func describeFunctionRejectionOf(t *testing.T, raw json.RawMessage) describeFunctionRejection {
	t.Helper()
	var tr toolResult
	if err := json.Unmarshal(raw, &tr); err != nil {
		t.Fatalf("decode tool envelope: %v\nraw: %s", err, raw)
	}
	if !tr.IsError {
		t.Fatalf("describe_function was expected to be rejected but the call succeeded: %s", raw)
	}
	if len(tr.Content) == 0 {
		t.Fatalf("rejection carried no content at all, so nothing tells an agent what happened: %s", raw)
	}
	out := describeFunctionRejection{Text: tr.Content[0].Text}
	if err := json.Unmarshal([]byte(tr.Content[0].Text), &out); err != nil {
		t.Fatalf("rejection text is not the PRD §01 record it is supposed to be: %v\ntext: %s", err, tr.Content[0].Text)
	}
	return out
}

// TestDescribeFunctionDisambiguation covers describe_function's member/
// member_id disambiguation contract end to end against a live Revit
// instance, using Autodesk.Revit.DB.Grid.Create as the overloaded member --
// it has exactly two live overloads (one taking a Line, one an Arc).
func TestDescribeFunctionDisambiguation(t *testing.T) {
	c, instances := startClient(t)
	instanceID := instances.Instances[0].InstanceID

	// Collected in the first subtest, consumed by the second -- subtests in a
	// Go test function run sequentially by default (none of these call
	// t.Parallel()), so this ordering is safe.
	var overloadMemberIDs []string
	var overloadSignatures []string

	t.Run("OverloadListCarriesMemberIds", func(t *testing.T) {
		out := describeFunctionSuccess(t, callDescribeFunction(t, c, map[string]any{
			"instance_id": instanceID,
			"member":      "Autodesk.Revit.DB.Grid.Create",
		}))

		overloadsRaw, ok := out.Result["overloads"]
		if !ok {
			t.Fatalf("expected an overloads[] disambiguation list for Grid.Create (a member known to be overloaded live), got a resolved single member instead: %+v", out.Result)
		}
		overloads, ok := overloadsRaw.([]any)
		if !ok {
			t.Fatalf("result.overloads is not an array: %+v", overloadsRaw)
		}
		// Never assert something that passes vacuously: an empty or
		// single-entry list here would mean Grid.Create is not overloaded on
		// this Revit version the way the test assumes, and that must be a
		// clear, named failure -- not a silently-satisfied ">= 2" that never
		// actually ran the real check.
		if len(overloads) < 2 {
			t.Fatalf("expected Grid.Create to have >= 2 live overloads (one Line, one Arc); found %d: %+v", len(overloads), overloads)
		}

		for i, raw := range overloads {
			entry, ok := raw.(map[string]any)
			if !ok {
				t.Fatalf("overloads[%d] is not an object: %+v", i, raw)
			}
			memberID, _ := entry["member_id"].(string)
			signature, _ := entry["signature"].(string)
			if memberID == "" {
				t.Errorf("overloads[%d] has an empty member_id, so it could never be used to disambiguate: %+v", i, entry)
			}
			if signature == "" {
				t.Errorf("overloads[%d] has an empty signature: %+v", i, entry)
			}
			overloadMemberIDs = append(overloadMemberIDs, memberID)
			overloadSignatures = append(overloadSignatures, signature)
		}
	})

	// THE CORE ASSERTION OF THIS FILE. issue #64 made member_id alone
	// sufficient to resolve one specific overload; before that fix, member
	// was required and this call shape either errored or was not
	// expressible at all. Run for BOTH overloads collected above, not just
	// one -- a broker/add-in bug that always resolves the first overload
	// regardless of which member_id was requested would still pass a
	// single-overload version of this check, and that is exactly the bug
	// class #64 fixed.
	t.Run("MemberIdAloneResolvesThatExactOverload", func(t *testing.T) {
		if len(overloadMemberIDs) < 2 {
			t.Fatalf("prerequisite subtest OverloadListCarriesMemberIds did not collect >= 2 member_ids (got %d) -- cannot exercise disambiguation for both overloads", len(overloadMemberIDs))
		}

		for i, memberID := range overloadMemberIDs {
			t.Run(memberID, func(t *testing.T) {
				// Deliberately NO "member" key at all in the params -- not an
				// empty string, ABSENT -- since #64's fix is specifically that
				// member is optional, not merely allowed to be blank.
				out := describeFunctionSuccess(t, callDescribeFunction(t, c, map[string]any{
					"instance_id": instanceID,
					"member_id":   memberID,
				}))

				if _, isOverloadList := out.Result["overloads"]; isOverloadList {
					t.Fatalf("member_id alone still returned an overloads[] disambiguation list instead of resolving to one member: %+v", out.Result)
				}
				gotMemberID, _ := out.Result["member_id"].(string)
				if gotMemberID != memberID {
					t.Fatalf("member_id alone resolved to the WRONG overload: requested %q, got %q -- result: %+v (this is precisely the bug issue #64 fixed: member_id must select the specific overload it names, not always the first one)", memberID, gotMemberID, out.Result)
				}
				gotSignature, _ := out.Result["signature"].(string)
				if gotSignature == "" {
					t.Errorf("resolved member has no signature: %+v", out.Result)
				}
				if gotSignature != overloadSignatures[i] {
					t.Errorf("resolved member's signature %q does not match what the overload list itself reported for this member_id (%q) -- result: %+v", gotSignature, overloadSignatures[i], out.Result)
				}
			})
		}
	})

	// Neither member nor member_id given at all -- must be a genuine tool
	// error, not a success with empty/nil data.
	t.Run("NeitherMemberNorMemberIdIsAnError", func(t *testing.T) {
		rej := describeFunctionRejectionOf(t, callDescribeFunction(t, c, map[string]any{
			"instance_id": instanceID,
		}))
		// The harness's helpers expose the full PRD §01 record (code,
		// message, remedy), same as execute_script's rejections elsewhere in
		// this suite, so assert on the field an agent is told to key off
		// (skill.md), not just on IsError being true.
		if rej.Error.Code != "missing-required-param" {
			t.Errorf("record's code = %q, want missing-required-param; result: %s", rej.Error.Code, rej.Text)
		}
	})
}
