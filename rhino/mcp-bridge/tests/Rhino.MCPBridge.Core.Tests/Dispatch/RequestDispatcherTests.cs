using System.Text.Json;
using Rhino.MCPBridge.Core.Dispatch;
using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.Core.Protocol;
using Rhino.MCPBridge.Core.Tests.Fakes;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Dispatch;

/// <summary>The three wire methods end to end over the fake host and an inline launcher (PRD §06):
/// inline completion, pre-flight refusal, busy, poll, cancel, and the retry while Rhino is mid-command.</summary>
public sealed class RequestDispatcherTests
{
    private sealed class Harness
    {
        public FakeRunHost Host { get; } = new();
        public InlineLauncher Launcher { get; } = new();
        public ExecutionManager Manager { get; } = ExecutionManager.CreateDefault(ExecutionRingBuffer.CreateDefault());
        public List<string> Logs { get; } = new();
        public DateTimeOffset Now = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        public RequestDispatcher Dispatcher { get; }

        public Harness(IRunLauncher? launcher = null)
        {
            var runner = new RoslynScriptRunner();
            runner.WarmupCompile();
            Dispatcher = new RequestDispatcher(Manager, new UndoRunExecutor(runner, Host), launcher ?? Launcher, Logs.Add, () => Now, _ => Task.CompletedTask);
        }

        public async Task<JsonElement> Call(string method, object @params)
        {
            var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params });
            var resp = await Dispatcher.DispatchAsync(JsonRpcRequest.Parse(json), CancellationToken.None);
            return JsonDocument.Parse(resp).RootElement.Clone();
        }

        public Task<JsonElement> Execute(string script, string id = "exec-1", object? extra = null)
        {
            var p = new Dictionary<string, object?> { ["execution_id"] = id, ["language"] = "csharp", ["script"] = script };
            if (extra is not null)
            {
                foreach (var kv in JsonSerializer.SerializeToElement(extra).EnumerateObject()) p[kv.Name] = kv.Value;
            }

            return Call("execute_script", p);
        }
    }

    private static string Code(JsonElement e) =>
        e.TryGetProperty("error", out var err) ? err.GetProperty("data").GetProperty("code").GetString()!
        : e.GetProperty("result").TryGetProperty("error", out var re) ? re.GetProperty("code").GetString()!
        : "";

    private static string Status(JsonElement e) => e.GetProperty("result").GetProperty("status").GetString()!;

    [Fact]
    public async Task Success_CompletesInline_WithReturnValue()
    {
        var h = new Harness();
        var r = await h.Execute("return 6 * 7;");
        Assert.Equal("success", Status(r));
        Assert.Equal("42", r.GetProperty("result").GetProperty("return_value").GetString());
        Assert.Equal(1, h.Launcher.Posted);
        Assert.Equal(1, h.Host.CommandsRun);
    }

    [Fact]
    public async Task LanguageOtherThanCSharp_IsRefusedLoudly_BeforeAnythingStarts()
    {
        var h = new Harness();
        var r = await h.Call("execute_script", new { execution_id = "e", language = "python", script = "1" });
        Assert.Equal("language-not-available", Code(r));
        Assert.Equal(0, h.Launcher.Posted);
    }

    [Theory]
    [InlineData("Document.Undo();", "script-api-denied")]
    [InlineData("Document.Save();", "script-lifecycle-confirmation-required")]
    [InlineData("return 1 +;", "script-compilation-failed")]
    public async Task Preflight_RefusesWithItsOwnCode_WithoutLaunching(string script, string code)
    {
        var h = new Harness();
        var r = await h.Execute(script);
        Assert.Equal("error", Status(r));
        Assert.Equal(code, Code(r));
        Assert.Equal(0, h.Launcher.Posted);
    }

    [Fact]
    public async Task ConfirmFlag_LiftsTheGate()
    {
        var h = new Harness();
        // Document is null in tier 1, so the script returns before reaching Save; what this pins is that
        // the gate no longer refuses at pre-flight.
        var r = await h.Execute("if (Document is null) return \"would save\"; Document.Save(); return 0;", extra: new { confirm_lifecycle_actions = true });
        Assert.Equal("success", Status(r));
    }

    [Fact]
    public async Task SecondScriptWhileOneIsQueued_IsBusy_WithTheRunningId_AndPollFinishesIt()
    {
        var deferred = new DeferredLauncher();
        var h = new Harness(deferred);
        var first = await h.Execute("return 1;", id: "a", extra: new { timeout_ms = 0 });
        Assert.Equal("pending", Status(first));
        var second = await h.Execute("return 2;", id: "b");
        Assert.Equal("busy", Status(second));
        Assert.Equal("a", second.GetProperty("result").GetProperty("execution_id").GetString());
        deferred.RunAll();
        var polled = await h.Call("poll_execution", new { execution_id = "a" });
        Assert.Equal("success", Status(polled));
    }

    [Fact]
    public async Task Cancel_WhileQueued_ResolvesCancelled_AndNeverRuns()
    {
        var deferred = new DeferredLauncher();
        var h = new Harness(deferred);
        await h.Execute("return 1;", id: "a", extra: new { timeout_ms = 0 });
        var cancel = await h.Call("cancel_execution", new { execution_id = "a" });
        Assert.Equal("cancelled", Status(cancel));
        deferred.RunAll();
        Assert.Equal(0, h.Host.CommandsRun);
    }

    [Fact]
    public async Task RhinoMidCommand_KeepsTheRunPending_AndRetriesUntilItIsFree()
    {
        var h = new Harness();
        h.Host.RefuseCommands = true;
        var r = await h.Execute("return 1;", extra: new { timeout_ms = 0 });
        Assert.Equal("pending", Status(r));
        Assert.Equal(0, h.Host.CommandsRun);
        // The retry is scheduled through the delay hook (completed instantly here); once Rhino is
        // free the next attempt runs the command.
        h.Host.RefuseCommands = false;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (h.Host.CommandsRun == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        var polled = await h.Call("poll_execution", new { execution_id = "exec-1", timeout_ms = 0 });
        Assert.Equal("success", Status(polled));
    }

    [Fact]
    public async Task UnknownExecutionId_OnPollAndCancel()
    {
        var h = new Harness();
        Assert.Equal("unknown-execution-id", Code(await h.Call("poll_execution", new { execution_id = "nope" })));
        Assert.Equal("unknown-execution-id", Code(await h.Call("cancel_execution", new { execution_id = "nope" })));
    }

    [Fact]
    public async Task UnknownMethod_ListsTheSupportedOnes()
    {
        var h = new Harness();
        var r = await h.Call("list_functions", new { });
        Assert.Equal("unknown-method", Code(r));
        Assert.Contains("execute_script", r.GetProperty("error").GetProperty("data").GetProperty("remedy")[0].GetString());
    }

    [Fact]
    public async Task ExecutionFailure_CarriesTheExceptionType_AndTheRollbackNotice()
    {
        var h = new Harness();
        h.Host.DuringRun = on => on(new DocumentChange(DocumentChange.Kind.Added, Guid.NewGuid(), "Brep", "Default"));
        var r = await h.Execute("throw new System.InvalidOperationException(\"boom\");");
        Assert.Equal("error", Status(r));
        Assert.Equal("script-execution-failed", Code(r));
        var err = r.GetProperty("result").GetProperty("error");
        Assert.Contains("boom", err.GetProperty("message").GetString());
        Assert.Equal("script-rolled-back", r.GetProperty("result").GetProperty("notices")[0].GetProperty("code").GetString());
        Assert.Equal(1, h.Host.UndoCalls);
    }

    private sealed class DeferredLauncher : IRunLauncher
    {
        private readonly Queue<Action> _q = new();
        public void Post(Action a) => _q.Enqueue(a);
        public void RunAll() { while (_q.Count > 0) _q.Dequeue()(); }
    }
}
