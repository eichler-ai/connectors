package registry

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/hub/protocol"
)

type fakeLink struct {
	sent   []protocol.Message
	closed string
}

func (l *fakeLink) Send(_ context.Context, m protocol.Message) error {
	l.sent = append(l.sent, m)
	return nil
}
func (l *fakeLink) Close(reason string) { l.closed = reason }

func bridge(user, instance string, since time.Time) *Bridge {
	return &Bridge{Key: Key{UserID: user, Connector: "excel", InstanceID: instance}, Since: since, Link: &fakeLink{}}
}

func TestNewestWins(t *testing.T) {
	r := New()
	old := bridge("u", "i", time.Now())
	if got := r.Register(old, nil); got != nil {
		t.Fatalf("first register replaced %v", got)
	}
	fresh := bridge("u", "i", time.Now())
	if got := r.Register(fresh, nil); got != old {
		t.Fatalf("second register replaced %v, want the old one", got)
	}
	if b, _ := r.Get("u", "excel", "i"); b != fresh {
		t.Fatal("newest is not live")
	}
	// The loser's teardown must not remove the winner.
	if r.Unregister(old) {
		t.Fatal("stale connection unregistered the live one")
	}
	if b, ok := r.Get("u", "excel", "i"); !ok || b != fresh {
		t.Fatal("winner gone after loser's unregister")
	}
	if !r.Unregister(fresh) {
		t.Fatal("live connection could not unregister itself")
	}
}

func TestSendChecksLiveness(t *testing.T) {
	r := New()
	old := bridge("u", "i", time.Now())
	r.Register(old, nil)
	fresh := bridge("u", "i", time.Now())
	r.Register(fresh, nil)
	msg := protocol.New(protocol.MethodPing, protocol.Ping{})
	if err := r.Send(context.Background(), old, msg); !errors.Is(err, ErrNotRegistered) {
		t.Fatalf("send to replaced connection: %v", err)
	}
	if err := r.Send(context.Background(), fresh, msg); err != nil {
		t.Fatal(err)
	}
	if l := fresh.Link.(*fakeLink); len(l.sent) != 1 || l.sent[0].Method != protocol.MethodPing {
		t.Fatalf("sent: %+v", l.sent)
	}
}

func TestListIsPerUserAndOrdered(t *testing.T) {
	r := New()
	t0 := time.Now()
	r.Register(bridge("u", "b", t0.Add(time.Second)), nil)
	r.Register(bridge("u", "a", t0), nil)
	r.Register(bridge("other", "c", t0), nil)
	r.Register(&Bridge{Key: Key{UserID: "u", Connector: "figma", InstanceID: "f"}, Since: t0, Link: &fakeLink{}}, nil)
	got := r.List("u", "excel")
	if len(got) != 2 || got[0].InstanceID != "a" || got[1].InstanceID != "b" {
		t.Fatalf("list: %+v", got)
	}
	if len(r.List("nobody", "excel")) != 0 {
		t.Fatal("unknown user has bridges")
	}
}

func TestDocuments(t *testing.T) {
	r := New()
	b := bridge("u", "i", time.Now())
	r.Register(b, []protocol.Document{{ID: "1", Title: "One", Active: true}})
	if d, ok := b.Document(""); !ok || d.ID != "1" {
		t.Fatalf("active document: %+v %v", d, ok)
	}
	r.UpdateDocuments(b, []protocol.Document{{ID: "2", Title: "Two", Active: true}, {ID: "3"}})
	if d, ok := b.Document("3"); !ok || d.ID != "3" {
		t.Fatal("document by id")
	}
	if _, ok := b.Document("1"); ok {
		t.Fatal("stale document still present")
	}
	docs := b.Documents()
	docs[0].Title = "mutated"
	if b.Documents()[0].Title != "Two" {
		t.Fatal("Documents returned the internal slice")
	}
}
