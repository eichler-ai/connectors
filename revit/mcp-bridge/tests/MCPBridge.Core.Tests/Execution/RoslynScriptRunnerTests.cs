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
    private static ScriptGlobals NewGlobals(CancellationToken token = default) => new(
        document: new FakeDocumentAdapter(),
        uiApplication: new FakeUiApplicationAdapter(),
        uiDocument: new FakeUiDocumentAdapter(),
        cancellationToken: token);

    [Fact]
    public async Task RunAsync_SimpleExpression_ReturnsValue()
    {
        var runner = new RoslynScriptRunner();

        var outcome = await runner.RunAsync("1 + 1", NewGlobals(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.ReturnValue);
    }

    [Fact]
    public async Task RunAsync_CanAccessGlobals_DocumentTitle()
    {
        var runner = new RoslynScriptRunner();
        var globals = NewGlobals();

        var outcome = await runner.RunAsync("Document.Title", globals, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("FakeDocument", outcome.ReturnValue);
    }

    [Fact]
    public async Task RunAsync_CapturesStdOut()
    {
        var runner = new RoslynScriptRunner();

        var outcome = await runner.RunAsync("System.Console.WriteLine(\"hello from script\"); 1", NewGlobals(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Contains("hello from script", outcome.StdOut);
    }

    [Fact]
    public async Task RunAsync_ThrowingScript_CapturesException_DoesNotThrowToCaller()
    {
        var runner = new RoslynScriptRunner();

        var outcome = await runner.RunAsync("throw new System.InvalidOperationException(\"boom\");", NewGlobals(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.False(outcome.WasCancelled);
        Assert.NotNull(outcome.Exception);
        Assert.Contains("boom", outcome.Exception!.Message);
    }

    [Fact]
    public async Task RunAsync_CompileError_CapturesAsFailure_DoesNotThrow()
    {
        var runner = new RoslynScriptRunner();

        var outcome = await runner.RunAsync("this is not valid C#!!!", NewGlobals(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Exception);
    }

    [Fact]
    public async Task RunAsync_CancelledToken_ObservedByScript_ReturnsCancelledOutcome()
    {
        var runner = new RoslynScriptRunner();
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
        var runner = new RoslynScriptRunner(compileCounter: () => compileCount++);

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
        });

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
        });

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
        var runner = new RoslynScriptRunner();

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
        var runner = new RoslynScriptRunner(compileCounter: () => compileCount++);

        await runner.RunAsync("await System.Threading.Tasks.Task.Delay(1); 1", NewGlobals(), CancellationToken.None);
        await runner.RunAsync("await System.Threading.Tasks.Task.Delay(1); 1", NewGlobals(), CancellationToken.None);

        Assert.Equal(0, compileCount);
    }

    [Fact]
    public async Task RunAsync_AwaitInsideAStringLiteral_IsNotRejected()
    {
        // Sanity check that the rejection is a real syntax-tree walk (AwaitExpressionSyntax), not a naive
        // text search for the substring "await".
        var runner = new RoslynScriptRunner();

        var outcome = await runner.RunAsync("\"await\".Length", NewGlobals(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(5, outcome.ReturnValue);
    }
}
