using System;
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
}
