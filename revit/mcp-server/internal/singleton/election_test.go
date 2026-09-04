package singleton

import (
	"context"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sync"
	"testing"
	"time"
)

// Holding a lock from a second AcquireLock call in the same process conflicts
// (flock is per open file description, LockFileEx per handle), which is what
// lets these tests stand in for a second process that never releases.

func holdAs(t *testing.T, path string, pid int) *Lock {
	t.Helper()
	l, got, err := AcquireLock(path)
	if err != nil || !got {
		t.Fatalf("hold %s: got=%v err=%v", filepath.Base(path), got, err)
	}
	if pid > 0 {
		if err := l.recordHolder(pid); err != nil {
			t.Fatal(err)
		}
	}
	t.Cleanup(func() { l.Release() })
	return l
}

var dead = func(int) bool { return false }
var live = func(int) bool { return true }

func TestElectWinsGeneration0AndRecordsItself(t *testing.T) {
	dir := t.TempDir()
	e, err := Elect(context.Background(), dir, dead)
	if err != nil {
		t.Fatalf("Elect: %v", err)
	}
	defer e.Lock.Release()
	if !e.Primary || e.Generation != 0 || len(e.SkippedDeadHolders) != 0 {
		t.Fatalf("election = %+v, want primary on generation 0", e)
	}
	pid, ok := readHolder(LockPath(dir))
	if !ok || pid != os.Getpid() {
		t.Fatalf("recorded holder = %d/%v, want %d", pid, ok, os.Getpid())
	}
	// The election mutex is free again afterwards.
	m, got, err := AcquireLock(filepath.Join(dir, electionLockFile))
	if err != nil || !got {
		t.Fatalf("election mutex should be free: got=%v err=%v", got, err)
	}
	m.Release()
}

func TestElectStepsOverAnExitedHolderOntoGeneration1(t *testing.T) {
	dir := t.TempDir()
	holdAs(t, LockPath(dir), 4242)

	e, err := Elect(context.Background(), dir, dead)
	if err != nil {
		t.Fatalf("Elect: %v", err)
	}
	defer e.Lock.Release()
	if !e.Primary || e.Generation != 1 {
		t.Fatalf("election = %+v, want primary on generation 1", e)
	}
	if len(e.SkippedDeadHolders) != 1 || e.SkippedDeadHolders[0] != 4242 {
		t.Fatalf("skipped = %v, want [4242]", e.SkippedDeadHolders)
	}
	if pid, ok := readHolder(GenerationLockPath(dir, 1)); !ok || pid != os.Getpid() {
		t.Fatalf("generation 1 holder = %d/%v", pid, ok)
	}
}

func TestElectIsSecondaryBehindALiveHolder(t *testing.T) {
	dir := t.TempDir()
	holdAs(t, LockPath(dir), 4242)
	e, err := Elect(context.Background(), dir, live)
	if err != nil {
		t.Fatalf("Elect: %v", err)
	}
	if e.Primary || e.Lock != nil {
		t.Fatalf("election = %+v, want secondary", e)
	}
	if _, err := os.Stat(GenerationLockPath(dir, 1)); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("no generation file should be created behind a live holder: %v", err)
	}
}

func TestElectTreatsAnUnrecordedHolderAsLive(t *testing.T) {
	// A lock held by a build that predates the holder record (or a holder
	// between acquiring and recording) is never stepped over.
	dir := t.TempDir()
	holdAs(t, LockPath(dir), 0)
	e, err := Elect(context.Background(), dir, dead)
	if err != nil {
		t.Fatalf("Elect: %v", err)
	}
	if e.Primary {
		t.Fatalf("must not take over from an unrecorded holder: %+v", e)
	}
}

func TestElectIsSecondaryWhenADeadBaseHolderIsFollowedByALiveGeneration(t *testing.T) {
	dir := t.TempDir()
	holdAs(t, LockPath(dir), 4242)              // corpse
	holdAs(t, GenerationLockPath(dir, 1), 4343) // the takeover primary, alive
	alive := func(pid int) bool { return pid == 4343 }
	e, err := Elect(context.Background(), dir, alive)
	if err != nil {
		t.Fatalf("Elect: %v", err)
	}
	if e.Primary {
		t.Fatalf("election = %+v, want secondary behind the live generation-1 primary", e)
	}
}

func TestElectGivesUpWhenEveryGenerationIsDead(t *testing.T) {
	dir := t.TempDir()
	for n := 0; n <= MaxLockGenerations; n++ {
		holdAs(t, GenerationLockPath(dir, n), 1000+n)
	}
	_, err := Elect(context.Background(), dir, dead)
	if !errors.Is(err, ErrNoFreeGeneration) {
		t.Fatalf("err = %v, want ErrNoFreeGeneration", err)
	}
}

func TestElectReportsAnElectionInProgressWhenTheMutexStaysHeld(t *testing.T) {
	dir := t.TempDir()
	holdAs(t, filepath.Join(dir, electionLockFile), 0)
	old := electionMutexWait
	electionMutexWait = 50 * time.Millisecond
	defer func() { electionMutexWait = old }()

	_, err := Elect(context.Background(), dir, dead)
	if !errors.Is(err, ErrElectionInProgress) {
		t.Fatalf("err = %v, want ErrElectionInProgress", err)
	}
}

func TestElectHonoursContextWhileWaitingForTheMutex(t *testing.T) {
	dir := t.TempDir()
	holdAs(t, filepath.Join(dir, electionLockFile), 0)
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
	defer cancel()
	_, err := Elect(ctx, dir, dead)
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("err = %v, want context deadline", err)
	}
}

func TestElectConcurrentCandidatesProduceExactlyOnePrimary(t *testing.T) {
	dir := t.TempDir()
	holdAs(t, LockPath(dir), 4242) // corpse on generation 0
	const candidates = 8
	var wg sync.WaitGroup
	results := make([]Election, candidates)
	errs := make([]error, candidates)
	for i := 0; i < candidates; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			results[i], errs[i] = Elect(context.Background(), dir, func(pid int) bool { return pid == os.Getpid() })
		}(i)
	}
	wg.Wait()
	primaries := 0
	for i := range results {
		if errs[i] != nil {
			t.Fatalf("candidate %d: %v", i, errs[i])
		}
		if results[i].Primary {
			primaries++
			defer results[i].Lock.Release()
			if results[i].Generation != 1 {
				t.Fatalf("primary on generation %d, want 1", results[i].Generation)
			}
		}
	}
	if primaries != 1 {
		t.Fatalf("%d primaries, want exactly 1", primaries)
	}
}

func TestGeneration0WinnerRemovesOnlyUnheldGenerations(t *testing.T) {
	dir := t.TempDir()
	for _, n := range []int{1, 2, 3} {
		if err := os.WriteFile(GenerationLockPath(dir, n), nil, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	holdAs(t, GenerationLockPath(dir, 2), 4343)

	e, err := Elect(context.Background(), dir, dead)
	if err != nil {
		t.Fatalf("Elect: %v", err)
	}
	defer e.Lock.Release()
	if e.Generation != 0 || e.CleanedGenerations != 2 {
		t.Fatalf("election = %+v, want generation 0 with 2 cleaned", e)
	}
	if _, err := os.Stat(GenerationLockPath(dir, 2)); err != nil {
		t.Fatalf("held generation must survive: %v", err)
	}
	for _, n := range []int{1, 3} {
		if _, err := os.Stat(GenerationLockPath(dir, n)); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("generation %d should be gone, stat err = %v", n, err)
		}
	}
}

func TestReadHolderRoundTripAndShorterPIDOverwritesLonger(t *testing.T) {
	dir := t.TempDir()
	l := holdAs(t, LockPath(dir), 1234567)
	if pid, ok := readHolder(LockPath(dir)); !ok || pid != 1234567 {
		t.Fatalf("holder = %d/%v", pid, ok)
	}
	if err := l.recordHolder(7); err != nil {
		t.Fatal(err)
	}
	if pid, ok := readHolder(LockPath(dir)); !ok || pid != 7 {
		t.Fatalf("holder after rewrite = %d/%v, want 7", pid, ok)
	}
	if _, ok := readHolder(filepath.Join(dir, "missing")); ok {
		t.Fatal("missing file must read as unrecorded")
	}
	if err := os.WriteFile(filepath.Join(dir, "empty"), nil, 0o644); err != nil {
		t.Fatal(err)
	}
	if _, ok := readHolder(filepath.Join(dir, "empty")); ok {
		t.Fatal("empty file must read as unrecorded")
	}
}

func TestProcessAliveOnSelfAnExitedChildAndImpossiblePIDs(t *testing.T) {
	if !ProcessAlive(os.Getpid()) {
		t.Fatal("the test process must read as alive")
	}
	if ProcessAlive(0) || ProcessAlive(-1) {
		t.Fatal("non-positive pids are never alive")
	}
	if runtime.GOOS == "windows" {
		return // an exited child's pid can be reused once its handle closes; covered live on the VM
	}
	cmd := exec.Command("true")
	if err := cmd.Run(); err != nil {
		t.Skipf("cannot run a child: %v", err)
	}
	if ProcessAlive(cmd.Process.Pid) {
		t.Fatalf("exited (and reaped) child pid %d must read as dead", cmd.Process.Pid)
	}
}
