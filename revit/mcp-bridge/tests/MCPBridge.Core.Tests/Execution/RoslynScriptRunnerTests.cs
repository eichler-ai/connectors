using System;
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
        // and the runner's ScriptCompilationCache is untouched (the cache intentionally keeps the
        // compiled Script<object> alive across runs -- see the class doc comment -- so this only
        // targets per-run state: the ALC used to execute this specific run), then force GC and assert
        // the weak reference can no longer resolve to a live object.
        var (weakAlc, outcome) = await RunAndCaptureWeakAlcAsync();

        Assert.True(outcome.Success);

        for (var i = 0; i < 10 && weakAlc.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(weakAlc.IsAlive, "The per-execution AssemblyLoadContext must become collectible once its run completes and Unload() is called.");
    }

    // Not inlined, and returns only the WeakReference + outcome (never the ALC or any script-execution
    // internals) so nothing keeps the ALC rooted on the caller's stack frame once this returns --
    // otherwise the JIT could keep locals alive for the rest of the enclosing method and the GC
    // assertion above would be meaningless.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference WeakAlc, ScriptExecutionOutcome Outcome)> RunAndCaptureWeakAlcAsync()
    {
        AssemblyLoadContext? captured = null;
        var runner = new RoslynScriptRunner(alcFactory: () =>
        {
            var alc = new AssemblyLoadContext("mcpbridge-script-weakref-test-" + Guid.NewGuid(), isCollectible: true);
            captured = alc;
            return alc;
        });

        var outcome = await runner.RunAsync("1 + 1", NewGlobals(), CancellationToken.None);
        var weakAlc = new WeakReference(captured);
        captured = null;
        return (weakAlc, outcome);
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
