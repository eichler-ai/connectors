package discovery

import (
	"context"
	"encoding/json"
	"net"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

// fakeInstance simulates the add-in side of one instance's wire connection:
// a transport.Conn whose request handler is scriptable per test. Modeled on
// internal/execution/execution_test.go's own fakeInstance, for consistency.
type fakeInstance struct {
	conn *transport.Conn
}

func newFakeInstance(t *testing.T, handler transport.RequestHandler) (*fakeInstance, *transport.Conn) {
	t.Helper()
	brokerSide, addinSide := net.Pipe()
	brokerConn := transport.NewConn(brokerSide)
	addinConn := transport.NewConn(addinSide)

	addinConn.SetRequestHandler(handler)

	go brokerConn.Serve()
	go addinConn.Serve()
	t.Cleanup(func() {
		brokerConn.Close()
		addinConn.Close()
	})
	return &fakeInstance{conn: addinConn}, brokerConn
}

func handlerReturning(result any) transport.RequestHandler {
	return func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return result, nil
	}
}

func TestListFunctionsRoutesToExplicitInstance(t *testing.T) {
	var gotMethod string
	var gotParams map[string]any
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		gotMethod = method
		json.Unmarshal(params, &gotParams)
		return map[string]any{"members": []any{}, "total_scoped": 0}, nil
	})
	r := NewRouter(registry.New())
	r.AttachInstance("inst-1", conn)

	raw, _, drec := r.ListFunctions(context.Background(), "inst-1", map[string]any{"namespace": "Autodesk.Revit.DB"})
	if drec != nil {
		t.Fatalf("unexpected diag error: %+v", drec)
	}
	if gotMethod != "list_functions" {
		t.Errorf("wire method = %q, want list_functions", gotMethod)
	}
	if gotParams["namespace"] != "Autodesk.Revit.DB" {
		t.Errorf("params = %+v, want namespace forwarded", gotParams)
	}
	var out map[string]any
	if err := json.Unmarshal(raw, &out); err != nil {
		t.Fatalf("decoding result: %v", err)
	}
	if _, ok := out["members"]; !ok {
		t.Errorf("result missing members field: %+v", out)
	}
}

func TestSearchFunctionsRoutesToAnyInstanceDeterministically(t *testing.T) {
	var seenInstances []string

	_, connZ := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		seenInstances = append(seenInstances, "inst-z")
		return map[string]any{"results": []any{}, "total_matched": 0}, nil
	})
	_, connA := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		seenInstances = append(seenInstances, "inst-a")
		return map[string]any{"results": []any{}, "total_matched": 0}, nil
	})

	r := NewRouter(registry.New())
	// Attach in non-sorted order to prove the pick is deterministic (sorted),
	// not map-iteration-order dependent.
	r.AttachInstance("inst-z", connZ)
	r.AttachInstance("inst-a", connA)

	_, _, drec := r.SearchFunctions(context.Background(), "", map[string]any{"query": "Delete"})
	if drec != nil {
		t.Fatalf("unexpected diag error: %+v", drec)
	}
	if len(seenInstances) != 1 || seenInstances[0] != "inst-a" {
		t.Errorf("seenInstances = %v, want exactly [inst-a] (lexicographically first)", seenInstances)
	}
}

func TestDescribeFunctionNoInstanceConnected(t *testing.T) {
	r := NewRouter(registry.New())
	_, _, drec := r.DescribeFunction(context.Background(), "", map[string]any{"member": "Autodesk.Revit.DB.Document.Delete"})
	if drec == nil {
		t.Fatal("expected diag error when no instance is connected")
	}
	if drec.Code != "no_instance_connected" {
		t.Errorf("Code = %q, want no_instance_connected", drec.Code)
	}
	if drec.Source != "mcp-server.internal.discovery" {
		t.Errorf("Source = %q, want mcp-server.internal.discovery", drec.Source)
	}
}

func TestListFunctionsUnknownInstanceID(t *testing.T) {
	r := NewRouter(registry.New())
	_, conn := newFakeInstance(t, handlerReturning(map[string]any{"members": []any{}}))
	r.AttachInstance("inst-1", conn)

	_, _, drec := r.ListFunctions(context.Background(), "ghost", map[string]any{})
	if drec == nil {
		t.Fatal("expected diag error for unknown instance_id")
	}
	if drec.Code != "instance_not_found" {
		t.Errorf("Code = %q, want instance_not_found", drec.Code)
	}
	if drec.Detail["instance_id"] != "ghost" {
		t.Errorf("Detail should name the instance_id, got %+v", drec.Detail)
	}
}

func TestDescribeFunctionRPCErrorPropagatesDiagnosticData(t *testing.T) {
	addinRecord := diag.New(diag.SeverityError, "member_not_found", "mcp-bridge.core.discovery", "no member matches \"Bogus.Member\"")
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return nil, &transport.RPCError{
			Code:    transport.ErrCodeInvalidParams,
			Message: "member not found",
			Data:    addinRecord,
		}
	})
	r := NewRouter(registry.New())
	r.AttachInstance("inst-1", conn)

	_, _, drec := r.DescribeFunction(context.Background(), "inst-1", map[string]any{"member": "Bogus.Member"})
	if drec == nil {
		t.Fatal("expected diag error")
	}
	if drec.Code != "member_not_found" {
		t.Errorf("Code = %q, want the add-in's own code to pass through unwrapped", drec.Code)
	}
}

func TestListFunctionsWireFailurePropagates(t *testing.T) {
	_, conn := newFakeInstance(t, handlerReturning(map[string]any{"members": []any{}}))
	r := NewRouter(registry.New())
	r.AttachInstance("inst-1", conn)
	conn.Close() // simulate a dead wire

	_, _, drec := r.ListFunctions(context.Background(), "inst-1", map[string]any{})
	if drec == nil {
		t.Fatal("expected diag error on wire failure")
	}
	if drec.Code != "wire_call_failed" {
		t.Errorf("Code = %q, want wire_call_failed", drec.Code)
	}
}

func TestDetachInstanceRemovesFromRouting(t *testing.T) {
	_, conn := newFakeInstance(t, handlerReturning(map[string]any{"members": []any{}}))
	r := NewRouter(registry.New())
	r.AttachInstance("inst-1", conn)
	r.DetachInstance("inst-1")

	_, _, drec := r.ListFunctions(context.Background(), "inst-1", map[string]any{})
	if drec == nil || drec.Code != "instance_not_found" {
		t.Fatalf("got %+v, want instance_not_found after detach", drec)
	}

	_, _, drec2 := r.ListFunctions(context.Background(), "", map[string]any{})
	if drec2 == nil || drec2.Code != "no_instance_connected" {
		t.Fatalf("got %+v, want no_instance_connected after detach leaves the map empty", drec2)
	}
}

func TestSearchFunctionsForwardsResultUnmodified(t *testing.T) {
	// The router must pass the add-in's result JSON through unmodified —
	// including a field the router doesn't itself know about — rather than
	// decoding into a narrow struct and re-encoding, which would silently
	// drop unknown fields.
	_, conn := newFakeInstance(t, handlerReturning(map[string]any{
		"results":             []any{map[string]any{"member_id": "M:Foo", "score": 0.9}},
		"total_matched":       1,
		"an_unexpected_field": "should survive round trip",
	}))
	r := NewRouter(registry.New())
	r.AttachInstance("inst-1", conn)

	raw, _, drec := r.SearchFunctions(context.Background(), "inst-1", map[string]any{"query": "Foo"})
	if drec != nil {
		t.Fatalf("unexpected diag error: %+v", drec)
	}
	var out map[string]any
	if err := json.Unmarshal(raw, &out); err != nil {
		t.Fatalf("decoding: %v", err)
	}
	if out["an_unexpected_field"] != "should survive round trip" {
		t.Errorf("out = %+v, want unknown field preserved", out)
	}
}

func TestCallReturnsRevitVersionFromRegistry(t *testing.T) {
	_, conn := newFakeInstance(t, handlerReturning(map[string]any{"total_scoped": 0}))
	reg := registry.New()
	reg.Register(&registry.Instance{InstanceID: "inst-1", RevitVersion: "2025"}, time.Now())
	r := NewRouter(reg)
	r.AttachInstance("inst-1", conn)

	_, revitVersion, drec := r.ListFunctions(context.Background(), "inst-1", map[string]any{})
	if drec != nil {
		t.Fatalf("unexpected diag error: %+v", drec)
	}
	if revitVersion != "2025" {
		t.Errorf("revitVersion = %q, want %q", revitVersion, "2025")
	}
}

func TestUnscopedCallAutoPicksWhenAllConnectedInstancesShareOneVersion(t *testing.T) {
	_, connA := newFakeInstance(t, handlerReturning(map[string]any{"total_scoped": 0}))
	_, connB := newFakeInstance(t, handlerReturning(map[string]any{"total_scoped": 0}))
	reg := registry.New()
	reg.Register(&registry.Instance{InstanceID: "inst-a", RevitVersion: "2027"}, time.Now())
	reg.Register(&registry.Instance{InstanceID: "inst-b", RevitVersion: "2027"}, time.Now())
	r := NewRouter(reg)
	r.AttachInstance("inst-a", connA)
	r.AttachInstance("inst-b", connB)

	_, revitVersion, drec := r.ListFunctions(context.Background(), "", map[string]any{})
	if drec != nil {
		t.Fatalf("unexpected diag error for same-version instances: %+v", drec)
	}
	if revitVersion != "2027" {
		t.Errorf("revitVersion = %q, want %q", revitVersion, "2027")
	}
}

func TestUnscopedCallErrorsWhenConnectedInstancesSpanDifferentVersions(t *testing.T) {
	// PRD §11: an unscoped call across differently-versioned instances must
	// never silently pick one -- that would hand back version-specific
	// results with nothing telling the caller they're version-specific.
	_, connA := newFakeInstance(t, handlerReturning(map[string]any{"total_scoped": 0}))
	_, connB := newFakeInstance(t, handlerReturning(map[string]any{"total_scoped": 0}))
	reg := registry.New()
	reg.Register(&registry.Instance{InstanceID: "inst-a", RevitVersion: "2025"}, time.Now())
	reg.Register(&registry.Instance{InstanceID: "inst-b", RevitVersion: "2027"}, time.Now())
	r := NewRouter(reg)
	r.AttachInstance("inst-a", connA)
	r.AttachInstance("inst-b", connB)

	_, _, drec := r.ListFunctions(context.Background(), "", map[string]any{})
	if drec == nil {
		t.Fatal("want an ambiguous-instance-version error, got none")
	}
	if drec.Code != "ambiguous_instance_version" {
		t.Errorf("drec.Code = %q, want %q", drec.Code, "ambiguous_instance_version")
	}
}

func TestNewRouterPanicsOnNilRegistry(t *testing.T) {
	defer func() {
		if recover() == nil {
			t.Fatal("want NewRouter(nil) to panic, it did not")
		}
	}()
	NewRouter(nil)
}

// TestUnscopedCallVersionDegradeCases covers the unregistered/unknown-version
// cases from an unscoped (no instance_id) call: an instance attached to the
// Router but absent from the registry must never contribute a blank ("")
// entry to versionsSeen/candidates -- that overloads "" to mean both "a
// real, empty version string" and "unknown", which previously produced
// incoherent results (see unknownRevitVersion's doc comment). This table
// covers both sides of the degrade: two unregistered instances still
// auto-pick silently (single, coherent "unknown" bucket), while a
// registered instance mixed with an unregistered one is still ambiguous
// (two distinct version buckets) -- but the candidate for the unregistered
// instance now reads "unknown", never "".
func TestUnscopedCallVersionDegradeCases(t *testing.T) {
	tests := []struct {
		name          string
		registerA     bool // inst-a
		registerB     bool // inst-b
		wantErr       bool
		wantErrCode   string
		wantVersion   string
		wantCandidate map[string]string // only checked when wantErr, keyed by instance_id -> expected revit_version
	}{
		{
			name:        "both unregistered auto-picks silently",
			registerA:   false,
			registerB:   false,
			wantErr:     false,
			wantVersion: "", // resolved instance isn't in the registry, so call() reports "" (no version to stamp)
		},
		{
			name:        "one registered one unregistered is ambiguous with unknown candidate, not blank",
			registerA:   true,
			registerB:   false,
			wantErr:     true,
			wantErrCode: "ambiguous_instance_version",
			wantCandidate: map[string]string{
				"inst-a": "2027",
				"inst-b": unknownRevitVersion,
			},
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			_, connA := newFakeInstance(t, handlerReturning(map[string]any{"total_scoped": 0}))
			_, connB := newFakeInstance(t, handlerReturning(map[string]any{"total_scoped": 0}))
			reg := registry.New()
			if tc.registerA {
				reg.Register(&registry.Instance{InstanceID: "inst-a", RevitVersion: "2027"}, time.Now())
			}
			if tc.registerB {
				reg.Register(&registry.Instance{InstanceID: "inst-b", RevitVersion: "2027"}, time.Now())
			}
			r := NewRouter(reg)
			r.AttachInstance("inst-a", connA)
			r.AttachInstance("inst-b", connB)

			_, revitVersion, drec := r.ListFunctions(context.Background(), "", map[string]any{})

			if tc.wantErr {
				if drec == nil {
					t.Fatal("want a diag error, got none")
				}
				if drec.Code != tc.wantErrCode {
					t.Errorf("Code = %q, want %q", drec.Code, tc.wantErrCode)
				}
				candidates, ok := drec.Detail["candidates"].([]map[string]string)
				if !ok {
					t.Fatalf("Detail[\"candidates\"] = %+v (%T), want []map[string]string", drec.Detail["candidates"], drec.Detail["candidates"])
				}
				got := map[string]string{}
				for _, c := range candidates {
					got[c["instance_id"]] = c["revit_version"]
				}
				for id, want := range tc.wantCandidate {
					if got[id] != want {
						t.Errorf("candidate %q revit_version = %q, want %q (full candidates: %+v)", id, got[id], want, candidates)
					}
					if got[id] == "" {
						t.Errorf("candidate %q revit_version is blank -- must never be the empty string", id)
					}
				}
				return
			}

			if drec != nil {
				t.Fatalf("unexpected diag error: %+v", drec)
			}
			if revitVersion != tc.wantVersion {
				t.Errorf("revitVersion = %q, want %q", revitVersion, tc.wantVersion)
			}
		})
	}
}

// TestAmbiguousInstanceVersionErrorDetailShape asserts the candidates detail
// payload's exact shape (PRD §01 diagnostic-record shape: detail is meant to
// be read by the caller per the error's own remedy string, so its shape is
// part of the contract, not an implementation detail).
func TestAmbiguousInstanceVersionErrorDetailShape(t *testing.T) {
	_, connA := newFakeInstance(t, handlerReturning(map[string]any{"total_scoped": 0}))
	_, connB := newFakeInstance(t, handlerReturning(map[string]any{"total_scoped": 0}))
	reg := registry.New()
	reg.Register(&registry.Instance{InstanceID: "inst-a", RevitVersion: "2025"}, time.Now())
	reg.Register(&registry.Instance{InstanceID: "inst-b", RevitVersion: "2027"}, time.Now())
	r := NewRouter(reg)
	r.AttachInstance("inst-a", connA)
	r.AttachInstance("inst-b", connB)

	_, _, drec := r.ListFunctions(context.Background(), "", map[string]any{})
	if drec == nil {
		t.Fatal("want an ambiguous-instance-version error, got none")
	}

	raw, err := json.Marshal(drec.Detail)
	if err != nil {
		t.Fatalf("marshaling Detail: %v", err)
	}
	var decoded struct {
		Candidates []struct {
			InstanceID   string `json:"instance_id"`
			RevitVersion string `json:"revit_version"`
		} `json:"candidates"`
	}
	if err := json.Unmarshal(raw, &decoded); err != nil {
		t.Fatalf("Detail did not round-trip through JSON as {candidates: [{instance_id, revit_version}]}: %v", err)
	}
	if len(decoded.Candidates) != 2 {
		t.Fatalf("candidates = %+v, want exactly 2 entries", decoded.Candidates)
	}
	want := map[string]string{"inst-a": "2025", "inst-b": "2027"}
	for _, c := range decoded.Candidates {
		if c.InstanceID == "" {
			t.Errorf("candidate has empty instance_id: %+v", c)
		}
		wantVersion, ok := want[c.InstanceID]
		if !ok {
			t.Errorf("unexpected candidate instance_id %q", c.InstanceID)
			continue
		}
		if c.RevitVersion != wantVersion {
			t.Errorf("candidate %q revit_version = %q, want %q", c.InstanceID, c.RevitVersion, wantVersion)
		}
	}
}

func TestExplicitInstanceIDBypassesVersionAmbiguityCheck(t *testing.T) {
	// Naming a specific instance is never ambiguous, even with differently
	// versioned instances also connected.
	_, connA := newFakeInstance(t, handlerReturning(map[string]any{"total_scoped": 0}))
	_, connB := newFakeInstance(t, handlerReturning(map[string]any{"total_scoped": 0}))
	reg := registry.New()
	reg.Register(&registry.Instance{InstanceID: "inst-a", RevitVersion: "2025"}, time.Now())
	reg.Register(&registry.Instance{InstanceID: "inst-b", RevitVersion: "2027"}, time.Now())
	r := NewRouter(reg)
	r.AttachInstance("inst-a", connA)
	r.AttachInstance("inst-b", connB)

	_, revitVersion, drec := r.ListFunctions(context.Background(), "inst-b", map[string]any{})
	if drec != nil {
		t.Fatalf("unexpected diag error: %+v", drec)
	}
	if revitVersion != "2027" {
		t.Errorf("revitVersion = %q, want %q", revitVersion, "2027")
	}
}
