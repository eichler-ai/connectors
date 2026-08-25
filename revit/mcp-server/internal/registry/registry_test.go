package registry

import "testing"

func TestRegisterAndGet(t *testing.T) {
	r := New()
	inst := &Instance{
		InstanceID:   "inst-1",
		PID:          1234,
		RevitVersion: "2027",
		Documents: []Document{
			{ID: "doc-abc", Title: "Sample.rvt", Active: true},
		},
	}
	r.Register(inst)

	got, ok := r.Get("inst-1")
	if !ok {
		t.Fatalf("Get: instance not found after Register")
	}
	if got.PID != 1234 || got.RevitVersion != "2027" {
		t.Errorf("Get returned wrong data: %+v", got)
	}
	if len(got.Documents) != 1 || got.Documents[0].ID != "doc-abc" {
		t.Errorf("Documents not preserved: %+v", got.Documents)
	}
}

func TestGetUnknownInstance(t *testing.T) {
	r := New()
	_, ok := r.Get("does-not-exist")
	if ok {
		t.Fatalf("Get should report not-found for an unregistered instance")
	}
}

func TestRegisterOverwritesExistingInstance(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1", PID: 1, RevitVersion: "2027"})
	r.Register(&Instance{InstanceID: "inst-1", PID: 2, RevitVersion: "2027", Documents: []Document{{ID: "doc-x"}}})

	got, ok := r.Get("inst-1")
	if !ok {
		t.Fatalf("Get: not found")
	}
	if got.PID != 2 {
		t.Errorf("re-register should overwrite PID, got %d", got.PID)
	}
	if len(got.Documents) != 1 {
		t.Errorf("re-register should overwrite Documents, got %+v", got.Documents)
	}
}

func TestRemove(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"})
	r.Remove("inst-1")
	if _, ok := r.Get("inst-1"); ok {
		t.Fatalf("instance should be gone after Remove")
	}
	// Remove of an already-absent instance must not panic or error.
	r.Remove("inst-1")
}

func TestList(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"})
	r.Register(&Instance{InstanceID: "inst-2"})

	list := r.List()
	if len(list) != 2 {
		t.Fatalf("List() len = %d, want 2", len(list))
	}
	ids := map[string]bool{}
	for _, i := range list {
		ids[i.InstanceID] = true
	}
	if !ids["inst-1"] || !ids["inst-2"] {
		t.Errorf("List() missing expected instances: %+v", list)
	}
}

func TestRegisterDeepCopiesDocuments(t *testing.T) {
	r := New()
	docs := []Document{{ID: "doc-a"}}
	r.Register(&Instance{InstanceID: "inst-1", Documents: docs})

	docs[0].ID = "mutated"

	got, _ := r.Get("inst-1")
	if got.Documents[0].ID != "doc-a" {
		t.Errorf("registry should not alias caller's slice; got %+v", got.Documents)
	}
}
