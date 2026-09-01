//go:build harness

package harness_test

import (
	"fmt"
	"os"
	"strconv"
	"strings"
	"testing"

	"github.com/eichler-ai/connectors/revit/test-harness/mcpclient"
)

// memcheckGate skips a diagnostic test unless MCP_HARNESS_MEMCHECK is set. Independent PR review
// finding: neither test in this file was actually gated despite this file's own doc comment (and
// the README's) claiming they are "not run as part of a normal test pass" -- an unfiltered
// `go test -tags harness ./...` executed both of them anyway, cycling real Revit documents every
// time, which contradicts their own stated purpose as opt-in, ready-made diagnostics for
// revisiting issue #31, not corpus regression tests.
func memcheckGate(t *testing.T) {
	t.Helper()
	if os.Getenv("MCP_HARNESS_MEMCHECK") == "" {
		t.Skip("skipping memcheck diagnostic: set MCP_HARNESS_MEMCHECK=1 to run it explicitly")
	}
}

// TestOpenForWritingMemoryCycles is a throwaway diagnostic, not part of the
// coverage corpus: N true cross-call cycles (create in one execute_script
// call, OpenForWriting+write in a separate one, close in a third) -- the
// exact pattern the OpenForWriting feature and its memory-safety analysis
// are about. Run with Revit's process memory sampled before and after via
// `prlctl exec ... Get-Process` externally; this test only drives the
// cycles themselves.
func TestOpenForWritingMemoryCycles(t *testing.T) {
	memcheckGate(t)
	c, instanceID, documentID := targetDocument(t)
	const cycles = 6

	for i := 0; i < cycles; i++ {
		func() {
			created := runScript(t, c, instanceID, documentID, `return Connector.CreateProjectDocument().Title;`)
			if created.Status != "success" {
				t.Fatalf("cycle %d: create failed: status=%q %s", i, created.Status, created.diag())
			}
			title := strings.TrimSpace(created.ReturnValue)
			// Independent PR review finding: a t.Fatalf on the write below used to skip
			// closeFixtureDocument entirely, leaking the just-created document -- in the one test whose
			// whole purpose is measuring document-cycle memory, a leaked-on-failure document would
			// corrupt every later sample in the same run. Deferred within this per-iteration closure
			// (not the outer loop, which would keep every cycle's document open until the whole test
			// returns, defeating the actual "close between cycles" pattern being measured) so it always
			// runs once this cycle's create succeeded, regardless of what happens to the write.
			defer closeDocumentByTitle(t, c, instanceID, documentID, title, "")

			written := runScript(t, c, instanceID, documentID, fixtureWritePreamble(title)+
				fmt.Sprintf("var level = Autodesk.Revit.DB.Level.Create(doc, %d.0);\nreturn level != null;\n", 10+i))
			if written.Status != "success" {
				t.Fatalf("cycle %d: write failed: status=%q %s", i, written.Status, written.diag())
			}
		}()
	}
}

// TestOpenDocumentCount reports how many documents Application.Documents
// currently holds -- diagnostic, to distinguish "documents that should have
// been closed are still open" from "memory grew despite documents actually
// being closed" while investigating the memory-cycle numbers.
func TestOpenDocumentCount(t *testing.T) {
	memcheckGate(t)
	c, instanceID, documentID := targetDocument(t)

	out := runScript(t, c, instanceID, documentID, `
var titles = new System.Collections.Generic.List<string>();
foreach (Autodesk.Revit.DB.Document d in UIApplication.Application.Documents) { titles.Add(d.Title); }
return string.Join(", ", titles) + " (count=" + titles.Count + ")";
`)
	if out.Status != "success" {
		t.Fatalf("status=%q %s", out.Status, out.diag())
	}
	t.Logf("open documents: %s", out.diag())
}

// TestMemoryRecycleExperiment is the zero-production-code #31 experiment (Fable
// cross-check, this session): from a clean Revit restart, run several BATCHES of
// create/write/close cycles and sample the Revit PROCESS's memory after each,
// with a GC+purge injected between the last two batches. It answers three
// questions in one session:
//
//   - BOUNDED vs UNBOUNDED: does Private-memory growth per batch DECAY (freed
//     memory recycled by later cycles -> plateau) or stay linear (a real leak)?
//     Private, not WorkingSet, is the headline signal -- WorkingSet is OS-trimmed
//     and noisy.
//   - DOES GC+PURGE RECYCLE: is the batch-3 slope smaller than batch-2's after
//     `GC.Collect; WaitForPendingFinalizers; GC.Collect; PurgeReleasedAPIObjects`?
//     #31's inline purge never forced a GC first, so at that moment the script's
//     wrappers (in a collectible ALC whose Unload only INITIATES collection) were
//     not yet released and purge had nothing to drain -- this is the untested
//     variant.
//   - MANAGED vs NATIVE: GC.GetTotalMemory separates the connector's managed heap
//     from Revit's native document-model memory, so "is any of this ours" is
//     answered every sample without a profiler.
//
// Diagnostic only (gated by MCP_HARNESS_MEMCHECK); reads the samples out via
// t.Logf -- run with -v and read the MEMSAMPLE lines. Batches/cycles are
// env-overridable (MEMCHECK_BATCHES, MEMCHECK_CYCLES) so the slope can be
// resolved at higher N without recompiling.
func TestMemoryRecycleExperiment(t *testing.T) {
	memcheckGate(t)
	c, instanceID, documentID := targetDocument(t)

	batches := envIntOr("MEMCHECK_BATCHES", 3)
	cyclesPerBatch := envIntOr("MEMCHECK_CYCLES", 6)
	t.Logf("MEMEXP: %d batches x %d cycles, GC+purge injected before the final batch", batches, cyclesPerBatch)

	sampleMemory(t, c, instanceID, documentID, "baseline")
	for b := 1; b <= batches; b++ {
		// GC+purge immediately before the FINAL batch, so batch-N's slope can be
		// compared against batch-(N-1)'s to isolate whether reclamation recycles.
		if b == batches && batches >= 2 {
			gcAndPurge(t, c, instanceID, documentID)
			sampleMemory(t, c, instanceID, documentID, "after-gc-purge (pre-final-batch)")
		}
		for i := 0; i < cyclesPerBatch; i++ {
			runMemoryCycle(t, c, instanceID, documentID)
		}
		sampleMemory(t, c, instanceID, documentID, fmt.Sprintf("after-batch-%d", b))
	}
}

// sampleMemory reads the Revit process's own memory from INSIDE Revit (the
// add-in runs in-process), so it samples the exact process under test with no
// external Get-Process round trip. Refresh() defeats the cached-counter trap.
func sampleMemory(t *testing.T, c *mcpclient.Client, instanceID, documentID, label string) {
	t.Helper()
	out := runScript(t, c, instanceID, documentID, `
var p = System.Diagnostics.Process.GetCurrentProcess();
p.Refresh();
int openDocs = 0;
foreach (Autodesk.Revit.DB.Document d in UIApplication.Application.Documents) { openDocs++; }
return new {
  privateMB = p.PrivateMemorySize64 / 1048576,
  workingMB = p.WorkingSet64 / 1048576,
  managedMB = System.GC.GetTotalMemory(false) / 1048576,
  openDocs = openDocs
};
`)
	if out.Status != "success" {
		t.Fatalf("memory sample %q failed: status=%q %s", label, out.Status, out.diag())
	}
	t.Logf("MEMSAMPLE [%s]: %s", label, strings.TrimSpace(out.ReturnValue))
}

// runMemoryCycle does one create/write/close, closing within the call (deferred
// in this frame, not the caller's) so each cycle's document is gone before the
// next begins -- the pattern #31 is about.
func runMemoryCycle(t *testing.T, c *mcpclient.Client, instanceID, documentID string) {
	t.Helper()
	created := runScript(t, c, instanceID, documentID, `return Connector.CreateProjectDocument().Title;`)
	if created.Status != "success" {
		t.Fatalf("cycle create failed: status=%q %s", created.Status, created.diag())
	}
	title := strings.TrimSpace(created.ReturnValue)
	defer closeDocumentByTitle(t, c, instanceID, documentID, title, "")

	written := runScript(t, c, instanceID, documentID, fixtureWritePreamble(title)+
		"var level = Autodesk.Revit.DB.Level.Create(doc, 10.0);\nreturn level != null;\n")
	if written.Status != "success" {
		t.Fatalf("cycle write failed: status=%q %s", written.Status, written.diag())
	}
}

// gcAndPurge is the untested reclamation variant: force a full GC and drain
// pending finalizers FIRST, so the API wrappers the prior cycles released become
// eligible, THEN PurgeReleasedAPIObjects to drain Revit's deferred native-release
// queue. Runs inside an ExternalEvent (valid API context). WaitForPendingFinalizers
// blocks the UI thread; if a finalizer ever needed it this would hang, so the
// harness timeout is the backstop.
func gcAndPurge(t *testing.T, c *mcpclient.Client, instanceID, documentID string) {
	t.Helper()
	out := runScript(t, c, instanceID, documentID, `
System.GC.Collect();
System.GC.WaitForPendingFinalizers();
System.GC.Collect();
UIApplication.Application.PurgeReleasedAPIObjects();
return "purged";
`)
	if out.Status != "success" {
		t.Fatalf("GC+purge failed: status=%q %s", out.Status, out.diag())
	}
}

// envIntOr reads a positive int from an env var, falling back to def.
func envIntOr(name string, def int) int {
	if v := os.Getenv(name); v != "" {
		if n, err := strconv.Atoi(v); err == nil && n > 0 {
			return n
		}
	}
	return def
}

// TestLiveManagedHeapAfterHardGC disambiguates the managed-heap reading from
// TestMemoryRecycleExperiment (which used GC.GetTotalMemory(false), garbage
// included). A hard collect + WaitForPendingFinalizers + GetTotalMemory(true)
// reports the TRUE LIVE managed heap: if it is still large, the connector is
// rooting managed memory (our bug, actionable); if it drops back near baseline,
// the per-batch managed numbers were just uncollected garbage and the real
// growth is native (Revit's document model). Run right after the experiment,
// same session, no restart.
func TestLiveManagedHeapAfterHardGC(t *testing.T) {
	memcheckGate(t)
	c, instanceID, documentID := targetDocument(t)
	out := runScript(t, c, instanceID, documentID, `
System.GC.Collect();
System.GC.WaitForPendingFinalizers();
System.GC.Collect();
long liveManagedMB = System.GC.GetTotalMemory(true) / 1048576;
UIApplication.Application.PurgeReleasedAPIObjects();
var p = System.Diagnostics.Process.GetCurrentProcess(); p.Refresh();
return new {
  liveManagedMB,
  privateMB = p.PrivateMemorySize64 / 1048576,
  workingMB = p.WorkingSet64 / 1048576,
  gen2Collections = System.GC.CollectionCount(2)
};
`)
	if out.Status != "success" {
		t.Fatalf("status=%q %s", out.Status, out.diag())
	}
	t.Logf("LIVEHEAP: %s", strings.TrimSpace(out.ReturnValue))
}

// TestLoadedAssemblyProbe distinguishes the two candidate roots of the ~2.3GB
// live managed retention: (a) per-run collectible ALCs NOT unloading in the live
// Revit process (their script assemblies would accumulate -> loadedAssemblies
// large), vs (b) the retention living in rooted managed state that is NOT a
// per-run assembly (e.g. the compilation cache's Script<object>/Compilation
// graph). A freshly-started Revit loads a few hundred assemblies; script
// submission assemblies unloaded with their ALC drop off this list.
func TestLoadedAssemblyProbe(t *testing.T) {
	memcheckGate(t)
	c, instanceID, documentID := targetDocument(t)
	out := runScript(t, c, instanceID, documentID, `
System.GC.Collect();
System.GC.WaitForPendingFinalizers();
System.GC.Collect();
var asms = System.AppDomain.CurrentDomain.GetAssemblies();
int total = asms.Length;
int dynamic = 0, submission = 0;
foreach (var a in asms) {
  if (a.IsDynamic) dynamic++;
  var n = a.GetName().Name;
  if (n != null && (n.StartsWith("ℛ") || n.Contains("Submission") || n.StartsWith("mcpbridge-script"))) submission++;
}
return new {
  loadedAssemblies = total,
  dynamicAssemblies = dynamic,
  scriptSubmissionAssemblies = submission,
  liveManagedMB = System.GC.GetTotalMemory(true) / 1048576
};
`)
	if out.Status != "success" {
		t.Fatalf("status=%q %s", out.Status, out.diag())
	}
	t.Logf("ASMPROBE: %s", strings.TrimSpace(out.ReturnValue))
}
