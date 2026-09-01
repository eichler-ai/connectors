using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Tests.Fakes;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

public class RoslynScriptRunnerTests
{
    // Every runner in this class is built with RevitAPI/RevitAPIUI as METADATA references. Without
    // them, ScriptGlobals' own members (Document/UIApplication/UIDocument are real Revit types as of
    // PRD §14) would not bind, and every denylist case below would fail with an ordinary CS0246 while
    // appearing to "reject" the script -- asserting nothing about the denylist at all. They cannot be
    // supplied as loaded assemblies here; see RevitApiReference's doc comment for why.
    static RoslynScriptRunnerTests()
    {
        // RoslynScriptRunner references whatever is ALREADY LOADED in this process
        // (LoadableReferences), so a `dynamic` script only compiles here if Microsoft.CSharp has been
        // loaded first -- otherwise it fails with CS0656 (missing Binder) and the late-bound denylist
        // cases below would "reject" for a reason that has nothing to do with the denylist. Touching
        // the type is what forces the assembly in. In production this is free: inside Revit the
        // add-in's own dependencies already pull it in, confirmed live (`dynamic d = 1;` runs).
        _ = typeof(Microsoft.CSharp.RuntimeBinder.Binder);
    }

    private static RoslynScriptRunner NewRunner(Action? compileCounter = null) =>
        new(compileCounter: compileCounter, additionalMetadataReferences: RevitApiReference.References);

    private static ScriptGlobals NewGlobals(CancellationToken token = default) => new(
        document: new FakeDocumentAdapter(),
        uiApplication: new FakeUiApplicationAdapter(),
        uiDocument: new FakeUiDocumentAdapter(),
        cancellationToken: token);

    [Fact]
    public async Task RunAsync_SimpleExpression_ReturnsValue()
    {
        var runner = NewRunner();

        var outcome = await runner.RunAsync("1 + 1", NewGlobals(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.ReturnValue);
    }

    [Fact]
    public async Task RunAsync_GlobalsBindToTheRealRevitTypes_ByPrdCasing()
    {
        // Phase 3 (PRD §14): Document/UIApplication/UIDocument are now the REAL Revit types. What this
        // tier can still assert is the BINDING -- that the PRD §06 identifiers exist in script scope
        // with that exact casing and are assignable to those exact types. Each lambda below proves one
        // of those, purely at compile time.
        //
        // Asserted via the compile counter rather than a return value, deliberately: the counter only
        // increments once GetOrCompile has bound the script successfully, so `1` IS the proof that all
        // three bindings type-checked. The script cannot be EXECUTED in this tier at all -- the emitted
        // submission names Revit types, and RevitAPI.dll is a mixed-mode assembly that will not load
        // outside Revit (see RevitApiReference). Execution-and-result assertions for the globals moved
        // to the tier-2 live harness (revit/test-harness) when this shipped.
        var compileCount = 0;
        var runner = NewRunner(compileCounter: () => compileCount++);

        var outcome = await runner.RunAsync(
            "System.Func<Autodesk.Revit.DB.Document> d = () => Document; " +
            "System.Func<Autodesk.Revit.UI.UIApplication> a = () => UIApplication; " +
            "System.Func<Autodesk.Revit.UI.UIDocument> u = () => UIDocument; " +
            "return d != null && a != null && u != null;",
            NewGlobals(), CancellationToken.None);

        Assert.Equal(1, compileCount);
        Assert.IsNotType<ScriptApiDenylistViolationException>(outcome.Exception);
    }

    // NOTE, deliberately not a test: ScriptGlobals' "this adapter cannot supply a real Revit object"
    // guard (its Raw<T> helper) CANNOT be exercised from this tier, and trying was informative enough to
    // write down. A script reading `Document.Title` never reaches that guard here: the JIT must resolve
    // every type a method body references -- including ScriptGlobals.Document's own return type -- before
    // executing a single statement of it, so the emitted submission fails on loading RevitAPI.dll (a
    // mixed-mode assembly; see RevitApiReference) rather than on the getter's null check. The guard is
    // defensive code for a future adapter that forgets IRawDocumentSource, and it is honest to say it is
    // unreachable from tier 1 rather than to pin it with a test that passes for an unrelated reason.

    [Fact]
    public async Task RunAsync_ConcurrentRuns_CaptureTheirOwnStdOut_Independently()
    {
        // Issue #52 / #35: the old process-global Console.SetOut swap made concurrent runs (and
        // xunit's default parallel test classes) cross-capture stdout. The AsyncLocal ambient
        // writer isolates per execution context: each run sees exactly its own writes, and a
        // plain background thread with no ambient writer never leaks into either capture.
        var runner = NewRunner();

        using var backgroundStop = new System.Threading.CancellationTokenSource();
        var background = Task.Run(() =>
        {
            while (!backgroundStop.IsCancellationRequested)
            {
                System.Console.Write("NOISE");
                System.Threading.Thread.Sleep(1);
            }
        });

        var runA = Task.Run(() => runner.RunAsync(
            "for (var i = 0; i < 50; i++) { System.Console.Write(\"A\"); System.Threading.Thread.Sleep(1); } 1",
            NewGlobals(), CancellationToken.None));
        var runB = Task.Run(() => runner.RunAsync(
            "for (var i = 0; i < 50; i++) { System.Console.Write(\"B\"); System.Threading.Thread.Sleep(1); } 1",
            NewGlobals(), CancellationToken.None));

        var outcomes = await Task.WhenAll(runA, runB);
        backgroundStop.Cancel();
        await background;

        Assert.True(outcomes[0].Success && outcomes[1].Success);
        Assert.Equal(new string('A', 50), outcomes[0].StdOut);
        Assert.Equal(new string('B', 50), outcomes[1].StdOut);
    }

    [Fact]
    public async Task RunAsync_VerbatimReRun_DoesNotReEmit()
    {
        // Issue #52: Emit used to run per EXECUTION even on compilation-cache hits. The emit is
        // now cached on CompiledScript; asserted here at the unit seam (an emit counter through
        // GetOrEmitPeImage) plus end-to-end that a re-run still executes correctly off the cached
        // image.
        var emitCalls = 0;
        var compiled = new CompiledScript(
            Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript.Create<object>("1 + 1"),
            ScriptApiAnalysis.Clean);
        byte[] FakeEmit(Microsoft.CodeAnalysis.Scripting.Script<object> _)
        {
            emitCalls++;
            return new byte[] { 1, 2, 3 };
        }

        _ = compiled.GetOrEmitPeImage(FakeEmit);
        var second = compiled.GetOrEmitPeImage(FakeEmit);

        Assert.Equal(1, emitCalls);
        Assert.Equal(new byte[] { 1, 2, 3 }, second);

        var runner = NewRunner();
        var first = await runner.RunAsync("40 + 2", NewGlobals(), CancellationToken.None);
        var rerun = await runner.RunAsync("40 + 2", NewGlobals(), CancellationToken.None);
        Assert.Equal(42, first.ReturnValue);
        Assert.Equal(42, rerun.ReturnValue);
    }

    [Fact]
    public void WarmupCompile_PopulatesTheCache_AndNeverThrows()
    {
        // Issue #52: warmup compiles+emits the marker once; a second call is a cache hit; and the
        // never-throws contract holds by construction (asserted trivially by running it at all --
        // a throw fails the test).
        var compileCount = 0;
        var runner = NewRunner(compileCounter: () => compileCount++);

        runner.WarmupCompile();
        runner.WarmupCompile();

        Assert.Equal(1, compileCount);
    }

    [Fact]
    public async Task RunAsync_CapturesStdOut()
    {
        var runner = NewRunner();

        var outcome = await runner.RunAsync("System.Console.WriteLine(\"hello from script\"); 1", NewGlobals(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Contains("hello from script", outcome.StdOut);
    }

    [Fact]
    public async Task RunAsync_ThrowingScript_CapturesException_DoesNotThrowToCaller()
    {
        var runner = NewRunner();

        var outcome = await runner.RunAsync("throw new System.InvalidOperationException(\"boom\");", NewGlobals(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.False(outcome.WasCancelled);
        Assert.NotNull(outcome.Exception);
        Assert.Contains("boom", outcome.Exception!.Message);
    }

    [Fact]
    public async Task RunAsync_CompileError_CapturesAsFailure_DoesNotThrow()
    {
        var runner = NewRunner();

        var outcome = await runner.RunAsync("this is not valid C#!!!", NewGlobals(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Exception);
    }

    [Fact]
    public async Task RunAsync_CancelledToken_ObservedByScript_ReturnsCancelledOutcome()
    {
        var runner = NewRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var globals = NewGlobals(cts.Token);

        var outcome = await runner.RunAsync(
            "CancellationToken.ThrowIfCancellationRequested(); 1", globals, cts.Token);

        Assert.False(outcome.Success);
        Assert.True(outcome.WasCancelled);
    }

    [Fact]
    public async Task RunAsync_RepeatedIdenticalScript_UsesCachedCompilation()
    {
        var compileCount = 0;
        var runner = NewRunner(compileCounter: () => compileCount++);

        await runner.RunAsync("1 + 1", NewGlobals(), CancellationToken.None);
        await runner.RunAsync("1 + 1", NewGlobals(), CancellationToken.None);
        await runner.RunAsync("1 + 1", NewGlobals(), CancellationToken.None);

        Assert.Equal(1, compileCount);
    }

    [Fact]
    public async Task RunAsync_EachExecution_UsesACollectibleAlc_AndUnloadsItAfterCompletion()
    {
        // Whether a collectible ALC's memory is *actually* reclaimed by a given moment is
        // up to the GC and is notoriously non-deterministic to assert in-process (doubly so
        // inside a test host, which can itself hold diagnostic/reflection references that
        // have nothing to do with the code under test). What RoslynScriptRunner owns and
        // can be asserted deterministically is that it (a) hands each run its own
        // collectible context, and (b) actually calls Unload() on it once the run
        // completes and the result is captured (PRD §06) -- observed here via the
        // ALC's own Unloading event, which fires synchronously from Unload() itself.
        var unloadWasSignaled = false;
        AssemblyLoadContext? captured = null;

        var runner = new RoslynScriptRunner(alcFactory: () =>
        {
            var alc = new AssemblyLoadContext("mcpbridge-script-test-" + Guid.NewGuid(), isCollectible: true);
            alc.Unloading += _ => unloadWasSignaled = true;
            captured = alc;
            return alc;
        },
            additionalMetadataReferences: RevitApiReference.References);

        var outcome = await runner.RunAsync("1 + 1", NewGlobals(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.NotNull(captured);
        Assert.True(captured!.IsCollectible);
        Assert.True(unloadWasSignaled, "RoslynScriptRunner must call Unload() on the per-execution ALC once the run completes.");
    }

    [Fact]
    public async Task RunAsync_MemoryIsActuallyReclaimed_AfterUnload()
    {
        // PR #2 review, Fix 2: the previous implementation's isolation attempt (ambient
        // AssemblyLoadContext.EnterContextualReflection() + Script.WithOptions(script.Options) as a
        // "clone") was a no-op -- Roslyn's own InteractiveAssemblyLoader ignores ambient contextual-
        // reflection state and always loads generated submission assemblies into a load context with
        // IsCollectible == false, so nothing it loaded could ever actually be unloaded, regardless of
        // whether RoslynScriptRunner called alc.Unload(). The old test for this
        // (RunAsync_EachExecution_UsesACollectibleAlc_AndUnloadsItAfterCompletion, above) only asserted
        // that Unload() was *called*, which is exactly the kind of weak/misleading assertion that let
        // the no-op ship: Unload() was genuinely being called, on an ALC that genuinely was collectible
        // -- it just wasn't the one the submission assembly actually loaded into.
        //
        // This test asserts actual reclamation: capture WeakReferences to the run's AssemblyLoadContext
        // AND (second review, Fix 8) to the assembly Roslyn actually loaded into it, then force GC and
        // assert BOTH weak references can no longer resolve to a live object. The old version of this
        // test (before Fix 8) only captured a WeakReference to the ALC itself -- but an ALC that was
        // created and never actually used to load anything would ALSO become trivially collectible after
        // Unload(), which is exactly the no-op failure mode this whole rewrite (Fix 2) exists to catch
        // (see the class doc comment: the original bug was Roslyn silently loading the submission assembly
        // into a *different*, non-collectible load context, while this runner dutifully created and
        // unloaded a collectible ALC that nothing was ever actually loaded into). Asserting the assembly's
        // own WeakReference is unresolvable too proves something was actually loaded into this ALC and
        // then actually reclaimed, not just that an empty ALC was created and unloaded.
        var (weakAlc, weakAssembly, outcome) = await RunAndCaptureWeakAlcAsync();

        Assert.True(outcome.Success);

        for (var i = 0; i < 10 && (weakAlc.IsAlive || weakAssembly.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(weakAlc.IsAlive, "The per-execution AssemblyLoadContext must become collectible once its run completes and Unload() is called.");
        Assert.False(weakAssembly.IsAlive, "The assembly actually loaded into the per-execution AssemblyLoadContext must become collectible too -- not just the (possibly empty) ALC itself.");
    }

    // Not inlined, and returns only the WeakReferences + outcome (never the ALC, assembly, or any
    // script-execution internals) so nothing keeps them rooted on the caller's stack frame once this
    // returns -- otherwise the JIT could keep locals alive for the rest of the enclosing method and the GC
    // assertion above would be meaningless.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference WeakAlc, WeakReference WeakAssembly, ScriptExecutionOutcome Outcome)> RunAndCaptureWeakAlcAsync()
    {
        AssemblyLoadContext? captured = null;
        var runner = new RoslynScriptRunner(alcFactory: () =>
        {
            var alc = new AssemblyLoadContext("mcpbridge-script-weakref-test-" + Guid.NewGuid(), isCollectible: true);
            captured = alc;
            return alc;
        },
            additionalMetadataReferences: RevitApiReference.References);

        var outcome = await runner.RunAsync("1 + 1", NewGlobals(), CancellationToken.None);

        // At this point RunAsync's finally block has already called alc.Unload() -- Unload() only requests
        // unloading (the actual reclamation happens via GC), so the ALC and its loaded assembly are both
        // still live and readable here. Fix 8: read the actually-loaded assembly off the ALC itself
        // (rather than trusting that "the ALC became collectible" implies "something was loaded into it")
        // so the sanity assertion below, and the WeakReference captured from it, are both grounded in what
        // Roslyn genuinely loaded -- not an assumption.
        Assembly? loadedAssembly = captured!.Assemblies.SingleOrDefault();
        Assert.NotNull(loadedAssembly); // sanity: the ALC actually has something loaded into it, not empty

        var weakAlc = new WeakReference(captured);
        var weakAssembly = new WeakReference(loadedAssembly);
        captured = null;
        loadedAssembly = null;
        return (weakAlc, weakAssembly, outcome);
    }

    [Fact]
    public async Task RunAsync_ScriptContainsAwait_RejectedBeforeCompilation_NeverHangsOrSilentlyDrops()
    {
        // PR #2 review, Fix 1's confirmed architecture decision: Execute() blocks on a script's Task
        // synchronously (.GetAwaiter().GetResult()), which is only deadlock-safe when the script has no
        // internal `await` of its own. A script with a top-level `await` must be rejected outright --
        // never silently hung, never silently dropped (PRD §01 observability-over-silence).
        var runner = NewRunner();

        var outcome = await runner.RunAsync(
            "await System.Threading.Tasks.Task.Delay(1); 1", NewGlobals(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.False(outcome.WasCancelled);
        Assert.IsType<ScriptAwaitNotAllowedException>(outcome.Exception);
        Assert.Contains(ScriptAwaitNotAllowedException.Code, outcome.Exception!.Message);
    }

    [Fact]
    public async Task RunAsync_ScriptContainsAwait_NeverCached_NeverCompiled()
    {
        var compileCount = 0;
        var runner = NewRunner(compileCounter: () => compileCount++);

        await runner.RunAsync("await System.Threading.Tasks.Task.Delay(1); 1", NewGlobals(), CancellationToken.None);
        await runner.RunAsync("await System.Threading.Tasks.Task.Delay(1); 1", NewGlobals(), CancellationToken.None);

        Assert.Equal(0, compileCount);
    }

    // --- PRD §14: ScriptApiDenylist ---
    //
    // These replace the three RunAsync_ScriptCalls*CreateTransaction_FailsToCompile cases that stood
    // here before Phase 3. Those guarded the same invariant by type-narrowing (IScriptDocument &c.,
    // now deleted), which only ever blocked OUR OWN adapter's CreateTransaction/CreateTransactionGroup
    // -- not real Revit API, and not the thing that actually matters. Now that `Document` is the real
    // Autodesk.Revit.DB.Document, the real risk is a script constructing its own
    // Autodesk.Revit.DB.Transaction against the document TransactionScriptExecutor has already opened
    // one on, and that is what the denylist blocks.
    //
    // All of these are COMPILE-time only: they need the Document/Transaction TYPES (via the metadata
    // reference and the static ctor's load touch above), never a live Document instance -- which is
    // exactly why this whole safety-relevant slice stays in tier 1 with no live Revit.

    [Theory]
    [InlineData("new Autodesk.Revit.DB.Transaction(Document, \"x\");", "Transaction")]
    [InlineData("new Autodesk.Revit.DB.TransactionGroup(Document, \"x\");", "TransactionGroup")]
    [InlineData("new Autodesk.Revit.DB.SubTransaction(Document);", "SubTransaction")]
    public async Task RunAsync_ScriptOpensItsOwnTransaction_IsRejectedByTheDenylist(string script, string expectedNamedType)
    {
        // THE load-bearing check. TransactionScriptExecutor opens the ambient TransactionGroup/
        // Transaction before compilation even runs; Revit permits only one open Transaction per
        // Document, so a script opening its own always fails -- and is what would make exposing the
        // real Document unsafe. Rejected at compile time, so the executor's existing rollback path
        // handles it exactly like any other compile error (no new failure handling).
        var runner = NewRunner();

        var outcome = await runner.RunAsync(script, NewGlobals(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.False(outcome.WasCancelled);
        Assert.IsType<ScriptApiDenylistViolationException>(outcome.Exception);
        Assert.Contains(ScriptApiDenylistViolationException.DeniedCode, outcome.Exception!.Message);
        Assert.Contains(expectedNamedType, outcome.Exception.Message);
    }

    // --- PRD §14: the confirmation-gated half ---
    //
    // The lifecycle/worksharing members are no longer flatly rejected: they are allowed when the
    // request itself opted in with confirm_lifecycle_actions. The principle deciding which members
    // belong here (and why this is a gate rather than a block) is written out in ScriptApiDenylist's
    // own doc comment: a script's changes roll back automatically if it throws, because the ambient
    // transaction covers document CONTENT; these members escape that boundary -- a person's session,
    // the filesystem, the shared central model, a printer, another user's checkout -- and no exception
    // undoes them. So the test is a per-run question, not a per-script one, and these cases assert
    // BOTH answers for the same script text.

    [Theory]
    // Overloads are cast-disambiguated so each script binds to exactly one member; an ambiguous call
    // (CS0121) would fail compilation before the denylist ever ran, and the test would pass for the
    // wrong reason. Signatures confirmed against the live 2027 API via describe_function.
    [InlineData("Document.Close();", "Close")]
    [InlineData("Document.Save();", "Save")]
    [InlineData("Document.SaveAs((string)null);", "SaveAs")]
    [InlineData("Document.SynchronizeWithCentral(null, null);", "SynchronizeWithCentral")]
    [InlineData("Document.Print((Autodesk.Revit.DB.ViewSet)null);", "Print")]
    [InlineData("Autodesk.Revit.DB.WorksharingUtils.RelinquishOwnership(Document, null, null);", "RelinquishOwnership")]
    // Added after an independent PR review proposed them and each was run through the SAME membership
    // question the six above were ("does a thrown exception in the script undo this?" -- no, for all of
    // them), then confirmed to exist with these exact signatures in the live Revit 2027 API via
    // describe_function rather than assumed.
    [InlineData("Document.SaveAsCloudModel(System.Guid.Empty, System.Guid.Empty, null, null);", "SaveAsCloudModel")]
    [InlineData("Document.Dispose();", "Dispose")]
    [InlineData("Document.PrintManager.SubmitPrint();", "SubmitPrint")]
    [InlineData("UIDocument.SaveAndClose();", "SaveAndClose")]
    [InlineData("UIApplication.PostCommand((Autodesk.Revit.UI.RevitCommandId)null);", "PostCommand")]
    public async Task RunAsync_LifecycleMemberWithoutConfirmation_IsRejected(string script, string expectedNamedMember)
    {
        var runner = NewRunner();

        // No confirmLifecycleActions argument at all -- the default, and the shape of every request
        // from an agent that has never heard of the flag. That MUST be the refusing case.
        var outcome = await runner.RunAsync(script, NewGlobals(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.False(outcome.WasCancelled);
        var ex = Assert.IsType<ScriptApiDenylistViolationException>(outcome.Exception);
        // The distinct code is the actionable part: script-api-denied means "change the script",
        // script-lifecycle-confirmation-required means "resend with the flag". An agent that cannot
        // tell them apart either gives up on a permitted operation or retries a forbidden one forever.
        Assert.Equal(ScriptApiDenylistViolationException.ConfirmationRequiredCode, ex.Code);
        Assert.Contains(ScriptApiDenylistViolationException.ConfirmationRequiredCode, ex.Message);
        Assert.Contains("confirm_lifecycle_actions", ex.Message);
        Assert.Contains(expectedNamedMember, ex.Message);
    }

    [Theory]
    [InlineData("Document.Close();")]
    [InlineData("Document.Save();")]
    [InlineData("Document.SaveAs((string)null);")]
    [InlineData("Document.SynchronizeWithCentral(null, null);")]
    [InlineData("Document.Print((Autodesk.Revit.DB.ViewSet)null);")]
    [InlineData("Autodesk.Revit.DB.WorksharingUtils.RelinquishOwnership(Document, null, null);")]
    [InlineData("Document.SaveAsCloudModel(System.Guid.Empty, System.Guid.Empty, null, null);")]
    [InlineData("Document.Dispose();")]
    [InlineData("Document.PrintManager.SubmitPrint();")]
    [InlineData("UIDocument.SaveAndClose();")]
    [InlineData("UIApplication.PostCommand((Autodesk.Revit.UI.RevitCommandId)null);")]
    public async Task RunAsync_LifecycleMemberWithConfirmation_IsNotRejected(string script)
    {
        // What this tier can prove, precisely: the SAME text that was refused above passes the gate
        // and is handed on to execution. It cannot prove the call then succeeds -- the emitted
        // submission names Revit types, and RevitAPI.dll is a mixed-mode assembly no test host can
        // load (see RevitApiReference), so the run fails afterwards for that unrelated reason. The
        // assertion is therefore "not refused BY THE GATE", which is exactly the property the gate
        // owns. Actually calling a lifecycle member end to end is a tier-2 case, in
        // revit/test-harness, where a real Revit is on the other end.
        var compileCount = 0;
        var runner = NewRunner(compileCounter: () => compileCount++);

        var outcome = await runner.RunAsync(script, NewGlobals(), CancellationToken.None, confirmLifecycleActions: true);

        Assert.Equal(1, compileCount);
        Assert.IsNotType<ScriptApiDenylistViolationException>(outcome.Exception);
    }

    [Fact]
    public async Task RunAsync_LifecycleScriptConfirmedThenResentUnconfirmed_IsRejectedTheSecondTime()
    {
        // THE cache-vs-per-request case, and the reason detection and decision are split at all. The
        // compiled script (and the detection result riding with it) is cached by verbatim text, so a
        // confirmed run leaves a cache entry behind. If the decision had been folded into compilation,
        // this second, UNCONFIRMED run would hit that entry and be waved through -- a permanent
        // confirmation bypass available to anyone who confirmed once. Compilation is genuinely reused
        // (compileCount stays 1); the decision is not.
        var compileCount = 0;
        var runner = NewRunner(compileCounter: () => compileCount++);
        const string script = "Document.Save();";

        var confirmed = await runner.RunAsync(script, NewGlobals(), CancellationToken.None, confirmLifecycleActions: true);
        var unconfirmed = await runner.RunAsync(script, NewGlobals(), CancellationToken.None);

        Assert.Equal(1, compileCount);
        Assert.IsNotType<ScriptApiDenylistViolationException>(confirmed.Exception);
        var ex = Assert.IsType<ScriptApiDenylistViolationException>(unconfirmed.Exception);
        Assert.Equal(ScriptApiDenylistViolationException.ConfirmationRequiredCode, ex.Code);
    }

    [Theory]
    // The bypass shapes that defeated the denylist's first, syntax-matching version, re-pinned for the
    // lifecycle members specifically now that they run down a different enforcement path (detected at
    // compile time, judged at run time) than the transaction check that originally caught them. A
    // method group is never an InvocationExpressionSyntax, and a script can reach one without ever
    // writing a call -- if detection only saw calls, the gate would be trivially avoidable.
    [InlineData("System.Func<bool> f = Document.Close; return f != null;", "Close")]
    [InlineData("System.Action f = Document.Save; return f != null;", "Save")]
    [InlineData("using D = Autodesk.Revit.DB.Document; System.Func<D, bool> f = d => d.Close(); return f != null;", "Close")]
    public async Task RunAsync_LifecycleMemberReachedWithoutACall_StillRequiresConfirmation(string script, string expectedNamedMember)
    {
        var runner = NewRunner();

        var outcome = await runner.RunAsync(script, NewGlobals(), CancellationToken.None);

        var ex = Assert.IsType<ScriptApiDenylistViolationException>(outcome.Exception);
        Assert.Equal(ScriptApiDenylistViolationException.ConfirmationRequiredCode, ex.Code);
        Assert.Contains(expectedNamedMember, ex.Message);
    }

    [Theory]
    // COMPILER-SYNTHESIZED DISPOSE shapes (v1 integrated review; live-exploitable before the fix):
    // `using` on the ambient Document closes it when the scope ends -- Dispose is Close under
    // another spelling -- but the synthesized Dispose call never appears as a bindable node, so the
    // symbol walk alone saw nothing. These are the single most idiomatic shapes a C#-fluent agent
    // could produce by accident, which is precisely the tier's target (unlike `dynamic`, the
    // documented accepted gap). The last two pin the interface-dispatch variant: casting to
    // System.IDisposable moves Dispose's containing type off the gated table, so the cast/as node
    // itself is what must be judged.
    [InlineData("using (Document) { }", "Dispose")]
    // Braced: a using DECLARATION needs an enclosing block to be legal at all in script code
    // (bare at top level it's a compile error, which would pass this test for the wrong reason).
    [InlineData("{ using var d = Document; }", "Dispose")]
    [InlineData("using (var d = Document) { }", "Dispose")]
    [InlineData("((System.IDisposable)Document).Dispose();", "Dispose")]
    [InlineData("var d = Document as System.IDisposable; d.Dispose();", "Dispose")]
    // Second review round's additions -- laundering shapes at the same explicit-intent level as the
    // cast: a chained cast through object, and the two pattern forms that hand out an
    // IDisposable-typed binding.
    [InlineData("((System.IDisposable)(object)Document).Dispose();", "Dispose")]
    [InlineData("if (Document is System.IDisposable d) { d.Dispose(); }", "Dispose")]
    [InlineData("switch (Document) { case System.IDisposable d: d.Dispose(); break; }", "Dispose")]
    public async Task RunAsync_SynthesizedOrLaunderedDispose_StillRequiresConfirmation(string script, string expectedNamedMember)
    {
        var runner = NewRunner();

        var outcome = await runner.RunAsync(script, NewGlobals(), CancellationToken.None);

        var ex = Assert.IsType<ScriptApiDenylistViolationException>(outcome.Exception);
        Assert.Equal(ScriptApiDenylistViolationException.ConfirmationRequiredCode, ex.Code);
        Assert.Contains(expectedNamedMember, ex.Message);
    }

    [Fact]
    public async Task RunAsync_OrdinaryDisposableUsing_IsNotGated()
    {
        // The other half of the synthesized-Dispose contract: gating keys on the RESOURCE TYPE having
        // a gated Dispose, so every ordinary IDisposable a script legitimately leans on (writers,
        // streams, ...) must flow through untouched -- a gate that taxed all of `using` would teach
        // agents to stop disposing things.
        var runner = NewRunner();

        var outcome = await runner.RunAsync(
            "using (var w = new System.IO.StringWriter()) { w.Write(\"ok\"); return w.ToString(); }",
            NewGlobals(),
            CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("ok", outcome.ReturnValue);
    }

    [Fact]
    public async Task RunAsync_AwaitUsing_IsRejectedLikeAnyOtherAwait()
    {
        // PR review finding (pre-existing, same synthesized-shape class as the Dispose gate): `await
        // using` carries its await as a keyword TOKEN with no AwaitExpressionSyntax node, so the old
        // node-typed walk let a script-defined IAsyncDisposable smuggle a genuine yield past the
        // top-level-await guard -- resuming script code off Revit's API context with the ambient
        // transaction open. The token-based check must refuse it with the same distinct exception.
        var runner = NewRunner();

        var outcome = await runner.RunAsync(
            "class A : System.IAsyncDisposable { public System.Threading.Tasks.ValueTask DisposeAsync() => default; }\n" +
            "{ await using var a = new A(); }",
            NewGlobals(),
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.IsType<ScriptAwaitNotAllowedException>(outcome.Exception);
    }

    [Fact]
    public async Task RunAsync_ScriptThrowingItsOwnOperationCanceledException_IsAFailure_NotCancelled()
    {
        // PRD §06: `cancelled` means "the agent asked for this". A script that throws OCE on its own
        // (or a call that surfaces one) with no cancellation ever requested used to be misreported as
        // cancelled -- recording a cancel nobody issued (v1 integrated review). The genuine path
        // (token signalled, script observes it) is pinned elsewhere; this pins the distinction.
        var runner = NewRunner();

        var outcome = await runner.RunAsync(
            "throw new System.OperationCanceledException();", NewGlobals(), CancellationToken.None);

        Assert.False(outcome.WasCancelled);
        Assert.False(outcome.Success);
        Assert.IsType<System.OperationCanceledException>(outcome.Exception);
    }

    [Fact]
    public async Task RunAsync_ScriptUsingSeveralLifecycleMembers_NamesThemAll()
    {
        // One refusal, every member named. An agent that had to discover them one failed run at a time
        // would either give up or confirm blind; naming them all is what lets it judge the whole set.
        var runner = NewRunner();

        var outcome = await runner.RunAsync(
            "Document.Save(); Document.Close(); return 1;", NewGlobals(), CancellationToken.None);

        var ex = Assert.IsType<ScriptApiDenylistViolationException>(outcome.Exception);
        Assert.Contains("Autodesk.Revit.DB.Document.Save", ex.Message);
        Assert.Contains("Autodesk.Revit.DB.Document.Close", ex.Message);
    }

    [Theory]
    [InlineData("new Autodesk.Revit.DB.Transaction(Document, \"x\");", "Transaction")]
    [InlineData("Autodesk.Revit.DB.TransactionGroup g = new(Document, \"x\"); return 1;", "TransactionGroup")]
    [InlineData("using Tx = Autodesk.Revit.DB.SubTransaction; var s = new Tx(Document); return 1;", "SubTransaction")]
    public async Task RunAsync_TransactionConstruction_IsRejectedEvenWithConfirmation(string script, string expectedNamedType)
    {
        // Check 1 is NOT gated and has no opt-in, by design: a second Transaction on the same document
        // is not a risk judgement a caller may override, it simply cannot work (Revit permits one open
        // Transaction per document). Passing the confirmation flag must change nothing here -- if it
        // ever did, the flag would have become a general "ignore the denylist" switch, which is not
        // what it is.
        var runner = NewRunner();

        var outcome = await runner.RunAsync(script, NewGlobals(), CancellationToken.None, confirmLifecycleActions: true);

        Assert.False(outcome.Success);
        var ex = Assert.IsType<ScriptApiDenylistViolationException>(outcome.Exception);
        Assert.Equal(ScriptApiDenylistViolationException.DeniedCode, ex.Code);
        Assert.Contains(expectedNamedType, ex.Message);
    }

    [Theory]
    // Target-typed `new` -- an ImplicitObjectCreationExpressionSyntax, NOT an
    // ObjectCreationExpressionSyntax. The first version of ScriptApiDenylist matched the latter by
    // syntax shape and this walked straight past it: confirmed LIVE against a real document, where it
    // successfully opened a competing Transaction. That is the exact invariant the whole "expose the
    // real Document" design rests on, so it is pinned per denied type, not just once.
    [InlineData("Autodesk.Revit.DB.Transaction t = new(Document, \"x\"); return 1;", "Transaction")]
    [InlineData("Autodesk.Revit.DB.TransactionGroup g = new(Document, \"x\"); return 1;", "TransactionGroup")]
    [InlineData("Autodesk.Revit.DB.SubTransaction s = new(Document); return 1;", "SubTransaction")]
    // A using-alias is another spelling the semantic check must see through.
    [InlineData("using Tx = Autodesk.Revit.DB.Transaction; var t = new Tx(Document, \"x\"); return 1;", "Transaction")]
    public async Task RunAsync_TransactionOpenedBySomeOtherSpellingOfNew_IsStillRejected(string script, string expectedNamedType)
    {
        var runner = NewRunner();

        var outcome = await runner.RunAsync(script, NewGlobals(), CancellationToken.None);

        Assert.IsType<ScriptApiDenylistViolationException>(outcome.Exception);
        Assert.Contains(expectedNamedType, outcome.Exception!.Message);
    }

    [Theory]
    // `dynamic` is the one spelling of `new` where the CONSTRUCTOR, not just the syntax, can go
    // unresolved: an argument of type dynamic makes overload resolution late-bound in principle, and
    // GetSymbolInfo(...).Symbol is null for a late-bound node. An independent PR review flagged this as
    // a way to walk past check 1 -- the refusal PRD §14 and skill.md both promise can never be opted
    // into -- in the same category as the target-typed `new` and method-group bypasses already fixed
    // here. It was checked LIVE against a real Revit 2027 document before being "fixed": `dynamic` is
    // genuinely usable from a script here (Microsoft.CSharp is present; `dynamic d = 1; d.ToString()`
    // runs), and every shape below was ALREADY rejected, because Roslyn still hands back the bound
    // constructor symbol for an object creation with dynamic arguments. So these cases pin behaviour
    // that already held rather than closing an open hole -- and the type-based fallback they exercise
    // (ScriptApiDenylist.Analyze) means the promise no longer rests on that Roslyn detail alone.
    [InlineData("dynamic d = Document; var t = new Autodesk.Revit.DB.Transaction(d, \"x\"); return 1;", "Transaction")]
    [InlineData("dynamic d = Document; dynamic n = \"x\"; var t = new Autodesk.Revit.DB.Transaction(d, n); return 1;", "Transaction")]
    [InlineData("dynamic d = Document; Autodesk.Revit.DB.TransactionGroup g = new(d, \"x\"); return 1;", "TransactionGroup")]
    [InlineData("dynamic d = Document; dynamic s = new Autodesk.Revit.DB.SubTransaction(d); return 1;", "SubTransaction")]
    public async Task RunAsync_TransactionConstructedWithALateBoundArgument_IsStillRejected(string script, string expectedNamedType)
    {
        var runner = NewRunner();

        var outcome = await runner.RunAsync(script, NewGlobals(), CancellationToken.None);

        var ex = Assert.IsType<ScriptApiDenylistViolationException>(outcome.Exception);
        Assert.Equal(ScriptApiDenylistViolationException.DeniedCode, ex.Code);
        Assert.Contains(expectedNamedType, ex.Message);
    }

    [Fact]
    public async Task RunAsync_DeniedScript_NeverCached_NeverCompiled()
    {
        // Same guarantee the await rejection has: an UNCONDITIONALLY denied script must not be counted
        // as a successful compilation nor enter the compilation cache, or a later identical run would
        // hit the cache and skip the check entirely. Note this is the opposite of the confirmation-gated
        // members, which DO get cached on purpose (their detection cannot change between runs, only the
        // decision can) -- see RunAsync_LifecycleScriptConfirmedThenResentUnconfirmed_IsRejectedTheSecondTime.
        var compileCount = 0;
        var runner = NewRunner(compileCounter: () => compileCount++);

        await runner.RunAsync("new Autodesk.Revit.DB.Transaction(Document, \"x\");", NewGlobals(), CancellationToken.None);
        var second = await runner.RunAsync("new Autodesk.Revit.DB.Transaction(Document, \"x\");", NewGlobals(), CancellationToken.None);

        Assert.Equal(0, compileCount);
        Assert.IsType<ScriptApiDenylistViolationException>(second.Exception);
    }

    [Theory]
    [InlineData("\"new Autodesk.Revit.DB.Transaction(Document, x)\".Length > 0")]
    [InlineData("\"Document.Close()\".Length > 0")]
    [InlineData("\"Autodesk.Revit.DB.WorksharingUtils.RelinquishOwnership\".Length > 0")]
    public async Task RunAsync_DenylistedTextInsideAStringLiteral_IsNotRejected(string script)
    {
        // Sanity check that the rejection is a real SEMANTIC check over bound symbols, not a text
        // search -- the exact counterpart of RunAsync_AwaitInsideAStringLiteral_IsNotRejected.
        var runner = NewRunner();

        var outcome = await runner.RunAsync(script, NewGlobals(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(true, outcome.ReturnValue);
    }

    [Fact]
    public async Task RunAsync_OrdinaryRevitApiUse_IsNotRejected()
    {
        // The denylist must not blanket-ban the Revit API -- exposing it is the entire point of Phase 3.
        // The compile counter is the assertion: it only increments after ScriptApiDenylist.Analyze has
        // returned without throwing, so `1` means this script bound real Revit symbols (a collector over
        // the real Document, the headline example the whole design exists for) and was allowed through.
        var compileCount = 0;
        var runner = NewRunner(compileCounter: () => compileCount++);

        var outcome = await runner.RunAsync(
            "System.Func<Autodesk.Revit.DB.FilteredElementCollector> f = " +
            "() => new Autodesk.Revit.DB.FilteredElementCollector(Document); return f != null;",
            NewGlobals(), CancellationToken.None);

        Assert.Equal(1, compileCount);
        Assert.IsNotType<ScriptApiDenylistViolationException>(outcome.Exception);
    }

    [Fact]
    public async Task RunAsync_DenylistedMemberNameOnAnUnrelatedType_IsNotRejected()
    {
        // The check is (containing type, member), not a bare member name: `Close` on some other type
        // is ordinary .NET and must keep working. A name-only check would break plain System.IO use.
        var runner = NewRunner();

        var outcome = await runner.RunAsync(
            "var ms = new System.IO.MemoryStream(); ms.Close(); return \"ok\";", NewGlobals(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("ok", outcome.ReturnValue);
    }

    [Fact]
    public async Task RunAsync_AwaitInsideAStringLiteral_IsNotRejected()
    {
        // Sanity check that the rejection is a real syntax-tree walk (AwaitExpressionSyntax), not a naive
        // text search for the substring "await".
        var runner = NewRunner();

        var outcome = await runner.RunAsync("\"await\".Length", NewGlobals(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(5, outcome.ReturnValue);
    }

    [Fact]
    public async Task RunAsync_ConnectorSettle_RequiresConfirmation()
    {
        // Issue #132, and the first entry in check 2's table that is OUR OWN member rather than Revit's.
        // Settle belongs there by the table's own governing test -- "does a thrown exception actually
        // undo this?" -- and the answer is no by construction: Settle(keep: true) assimilates the
        // document's group, making every write to it so far permanent immediately.
        //
        // Ungated it was a NON-REFLECTION route to commit/rollback authority over the ambient document,
        // which ScriptApiDenylist's own class comment says is accepted only via reflection and must be
        // closed structurally wherever such a route appears. Found by independent review, not by me.
        var runner = NewRunner();
        const string script = "Connector.Settle(Document, true);";

        var unconfirmed = await runner.RunAsync(script, NewGlobals(), CancellationToken.None);

        var ex = Assert.IsType<ScriptApiDenylistViolationException>(unconfirmed.Exception);
        Assert.Equal(ScriptApiDenylistViolationException.ConfirmationRequiredCode, ex.Code);
        Assert.Contains("Settle", ex.DeniedMember);
    }

    [Fact]
    public async Task RunAsync_ConnectorWithTransactionAndWithoutTransaction_AreNotGated()
    {
        // The other two scopes must NOT be gated, and asserting it is not redundant: gating the whole
        // Connector type rather than the single member would have been the easy mistake, and it would
        // have made every stairs script -- the feature's headline case -- require a flag it has no
        // reason to need. Neither leaves anything permanent; the group still covers rollback.
        var runner = NewRunner();

        foreach (var script in new[]
                 {
                     "Connector.WithTransaction(Document, () => { });",
                     "Connector.WithoutTransaction(Document, () => { });",
                 })
        {
            var outcome = await runner.RunAsync(script, NewGlobals(), CancellationToken.None);
            Assert.IsNotType<ScriptApiDenylistViolationException>(outcome.Exception);
        }
    }
}
