using Eichler.Connectors.Rhino;
using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.Core.Execution.Python;
using Rhino.MCPBridge.Core.Tests.Fakes;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Execution.Python;

public sealed class PythonScriptRunnerTests
{
    private static (PythonScriptRunner Runner, FakePythonHost Host) Make()
    {
        var host = new FakePythonHost();
        host.EnsureLoaded();
        return (new PythonScriptRunner(host), host);
    }

    [Fact]
    public async Task BindsTheFourGlobals_PrefixesTheShebangAndPreamble_AndReadsResultBack()
    {
        var (runner, host) = Make();
        host.OnRun = (_, _) => new PythonRunResult { Result = 42, StdOut = "hello\n" };
        using var cts = new CancellationTokenSource();
        var outcome = await runner.RunAsync("result = 42\nprint('hello')", TestGlobals.Create(cts.Token, "lbl"), cts.Token, false);

        Assert.True(outcome.Success);
        Assert.Equal(42, outcome.ReturnValue);
        Assert.Equal("hello\n", outcome.StdOut);
        Assert.Equal(2, host.Runs.Count);
        var run = host.Runs[0];
        Assert.Equal(PythonScriptRunner.RestoreScriptContext, host.Runs[1].Text);
        Assert.StartsWith(PythonScriptRunner.Prefix, run.Text);
        Assert.EndsWith("result = 42\nprint('hello')", run.Text);
        Assert.Equal("result", run.ResultName);
        Assert.Equal(PythonScriptRunner.GlobalNames.OrderBy(n => n), run.Inputs.Keys.OrderBy(n => n));
        Assert.Null(run.Inputs["ghdoc"]);
        Assert.IsType<Connector>(run.Inputs["connector"]);
        var cancel = Assert.IsType<CancelSignal>(run.Inputs["cancel"]);
        Assert.False(cancel.IsRequested);
        cts.Cancel();
        Assert.True(cancel.IsRequested);
        Assert.Throws<OperationCanceledException>(cancel.Check);
    }

    [Fact]
    public async Task PreflightRefusals_NeverReachTheHost()
    {
        var (runner, host) = Make();
        var denied = await runner.RunAsync("import rhinoscriptsyntax as rs\nrs.GetPoint()", TestGlobals.Create(), default, false);
        Assert.False(denied.Success);
        Assert.IsType<ScriptApiDenylistViolationException>(denied.Exception);
        var gated = runner.TryPreflight("doc.Save()", confirmLifecycleActions: false)!;
        Assert.Equal(ScriptApiDenylistViolationException.ConfirmationRequiredCode, ((ScriptApiDenylistViolationException)gated.Exception!).Code);
        Assert.Null(runner.TryPreflight("doc.Save()", confirmLifecycleActions: true));
        Assert.Empty(host.Runs);
    }

    [Fact]
    public async Task HostError_BecomesAPythonScriptException_WithTheShiftedTraceback()
    {
        var (runner, host) = Make();
        host.OnRun = (_, _) => new PythonRunResult
        {
            Error = new InvalidOperationException("boom from python"),
            StdOut = "before\n",
            StdErr = "Traceback (most recent call last):\n  File \"<string>\", line 6, in <module>\n  File \"/lib/rhinoscriptsyntax.py\", line 120, in AddCircle\nRuntimeError: boom from python\n",
        };
        var outcome = await runner.RunAsync("x = 1\ny = 2\nraise RuntimeError('boom from python')", TestGlobals.Create(), default, false);
        Assert.False(outcome.Success);
        Assert.False(outcome.WasCancelled);
        Assert.Equal("before\n", outcome.StdOut);
        var ex = Assert.IsType<PythonScriptException>(outcome.Exception);
        Assert.Equal("boom from python", ex.Message);
        Assert.Contains("File \"<script>\", line 3, in <module>", ex.Traceback);
        // Library frames keep their numbers.
        Assert.Contains("File \"/lib/rhinoscriptsyntax.py\", line 120, in AddCircle", ex.Traceback);
        Assert.False(ex.IsSyntaxError);
    }

    [Fact]
    public void SyntaxErrors_AreRecognised_FromRhinoCodesCompileErrorAndFromATraceback()
    {
        // The live shape (RhinoCode 8.35): message "Compile Error", stderr with the staged file and [line:col].
        var live = new PythonScriptException(new Exception("Compile Error"), PythonScriptRunner.ShiftTraceback("Compile Error\ninvalid syntax  (Error CPYC01) file:///Users/me/.rhinocode/stage/3fw0rzwd.uxl:[4:1]\n"));
        Assert.True(live.IsSyntaxError);
        Assert.Contains("<script>:[1:1]", live.Traceback);
        Assert.Equal("Compile Error: invalid syntax  (Error CPYC01) <script>:[1:1]", live.Message);
        var raised = new PythonScriptException(new Exception("invalid syntax"), "  File \"<string>\", line 3\n    x = = 1\nSyntaxError: invalid syntax\n");
        Assert.True(raised.IsSyntaxError);
        Assert.False(new PythonScriptException(new Exception("boom"), "ValueError: boom").IsSyntaxError);
    }

    [Fact]
    public async Task ErrorAfterCancellation_IsACancelledOutcome()
    {
        var (runner, host) = Make();
        using var cts = new CancellationTokenSource();
        host.OnRun = (_, inputs) =>
        {
            cts.Cancel();
            try { ((CancelSignal)inputs["cancel"]!).Check(); }
            catch (OperationCanceledException oce) { return new PythonRunResult { Error = oce, StdOut = "partial" }; }
            throw new Exception("unreachable");
        };
        var outcome = await runner.RunAsync("cancel.Check()", TestGlobals.Create(cts.Token), cts.Token, false);
        Assert.True(outcome.WasCancelled);
        Assert.Equal("partial", outcome.StdOut);
    }

    [Fact]
    public async Task ErrorThatIsNotACancellation_KeepsItsOwnError_EvenWhenTheTokenIsSet()
    {
        var (runner, host) = Make();
        using var cts = new CancellationTokenSource();
        host.OnRun = (_, _) => { cts.Cancel(); return new PythonRunResult { Error = new Exception("boom"), StdErr = "ValueError: boom" }; };
        var outcome = await runner.RunAsync("raise ValueError('boom')", TestGlobals.Create(cts.Token), cts.Token, false);
        Assert.False(outcome.WasCancelled);
        Assert.Equal("boom", outcome.Exception!.Message);
    }

    [Fact]
    public async Task StdErrOnASuccessfulRun_IsAppendedToOutput_Marked()
    {
        var (runner, host) = Make();
        host.OnRun = (_, _) => new PythonRunResult { StdOut = "out\n", StdErr = "warn\n" };
        var outcome = await runner.RunAsync("pass", TestGlobals.Create(), default, false);
        Assert.Equal("out\n[stderr]\nwarn\n", outcome.StdOut);
    }

    [Fact]
    public void ScriptContextIsRestored_EvenWhenTheHostThrows()
    {
        var (runner, host) = Make();
        host.OnRun = (_, _) => throw new InvalidOperationException("host broke");
        Assert.Throws<InvalidOperationException>(() => runner.RunAsync("pass", TestGlobals.Create(), default, false).GetAwaiter().GetResult());
        Assert.Equal(PythonScriptRunner.RestoreScriptContext, host.Runs[^1].Text);
    }

    [Fact]
    public void AMessageMentioningSyntaxError_IsNotASyntaxError()
    {
        Assert.False(new PythonScriptException(new Exception("SyntaxError in the input file"), "Traceback...\nValueError: SyntaxError in the input file\n").IsSyntaxError);
        Assert.True(new PythonScriptException(new Exception("x"), "  File \"<script>\", line 1\n    x = = 1\n        ^\nSyntaxError: invalid syntax\n").IsSyntaxError);
    }

    [Fact]
    public async Task UnavailableHost_IsAFailedOutcome_NotACrash()
    {
        var host = new FakePythonHost { UnavailableReason = "still loading" };
        var runner = new PythonScriptRunner(host);
        Assert.Equal("still loading", runner.UnavailableReason);
        var outcome = await runner.RunAsync("result = 1", TestGlobals.Create(), default, false);
        Assert.False(outcome.Success);
        Assert.Contains("still loading", outcome.Exception!.Message);
        Assert.Empty(host.Runs);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("File \"<string>\", line 1, in <module>", "File \"<string>\", line 1, in <module>")]
    [InlineData("File \"<string>\", line 2, in <module>", "File \"<string>\", line 2, in <module>")]
    [InlineData("File \"<string>\", line 3, in <module>", "File \"<string>\", line 3, in <module>")]
    [InlineData("File \"<string>\", line 4, in <module>", "File \"<script>\", line 1, in <module>")]
    [InlineData("File \"file:///Users/me/.rhinocode/stage/5i05zwmq.4ha\", line 6, in <module>", "File \"<script>\", line 3, in <module>")]
    [InlineData("File \"file:///Users/me/.rhinocode/stage/5i05zwmq.4ha\", line 2, in <module>", "File \"file:///Users/me/.rhinocode/stage/5i05zwmq.4ha\", line 2, in <module>")]
    [InlineData("invalid syntax  (Error CPYC01) file:///Users/me/.rhinocode/stage/x.y:[5:3]", "invalid syntax  (Error CPYC01) <script>:[2:3]")]
    [InlineData("invalid syntax  (Error CPYC01) file:///Users/me/.rhinocode/stage/x.y:[1:1]", "invalid syntax  (Error CPYC01) file:///Users/me/.rhinocode/stage/x.y:[1:1]")]
    [InlineData("File \"C:\\Users\\me\\.rhinocode\\stage\\ab.cd\", line 5, in f", "File \"<script>\", line 2, in f")]
    [InlineData("File \"/site-rhinopython/rhinoscript/curve.py\", line 176, in AddCircle", "File \"/site-rhinopython/rhinoscript/curve.py\", line 176, in AddCircle")]
    public void ShiftTraceback_LeavesPrefixFramesAlone(string input, string expected)
    {
        Assert.Equal(expected, PythonScriptRunner.ShiftTraceback(input));
    }
}
