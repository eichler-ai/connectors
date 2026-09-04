package singleton

import (
	"errors"
	"os"
	"path/filepath"
	"testing"
)

// Holding a lock from a second AcquireLock call in the same process conflicts
// (flock and LockFileEx are both per open file description / handle), which is
// what lets these tests stand in for a second process that never releases.

func writeBroker(t *testing.T, dir string, pid int) {
	t.Helper()
	if err := WriteBrokerJSON(dir, BrokerInfo{Host: "127.0.0.1", Port: 1, PID: pid, Token: "t"}); err != nil {
		t.Fatal(err)
	}
}

func TestTakeOverClaimsGeneration1WhenBaseLockHolderHasExited(t *testing.T) {
	dir := t.TempDir()
	corpse, got, err := AcquireLock(LockPath(dir))
	if err != nil || !got {
		t.Fatalf("base lock: got=%v err=%v", got, err)
	}
	defer corpse.Release()
	writeBroker(t, dir, 4242)

	dead := func(int) bool { return false }
	l, gen, err := TakeOver(dir, 4242, dead, 0)
	if err != nil {
		t.Fatalf("TakeOver: %v", err)
	}
	defer l.Release()
	if gen != 1 {
		t.Fatalf("generation = %d, want 1", gen)
	}
	if _, err := os.Stat(GenerationLockPath(dir, 1)); err != nil {
		t.Fatalf("broker.lock.1 should exist: %v", err)
	}
	// The takeover lock is released again once the generation is held.
	tl, got, err := AcquireLock(filepath.Join(dir, takeoverLockFile))
	if err != nil || !got {
		t.Fatalf("takeover lock should be free afterwards: got=%v err=%v", got, err)
	}
	tl.Release()
}

func TestTakeOverReturnsBaseLockWhenItIsFreeAfterAll(t *testing.T) {
	dir := t.TempDir()
	writeBroker(t, dir, 4242)
	l, gen, err := TakeOver(dir, 4242, func(int) bool { return false }, 0)
	if err != nil {
		t.Fatalf("TakeOver: %v", err)
	}
	defer l.Release()
	if gen != 0 {
		t.Fatalf("generation = %d, want 0 (base lock was free)", gen)
	}
}

func TestTakeOverSkipsHeldGenerations(t *testing.T) {
	dir := t.TempDir()
	var held []*Lock
	for n := 0; n <= 2; n++ {
		l, got, err := AcquireLock(GenerationLockPath(dir, n))
		if err != nil || !got {
			t.Fatalf("gen %d: got=%v err=%v", n, got, err)
		}
		held = append(held, l)
	}
	defer func() {
		for _, l := range held {
			l.Release()
		}
	}()
	writeBroker(t, dir, 4242)

	l, gen, err := TakeOver(dir, 4242, func(int) bool { return false }, 0)
	if err != nil {
		t.Fatalf("TakeOver: %v", err)
	}
	defer l.Release()
	if gen != 3 {
		t.Fatalf("generation = %d, want 3", gen)
	}
}

func TestTakeOverGivesUpPastMaxGenerations(t *testing.T) {
	dir := t.TempDir()
	var held []*Lock
	for n := 0; n <= MaxLockGenerations; n++ {
		l, got, err := AcquireLock(GenerationLockPath(dir, n))
		if err != nil || !got {
			t.Fatalf("gen %d: got=%v err=%v", n, got, err)
		}
		held = append(held, l)
	}
	defer func() {
		for _, l := range held {
			l.Release()
		}
	}()
	writeBroker(t, dir, 4242)

	l, _, err := TakeOver(dir, 4242, func(int) bool { return false }, 0)
	if !errors.Is(err, ErrNoFreeGeneration) {
		t.Fatalf("err = %v, want ErrNoFreeGeneration", err)
	}
	if l != nil {
		t.Fatalf("no lock expected on give-up")
	}
}

func TestTakeOverAbortsWhenBrokerJSONNamesALivePrimary(t *testing.T) {
	dir := t.TempDir()
	corpse, got, err := AcquireLock(LockPath(dir))
	if err != nil || !got {
		t.Fatalf("base lock: got=%v err=%v", got, err)
	}
	defer corpse.Release()
	// The caller saw pid 4242 dead; by the time the takeover lock is held a
	// live primary (this test process) has written broker.json.
	writeBroker(t, dir, os.Getpid())

	l, _, err := TakeOver(dir, 4242, ProcessAlive, 0)
	if !errors.Is(err, ErrHolderAlive) {
		t.Fatalf("err = %v, want ErrHolderAlive", err)
	}
	if l != nil {
		t.Fatalf("no lock expected when the holder is alive")
	}
}

func TestTakeOverRechecksTheDeadPIDUnderTheLock(t *testing.T) {
	dir := t.TempDir()
	corpse, got, err := AcquireLock(LockPath(dir))
	if err != nil || !got {
		t.Fatalf("base lock: got=%v err=%v", got, err)
	}
	defer corpse.Release()
	writeBroker(t, dir, 4242)

	// broker.json's pid reads as dead but the caller's pid reads alive: the
	// caller's earlier judgement was wrong, so no takeover.
	calls := 0
	alive := func(pid int) bool {
		calls++
		return calls > 1
	}
	l, _, err := TakeOver(dir, 4242, alive, 0)
	if !errors.Is(err, ErrHolderAlive) {
		t.Fatalf("err = %v, want ErrHolderAlive", err)
	}
	if l != nil {
		t.Fatalf("no lock expected")
	}
}

func TestTakeOverRefusesWhileAnotherTakeoverRuns(t *testing.T) {
	dir := t.TempDir()
	tl, got, err := AcquireLock(filepath.Join(dir, takeoverLockFile))
	if err != nil || !got {
		t.Fatalf("takeover lock: got=%v err=%v", got, err)
	}
	defer tl.Release()
	writeBroker(t, dir, 4242)

	l, _, err := TakeOver(dir, 4242, func(int) bool { return false }, 0)
	if !errors.Is(err, ErrTakeoverInProgress) {
		t.Fatalf("err = %v, want ErrTakeoverInProgress", err)
	}
	if l != nil {
		t.Fatalf("no lock expected")
	}
}

func TestReleaseStaleGenerationsRemovesOnlyUnheldFiles(t *testing.T) {
	dir := t.TempDir()
	for _, n := range []int{1, 2, 3} {
		if err := os.WriteFile(GenerationLockPath(dir, n), nil, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	held, got, err := AcquireLock(GenerationLockPath(dir, 2))
	if err != nil || !got {
		t.Fatalf("gen 2: got=%v err=%v", got, err)
	}
	defer held.Release()

	if removed := ReleaseStaleGenerations(dir); removed != 2 {
		t.Fatalf("removed = %d, want 2", removed)
	}
	if _, err := os.Stat(GenerationLockPath(dir, 2)); err != nil {
		t.Fatalf("held generation must survive: %v", err)
	}
	for _, n := range []int{1, 3} {
		if _, err := os.Stat(GenerationLockPath(dir, n)); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("gen %d should be gone, stat err = %v", n, err)
		}
	}
	if removed := ReleaseStaleGenerations(dir); removed != 0 {
		t.Fatalf("second sweep removed %d, want 0", removed)
	}
}

func TestProcessAliveOnSelfAndOnAnImpossiblePID(t *testing.T) {
	if !ProcessAlive(os.Getpid()) {
		t.Fatal("the test process must read as alive")
	}
	if ProcessAlive(0) || ProcessAlive(-1) {
		t.Fatal("non-positive pids are never alive")
	}
}
