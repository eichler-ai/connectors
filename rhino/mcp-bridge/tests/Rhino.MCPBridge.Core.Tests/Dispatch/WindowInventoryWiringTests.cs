using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rhino.MCPBridge.Core.Diagnostics;
using Rhino.MCPBridge.Core.Dispatch;
using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.Core.Protocol;
using Rhino.MCPBridge.Core.Tests.Fakes;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Dispatch;

/// <summary>The §08 integration point: does RequestDispatcher actually invoke the window inventory on a
/// timeout that leaves the run non-terminal, and merge (or omit) its notices? The positive attach path
/// cannot be shown live (staging a real main-thread-blocking modal is the whole §08 problem), so it is
/// proven here with a fake. The run is kept pending with a launcher that never runs the body, and a clock
/// the 100 ms poll delay advances past the deadline — exercising the real production condition (a genuine
/// timeout_ms>0 timeout, not the timeout_ms&lt;=0 non-blocking poll, which correctly does not fire).</summary>
public sealed class WindowInventoryWiringTests
{
    private sealed class Clock { public DateTimeOffset Now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero); }

    /// <summary>Accepts the run but never runs its body, so the execution stays pending (non-terminal).</summary>
    private sealed class PendingLauncher : IRunLauncher { public void Post(Action onMainThread) { } }

    private static WindowInfo MainWindow() => new("Untitled - Rhino 8", "RhinoMainWnd", IsMainWindow: true, IsVisible: true);
    private static WindowInfo Dialog() => new("Open Template File", "#32770", IsMainWindow: false, IsVisible: true);

    // PollInterval (100 ms) advances the clock a minute (past the 100 ms wait deadline) and completes, so
    // WaitAsync returns the pending record via timeout. The 2500 ms inventory cap either never completes
    // (so the fast fake pass wins the WhenAny) or completes at once (so the cap wins), per capWins.
    private static Func<TimeSpan, Task> Delay(Clock clock, bool capWins) => d =>
    {
        if (d >= TimeSpan.FromSeconds(1))
        {
            return capWins ? Task.CompletedTask : new TaskCompletionSource<bool>().Task;
        }

        clock.Now = clock.Now.AddMinutes(1);
        return Task.CompletedTask;
    };

    private static RequestDispatcher Dispatcher(Clock clock, Func<TimeSpan, Task> delay, IWindowInventory inv)
    {
        var runner = new RoslynScriptRunner();
        runner.WarmupCompile();
        return new RequestDispatcher(
            ExecutionManager.CreateDefault(ExecutionRingBuffer.CreateDefault()),
            new UndoRunExecutor(new ScriptRunners(runner), new FakeRunHost()),
            new PendingLauncher(), _ => { }, () => clock.Now, delay, windowInventory: inv);
    }

    private static async Task<JsonElement> RunNonTerminal(RequestDispatcher d)
    {
        var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "execute_script", @params = new { execution_id = "e", language = "csharp", script = "return 1;", timeout_ms = 100 } });
        var resp = await d.DispatchAsync(JsonRpcRequest.Parse(json), CancellationToken.None);
        return JsonDocument.Parse(resp).RootElement.Clone();
    }

    private static List<string> NoticeCodes(JsonElement r) =>
        r.GetProperty("result").TryGetProperty("notices", out var n)
            ? n.EnumerateArray().Select(x => x.GetProperty("code").GetString()!).ToList()
            : new List<string>();

    [Fact]
    public async Task Timeout_NonTerminal_WithACandidateWindow_AttachesTheFallbackNotice()
    {
        var clock = new Clock();
        var inv = new FakeWindowInventory { Windows = new[] { MainWindow(), Dialog() } };
        var r = await RunNonTerminal(Dispatcher(clock, Delay(clock, capWins: false), inv));

        Assert.Contains(WindowInventoryDiagnostic.NoticeCode, NoticeCodes(r));
        Assert.Equal(1, inv.EnumerateCallCount);
    }

    [Fact]
    public async Task Timeout_NonTerminal_WithOnlyTheMainWindow_AttachesNoNotice()
    {
        var clock = new Clock();
        var inv = new FakeWindowInventory { Windows = new[] { MainWindow() } };
        var r = await RunNonTerminal(Dispatcher(clock, Delay(clock, capWins: false), inv));

        Assert.DoesNotContain(WindowInventoryDiagnostic.NoticeCode, NoticeCodes(r));
        Assert.Equal(1, inv.EnumerateCallCount); // it ran; it just found no candidate
    }

    [Fact]
    public async Task Timeout_WhenTheInventoryExceedsItsCap_AttachesNoNotice()
    {
        var clock = new Clock();
        var inv = new FakeWindowInventory { Windows = new[] { MainWindow(), Dialog() }, BlockFor = TimeSpan.FromSeconds(30) };
        var r = await RunNonTerminal(Dispatcher(clock, Delay(clock, capWins: true), inv));

        Assert.DoesNotContain(WindowInventoryDiagnostic.NoticeCode, NoticeCodes(r));
    }

    [Fact]
    public async Task Timeout_WhenTheInventoryThrows_AttachesNoNotice_AndStillReturns()
    {
        var clock = new Clock();
        var inv = new FakeWindowInventory { Windows = new[] { MainWindow(), Dialog() }, ThrowOnEnumerate = true };
        var r = await RunNonTerminal(Dispatcher(clock, Delay(clock, capWins: false), inv));

        Assert.DoesNotContain(WindowInventoryDiagnostic.NoticeCode, NoticeCodes(r));
        Assert.True(r.GetProperty("result").TryGetProperty("status", out _)); // the response still came back
    }
}
