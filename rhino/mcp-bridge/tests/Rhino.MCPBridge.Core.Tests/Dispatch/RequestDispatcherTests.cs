using System.Text.Json;
using Rhino.MCPBridge.Core.Dispatch;
using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.Core.Execution.Python;
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

        public Harness(IRunLauncher? launcher = null, FakePythonHost? python = null)
        {
            var runner = new RoslynScriptRunner();
            runner.WarmupCompile();
            var runners = python is null ? new ScriptRunners(runner) : new ScriptRunners(runner, new PythonScriptRunner(python));
            Dispatcher = new RequestDispatcher(Manager, new UndoRunExecutor(runners, Host), launcher ?? Launcher, Logs.Add, () => Now, _ => Task.CompletedTask);
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
    public async Task UnknownLanguage_IsRefusedLoudly_BeforeAnythingStarts()
    {
        var h = new Harness();
        var r = await h.Call("execute_script", new { execution_id = "e", language = "ruby", script = "1" });
        Assert.Equal("language-not-available", Code(r));
        Assert.Contains("csharp", r.GetProperty("error").GetProperty("data").GetProperty("message").GetString());
        Assert.Equal(0, h.Launcher.Posted);
    }

    [Fact]
    public async Task PythonWhileTheHostIsLoading_IsRefusedWithTheReason_AndRunsOnceLoaded()
    {
        var python = new FakePythonHost { UnavailableReason = "the Python 3 language has not finished loading" };
        var h = new Harness(python: python);
        var r = await h.Call("execute_script", new { execution_id = "e", language = "python", script = "result = 1" });
        Assert.Equal("language-not-available", Code(r));
        Assert.Contains("not finished loading", r.GetProperty("error").GetProperty("data").GetProperty("message").GetString());
        Assert.Equal(0, h.Launcher.Posted);

        python.EnsureLoaded();
        python.OnRun = (_, _) => new PythonRunResult { Result = "ok", StdOut = "hi\n" };
        var r2 = await h.Call("execute_script", new { execution_id = "e2", language = "python", script = "print('hi')\nresult = 'ok'" });
        Assert.Equal("success", Status(r2));
        Assert.Equal("ok", r2.GetProperty("result").GetProperty("return_value").GetString());
        Assert.Equal("hi\n", r2.GetProperty("result").GetProperty("output").GetString());
        Assert.Equal(1, h.Host.CommandsRun);
    }

    [Fact]
    public async Task WithBothRunners_CSharpStillGoesToRoslyn_AndPythonToTheHost()
    {
        var python = new FakePythonHost();
        python.EnsureLoaded();
        python.OnRun = (_, _) => new PythonRunResult { Result = "py" };
        var h = new Harness(python: python);
        var cs = await h.Execute("return \"cs\";", id: "a");
        Assert.Equal("cs", cs.GetProperty("result").GetProperty("return_value").GetString());
        Assert.Empty(python.Runs);
        var py = await h.Call("execute_script", new { execution_id = "b", language = "python", script = "result = 'py'" });
        Assert.Equal("py", py.GetProperty("result").GetProperty("return_value").GetString());
        // The script plus the scriptcontext restore that follows every run.
        Assert.Equal(2, python.Runs.Count);
        Assert.Equal(2, h.Host.CommandsRun);
    }

    [Fact]
    public async Task PythonPreflight_RefusesDeniedAndGatedScripts_WithoutLaunching()
    {
        var python = new FakePythonHost();
        python.EnsureLoaded();
        var h = new Harness(python: python);
        Assert.Equal("script-api-denied", Code(await h.Call("execute_script", new { execution_id = "a", language = "python", script = "import rhinoscriptsyntax as rs\nrs.GetObject()" })));
        Assert.Equal("script-lifecycle-confirmation-required", Code(await h.Call("execute_script", new { execution_id = "b", language = "python", script = "doc.Save()" })));
        Assert.Equal(0, h.Launcher.Posted);
        Assert.Empty(python.Runs);
    }

    [Fact]
    public async Task PythonError_CarriesTheTraceback_AndRollsBack()
    {
        var python = new FakePythonHost();
        python.EnsureLoaded();
        python.OnRun = (_, _) => new PythonRunResult { Error = new Exception("boom"), StdErr = "Traceback (most recent call last):\n  File \"<string>\", line 3, in <module>\nRuntimeError: boom\n" };
        var h = new Harness(python: python);
        h.Host.DuringRun = on => on(new DocumentChange(DocumentChange.Kind.Added, Guid.NewGuid(), "Brep", "Default"));
        var r = await h.Call("execute_script", new { execution_id = "a", language = "python", script = "raise RuntimeError('boom')" });
        Assert.Equal("error", Status(r));
        Assert.Equal("script-execution-failed", Code(r));
        var err = r.GetProperty("result").GetProperty("error");
        Assert.Equal("boom", err.GetProperty("message").GetString());
        Assert.Contains("line 1, in <module>", err.GetProperty("detail").GetProperty("traceback").GetString());
        Assert.Equal("script-rolled-back", r.GetProperty("result").GetProperty("notices")[0].GetProperty("code").GetString());
        Assert.Equal(1, h.Host.UndoCalls);
    }

    [Fact]
    public async Task PythonSyntaxError_MapsToTheCompileCode()
    {
        var python = new FakePythonHost();
        python.EnsureLoaded();
        python.OnRun = (_, _) => new PythonRunResult { Error = new Exception("invalid syntax"), StdErr = "  File \"<string>\", line 3\n    x = = 1\nSyntaxError: invalid syntax\n" };
        var h = new Harness(python: python);
        var r = await h.Call("execute_script", new { execution_id = "a", language = "python", script = "x = = 1" });
        Assert.Equal("script-compilation-failed", Code(r));
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
        // Each refused attempt marks the record running for the duration of the attempt and then
        // pending again, and the delay hook is a no-op here so attempts run back to back: the
        // response may land in either state. What is pinned is that nothing completed and no
        // command ran.
        Assert.Contains(Status(r), new[] { "pending", "running" });
        Assert.Equal(0, h.Host.CommandsRun);
        // Once Rhino is free the next attempt runs the command.
        h.Host.RefuseCommands = false;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        // CommandsRun flips when the command starts, not when it finishes, and the harness clock is
        // frozen (a poll with a timeout would spin rather than wait), so wait for the terminal state
        // here with a real deadline (was racy on the Windows CI runner).
        while (!(h.Manager.Poll("exec-1")?.Status.IsTerminal() ?? false) && DateTime.UtcNow < deadline) await Task.Delay(10);
        var polled = await h.Call("poll_execution", new { execution_id = "exec-1", timeout_ms = 0 });
        Assert.Equal("success", Status(polled));
    }

    [Fact]
    public async Task CaptureView_IsBusyWhileAScriptRuns_AndUnknownWithoutACaptureService()
    {
        // Without a capture service the method is unknown (a bridge build without it answers legibly).
        var h = new Harness();
        Assert.Equal("unknown-method", Code(await h.Call("capture_view", new { target = "active" })));
        // With one, a queued run makes it busy.
        var deferred = new DeferredLauncher();
        var runner = new RoslynScriptRunner(); runner.WarmupCompile();
        var d = new RequestDispatcher(h.Manager, new UndoRunExecutor(new ScriptRunners(runner), h.Host), deferred, h.Logs.Add, () => h.Now, _ => Task.CompletedTask,
            capture: new Core.Capture.ViewCaptureService(new NoViewports()), onMainThread: f => f());
        var first = await d.DispatchAsync(JsonRpcRequest.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"execute_script\",\"params\":{\"execution_id\":\"a\",\"language\":\"csharp\",\"script\":\"return 1;\",\"timeout_ms\":0}}"), CancellationToken.None);
        Assert.Contains("\"status\":\"pending\"", first);
        var cap = await d.DispatchAsync(JsonRpcRequest.Parse("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"capture_view\",\"params\":{\"target\":\"active\"}}"), CancellationToken.None);
        Assert.Contains("\"status\":\"busy\"", cap);
        deferred.RunAll();
        // Idle again: the fake has no viewports, so the request reaches the service and gets its record.
        var cap2 = await d.DispatchAsync(JsonRpcRequest.Parse("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"capture_view\",\"params\":{\"target\":\"active\"}}"), CancellationToken.None);
        Assert.Contains("no-viewports", cap2);
    }

    private sealed class NoViewports : Core.Capture.IViewCapture
    {
        public IReadOnlyList<string> ViewportNames(object document) => Array.Empty<string>();
        public (int Width, int Height) ViewportSize(object document, string viewport) => (1, 1);
        public IReadOnlyList<string> DisplayModeNames() => Array.Empty<string>();
        public (byte[] Bytes, string? RestoreFailure) Capture(object document, string viewport, int width, int height, string? displayMode, string zoom, bool transparent, bool grid, bool axes, string mimeType) => (Array.Empty<byte>(), null);
    }

    [Fact]
    public async Task UndoRedo_GoesThroughTheBusyGate_AndIsPollable()
    {
        var deferred = new DeferredLauncher();
        var h = new Harness(deferred);
        h.Host.DuringRun = on => on(new DocumentChange(DocumentChange.Kind.Added, Guid.NewGuid(), "Brep", "Default"));
        var first = await h.Execute("return 1;", id: "a", extra: new { timeout_ms = 0 });
        Assert.Equal("pending", Status(first));
        // An undo arriving mid-script is busy pointing at the script.
        var busy = await h.Call("undo_redo", new { execution_id = "u-1", direction = "undo" });
        Assert.Equal("busy", Status(busy));
        Assert.Equal("a", busy.GetProperty("result").GetProperty("execution_id").GetString());
        deferred.RunAll();

        var undo = await h.Call("undo_redo", new { execution_id = "u-2", direction = "undo", timeout_ms = 0 });
        Assert.Equal("pending", Status(undo));
        deferred.RunAll();
        var polled = await h.Call("poll_execution", new { execution_id = "u-2" });
        Assert.Equal("success", Status(polled));
        Assert.Equal("undo-reverted-connector-work", polled.GetProperty("result").GetProperty("notices")[0].GetProperty("code").GetString());
        Assert.Equal(1, h.Host.UndoCalls);
    }

    [Fact]
    public async Task UndoRedo_CancelledWhileQueued_NeverRuns()
    {
        var deferred = new DeferredLauncher();
        var h = new Harness(deferred);
        h.Host.DuringRun = on => on(new DocumentChange(DocumentChange.Kind.Added, Guid.NewGuid(), "Brep", "Default"));
        await h.Execute("return 1;", id: "a", extra: new { timeout_ms = 0 });
        deferred.RunAll();
        var undo = await h.Call("undo_redo", new { execution_id = "u-1", direction = "undo", timeout_ms = 0 });
        Assert.Equal("pending", Status(undo));
        Assert.Equal("cancelled", Status(await h.Call("cancel_execution", new { execution_id = "u-1" })));
        deferred.RunAll();
        Assert.Equal(0, h.Host.UndoCalls);
        Assert.Equal("cancelled", Status(await h.Call("poll_execution", new { execution_id = "u-1", timeout_ms = 0 })));
    }

    [Fact]
    public async Task UndoRedo_UpdatesLastRun_ButNotTheGatesEvidence()
    {
        var h = new Harness();
        h.Host.DuringRun = on => on(new DocumentChange(DocumentChange.Kind.Added, Guid.NewGuid(), "Brep", "Default"));
        await h.Execute("return 1;", id: "a");
        var undo = await h.Call("undo_redo", new { execution_id = "u-1", direction = "undo" });
        Assert.Equal("success", Status(undo));
        Assert.Equal("undo", h.Dispatcher.Ledger.Get("tmp-known")!.Status);
        Assert.Equal("a", h.Dispatcher.Ledger.LastChanging("tmp-known")!.ExecutionId);
        // The run after the undo sees the undo as last_run.
        var next = await h.Execute("return 2;", id: "b");
        Assert.Equal("u-1", next.GetProperty("result").GetProperty("last_run").GetProperty("execution_id").GetString());
    }

    [Fact]
    public async Task UndoRedo_BadDirection_AndRefusalCode()
    {
        var h = new Harness();
        Assert.Equal("invalid-params", Code(await h.Call("undo_redo", new { execution_id = "u", direction = "sideways" })));
        // No connector run has changed the document: nothing is provably ours.
        var refused = await h.Call("undo_redo", new { execution_id = "u-1", direction = "undo" });
        Assert.Equal("error", Status(refused));
        Assert.Equal("undo-confirmation-required", Code(refused));
        Assert.Equal(0, h.Host.UndoCalls);
    }

    [Fact]
    public async Task LastRun_IsRecordedPerDocument_AndThePreviousOneRidesTheNextResult()
    {
        var h = new Harness();
        h.Host.DuringRun = on => on(new DocumentChange(DocumentChange.Kind.Added, Guid.NewGuid(), "Brep", "Default"));
        var first = await h.Call("execute_script", new { execution_id = "a", language = "csharp", script = "return 1;", agent_client_id = "srv-a", label = "first" });
        Assert.Equal("success", Status(first));
        Assert.False(first.GetProperty("result").TryGetProperty("last_run", out _));
        var last = h.Dispatcher.Ledger.Get("tmp-known")!;
        Assert.Equal("a", last.ExecutionId);
        Assert.Equal("srv-a", last.AgentClientId);
        Assert.Equal("first", last.Label);
        Assert.True(last.ChangedDocument);
        Assert.Equal("success", last.Status);

        var second = await h.Call("execute_script", new { execution_id = "b", language = "csharp", script = "return 2;", agent_client_id = "srv-b" });
        var previous = second.GetProperty("result").GetProperty("last_run");
        Assert.Equal("a", previous.GetProperty("execution_id").GetString());
        Assert.Equal("srv-a", previous.GetProperty("agent_client_id").GetString());
        Assert.Equal("b", h.Dispatcher.Ledger.Get("tmp-known")!.ExecutionId);
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
