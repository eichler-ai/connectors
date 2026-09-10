using System.Reflection;
using System.Text.Json;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.PlugIns;

[assembly: PlugInDescription(DescriptionType.Organization, "Eichler spike")]
[assembly: System.Runtime.InteropServices.Guid("6f3f7a0e-8d0c-4c58-9d2a-5b3d3c2b1a10")]

namespace MCPSpike;

public sealed class MCPSpikePlugIn : PlugIn
{
    public static MCPSpikePlugIn? Instance { get; private set; }
    public MCPSpikePlugIn() { Instance = this; }
    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;
}

static class Report
{
    public static readonly string Dir = Environment.GetEnvironmentVariable("MCPSPIKE_OUT") ?? Path.Combine(Path.GetTempPath(), "mcpspike");
    public static readonly List<object> Steps = new();
    public static void Step(string name, object data) { Steps.Add(new { name, data }); Flush(); }
    public static void Flush()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "report.json"), JsonSerializer.Serialize(Steps, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// Dumps the public surface of Rhino.Runtime.Code that matters for hosting scripts.
[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikeApi : Command
{
    public override string EnglishName => "MCPSpikeApi";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            var asm = typeof(Rhino.Runtime.Code.RhinoCode).Assembly;
            var wanted = new[] { "RhinoCode", "RunContext", "Code", "ILanguage", "ILanguages", "LanguageSpec", "ContextInputs", "ContextOutputs", "ExecuteException", "IRunContext", "CodeRunner", "ExecutionException", "RunOptions", "CodeContext", "ILanguageLibrary" };
            var dump = new Dictionary<string, object>();
            foreach (var t in asm.GetExportedTypes().Where(t => wanted.Contains(t.Name) || t.Namespace == "Rhino.Runtime.Code.Execution"))
            {
                var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Select(m => m switch
                    {
                        MethodInfo mi when !mi.IsSpecialName => $"{mi.ReturnType.Name} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})",
                        PropertyInfo pi => $"{pi.PropertyType.Name} {pi.Name} {{{(pi.CanRead ? "get;" : "")}{(pi.CanWrite ? "set;" : "")}}}",
                        EventInfo ei => $"event {ei.EventHandlerType?.Name} {ei.Name}",
                        ConstructorInfo ci => $"ctor({string.Join(", ", ci.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})",
                        _ => null
                    }).Where(s => s != null).Distinct().OrderBy(s => s).ToList();
                dump[$"{t.Namespace}.{t.Name}" + (t.IsInterface ? " (interface)" : "") + (t.BaseType != null && t.BaseType != typeof(object) ? " : " + t.BaseType.Name : "")] = members!;
            }
            Report.Step("api-surface", dump);
            Report.Step("env", new { rhino = RhinoApp.Version.ToString(), clr = Environment.Version.ToString(), thread = Thread.CurrentThread.ManagedThreadId, inCommand = Command.InCommand(), inScriptRunner = Command.InScriptRunnerCommand() });
            return Result.Success;
        }
        catch (Exception ex) { Report.Step("api-surface-error", ex.ToString()); return Result.Failure; }
    }
}

/// Runs a Python 3 script through RhinoCode.RunScript with a RunContext, capturing outputs.
[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikePy : Command
{
    public override string EnglishName => "MCPSpikePy";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            var script = File.ReadAllText(Path.Combine(Report.Dir, "py_in.py"));
            var ctx = new Rhino.Runtime.Code.Execution.RunContext { AutoApplyParams = true };
            ctx.Inputs.Set("x", 21);
            ctx.Outputs.Set("result", (object?)null);
            var ms0 = new MemoryStream();
            ctx.OutputStream = ms0;
            var t0 = DateTime.UtcNow;
            Rhino.Runtime.Code.RhinoCode.RunScript(script, ctx);
            var ms = (DateTime.UtcNow - t0).TotalMilliseconds;
            object? result = null; try { result = ctx.Outputs.Get<object>("result"); } catch (Exception e) { result = "get failed: " + e.Message; }
            Report.Step("python-run", new { ms, stdout = System.Text.Encoding.UTF8.GetString(ms0.ToArray()), result = result?.ToString(), resultType = result?.GetType().FullName, thread = Thread.CurrentThread.ManagedThreadId });
            return Result.Success;
        }
        catch (Exception ex) { Report.Step("python-run-error", ex.ToString()); return Result.Failure; }
    }
}

/// Adds objects inside this command; a background thread then posts _-Undo after the command has
/// returned and records whether the objects went away (the mid-run Undo() did nothing, per the CLI spike).
[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikeUndo : Command
{
    public override string EnglishName => "MCPSpikeUndo";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            var before0 = doc.Objects.Count; Report.Step("before", new { before0, undoActive = doc.UndoActive });
            var before = doc.Objects.Count;
            var sn = doc.BeginUndoRecord("MCP: spike undo");
            doc.Objects.AddSphere(new Sphere(Point3d.Origin, 5));
            doc.Objects.AddSphere(new Sphere(new Point3d(20, 0, 0), 5));
            doc.EndUndoRecord(sn);
            var midUndo = doc.Undo();
            var afterMid = doc.Objects.Count;
            Report.Step("undo-in-command", new { before, added = afterMid, midUndoReturned = midUndo, undoActive = doc.UndoActive });
            var serial = doc.RuntimeSerialNumber;
            new Thread(() =>
            {
                Thread.Sleep(500);
                int afterCmd = -1, afterUndo = -1; bool undoActiveAfterCmd = false, ran = false;
                RhinoApp.InvokeOnUiThread(() =>
                {
                    var d = RhinoDoc.FromRuntimeSerialNumber(serial);
                    afterCmd = d.Objects.Count; undoActiveAfterCmd = d.UndoActive;
                    ran = RhinoApp.RunScript("_-Undo", false);
                    afterUndo = d.Objects.Count;
                });
                Thread.Sleep(500);
                RhinoApp.InvokeOnUiThread(() =>
                {
                    var d = RhinoDoc.FromRuntimeSerialNumber(serial);
                    Report.Step("undo-after-command", new { afterCmd, undoActiveAfterCmd, undoCommandRan = ran, afterUndoImmediate = afterUndo, afterUndoLater = d.Objects.Count, redoActive = d.RedoActive });
                });
            }).Start();
            return Result.Success;
        }
        catch (Exception ex) { Report.Step("undo-error", ex.ToString()); return Result.Failure; }
    }
}

/// Calls RhinoCode.RunScript from a background thread directly, and via InvokeOnUiThread, to see which works.
[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikeThread : Command
{
    public override string EnglishName => "MCPSpikeThread";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var script = "#! python 3\nimport threading, scriptcontext as sc\nresult = (threading.current_thread().name, sc.doc.Objects.Count if sc.doc else -1)\n";
        new Thread(() =>
        {
            try
            {
                var ctx = new Rhino.Runtime.Code.Execution.RunContext { AutoApplyParams = true };
                ctx.Outputs.Set("result", (object?)null);
                Rhino.Runtime.Code.RhinoCode.RunScript(script, ctx);
                Report.Step("python-from-background-thread", new { ok = true, result = ctx.Outputs.Get<object>("result")?.ToString() });
            }
            catch (Exception ex) { Report.Step("python-from-background-thread", new { ok = false, error = ex.GetType().Name + ": " + ex.Message }); }
            RhinoApp.InvokeOnUiThread(() =>
            {
                try
                {
                    var ctx = new Rhino.Runtime.Code.Execution.RunContext { AutoApplyParams = true };
                    ctx.Outputs.Set("result", (object?)null);
                    Rhino.Runtime.Code.RhinoCode.RunScript(script, ctx);
                    Report.Step("python-via-invoke-on-ui-thread", new { ok = true, result = ctx.Outputs.Get<object>("result")?.ToString(), inCommand = Command.InCommand() });
                }
                catch (Exception ex) { Report.Step("python-via-invoke-on-ui-thread", new { ok = false, error = ex.GetType().Name + ": " + ex.Message }); }
            });
        }).Start();
        return Result.Success;
    }
}

static class UndoProbe
{
    /// After the calling command has returned, try three undo mechanisms in turn on the UI thread, reporting counts after each.
    public static void PostUndoAndReport(string label, uint serial, int expectedBefore)
    {
        new Thread(() =>
        {
            Thread.Sleep(700);
            var steps = new List<object>();
            void Snap(string what, object extra) => RhinoApp.InvokeOnUiThread(() => { var d = RhinoDoc.FromRuntimeSerialNumber(serial); steps.Add(new { what, objects = d.Objects.Count, undoActive = d.UndoActive, redoActive = d.RedoActive, inCommand = Command.InCommand(), extra }); });
            Snap("after-command", null);
            bool api = false; RhinoApp.InvokeOnUiThread(() => { api = RhinoDoc.FromRuntimeSerialNumber(serial).Undo(); });
            Thread.Sleep(500); Snap("after-doc.Undo()", api);
            bool rs1 = false; RhinoApp.InvokeOnUiThread(() => { rs1 = RhinoApp.RunScript("_Undo", true); });
            Thread.Sleep(500); Snap("after-RunScript(_Undo,echo)", rs1);
            bool rs2 = false; RhinoApp.InvokeOnUiThread(() => { rs2 = RhinoApp.RunScript(serial, "_Undo", true); });
            Thread.Sleep(500); Snap("after-RunScript(serial,_Undo)", rs2);
            RhinoApp.InvokeOnUiThread(() => Report.Step(label, new { expectedBefore, steps }));
        }).Start();
    }
}

/// B: objects added inside a command under our own record, NO mid-command Undo; _-Undo posted after.
[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikeUndoB : Command
{
    public override string EnglishName => "MCPSpikeUndoB";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var before0 = doc.Objects.Count; Report.Step("before", new { before0, undoActive = doc.UndoActive });
        var sn = doc.BeginUndoRecord("MCP: spike B");
        doc.Objects.AddSphere(new Sphere(Point3d.Origin, 5));
        doc.Objects.AddSphere(new Sphere(new Point3d(20, 0, 0), 5));
        doc.EndUndoRecord(sn);
        UndoProbe.PostUndoAndReport("undoB-own-record-then-undo-after-command", doc.RuntimeSerialNumber, 2);
        return Result.Success;
    }
}

/// C: a Python script adds objects through RhinoCode with RecordDocumentUndo=true; _-Undo posted after.
[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikeUndoC : Command
{
    public override string EnglishName => "MCPSpikeUndoC";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var before0 = doc.Objects.Count; Report.Step("before", new { before0, undoActive = doc.UndoActive });
        var ctx = new Rhino.Runtime.Code.Execution.RunContext { AutoApplyParams = true, RecordDocumentUndo = true };
        try { Rhino.Runtime.Code.RhinoCode.RunScript(File.ReadAllText(Path.Combine(Report.Dir, "py_undo.py")), ctx); } catch (Exception ex) { Report.Step("undoC-error", ex.ToString()); }
        Report.Step("undoC-python-added", new { objects = doc.Objects.Count, undoActive = doc.UndoActive });
        UndoProbe.PostUndoAndReport("undoC-python-RecordDocumentUndo-then-undo-after-command", doc.RuntimeSerialNumber, 3);
        return Result.Success;
    }
}

/// D: objects added from InvokeOnUiThread OUTSIDE any command, under our own record; _-Undo posted after.
[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikeUndoD : Command
{
    public override string EnglishName => "MCPSpikeUndoD";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var before0 = doc.Objects.Count; Report.Step("before", new { before0, undoActive = doc.UndoActive });
        var serial = doc.RuntimeSerialNumber;
        new Thread(() =>
        {
            Thread.Sleep(700);
            RhinoApp.InvokeOnUiThread(() =>
            {
                var d = RhinoDoc.FromRuntimeSerialNumber(serial);
                var sn = d.BeginUndoRecord("MCP: spike D");
                d.Objects.AddSphere(new Sphere(Point3d.Origin, 5));
                d.Objects.AddSphere(new Sphere(new Point3d(20, 0, 0), 5));
                var ended = d.EndUndoRecord(sn);
                Report.Step("undoD-added-outside-command", new { objects = d.Objects.Count, inCommand = Command.InCommand(), ended, undoActive = d.UndoActive });
                UndoProbe.PostUndoAndReport("undoD-outside-command-then-undo", serial, 2);
            });
        }).Start();
        return Result.Success;
    }
}

/// E: from a background thread, run our own command via ExecuteCommand, then undo it via ExecuteCommand(_Undo), then via SendKeystrokes.
[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikeAddTwo : Command
{
    public static int Runs;
    public override string EnglishName => "MCPSpikeAddTwo";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var run = ++Runs;
        var sn = doc.BeginUndoRecord("MCP: add two #" + run);
        var a = new Rhino.DocObjects.ObjectAttributes { Name = "run" + run + "-a" };
        var b = new Rhino.DocObjects.ObjectAttributes { Name = "run" + run + "-b" };
        doc.Objects.AddSphere(new Sphere(Point3d.Origin, 5), a);
        doc.Objects.AddSphere(new Sphere(new Point3d(20, 0, 0), 5), b);
        doc.EndUndoRecord(sn);
        return Result.Success;
    }
}

[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikeE : Command
{
    public override string EnglishName => "MCPSpikeE";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var serial = doc.RuntimeSerialNumber;
        new Thread(() =>
        {
            Thread.Sleep(700);
            var steps = new List<object>();
            void Snap(string what, object extra) => RhinoApp.InvokeOnUiThread(() => { var d = RhinoDoc.FromRuntimeSerialNumber(serial); steps.Add(new { what, objects = d.Objects.Count, names = string.Join(",", d.Objects.Select(o => o.Name).OrderBy(n => n)), undoActive = d.UndoActive, redoActive = d.RedoActive, extra }); });
            Snap("start", null);
            object r1 = null!; RhinoApp.InvokeOnUiThread(() => { try { r1 = RhinoApp.ExecuteCommand(RhinoDoc.FromRuntimeSerialNumber(serial), "_MCPSpikeAddTwo").ToString(); } catch (Exception ex) { r1 = ex.GetType().Name + ": " + ex.Message; } });
            Thread.Sleep(1500); Snap("after-ExecuteCommand(AddTwo)", r1);
            object r2 = null!; RhinoApp.InvokeOnUiThread(() => { try { r2 = RhinoApp.ExecuteCommand(RhinoDoc.FromRuntimeSerialNumber(serial), "_Undo").ToString(); } catch (Exception ex) { r2 = ex.GetType().Name + ": " + ex.Message; } });
            Thread.Sleep(1500); Snap("after-ExecuteCommand(_Undo)", r2);
            object r3 = null!; RhinoApp.InvokeOnUiThread(() => { try { r3 = RhinoApp.ExecuteCommand(RhinoDoc.FromRuntimeSerialNumber(serial), "_MCPSpikeAddTwo").ToString(); } catch (Exception ex) { r3 = ex.GetType().Name + ": " + ex.Message; } });
            Thread.Sleep(1500); Snap("after-ExecuteCommand(AddTwo)-again", r3);
            RhinoApp.InvokeOnUiThread(() => { try { RhinoApp.SendKeystrokes("_Undo", true); } catch (Exception ex) { steps.Add(new { what = "sendkeys-error", error = ex.Message }); } });
            Thread.Sleep(1500); Snap("after-SendKeystrokes(_Undo)", null);
            RhinoApp.InvokeOnUiThread(() => Report.Step("E-run-and-undo-from-background", new { steps }));
        }).Start();
        return Result.Success;
    }
}

/// C2: vary the RunContext to find what makes language detection fail.
[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikeUndoC2 : Command
{
    public override string EnglishName => "MCPSpikeUndoC2";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var text = File.ReadAllText(Path.Combine(Report.Dir, "py_in.py"));
        void Try(string label, Rhino.Runtime.Code.Execution.RunContext ctx)
        {
            try { Rhino.Runtime.Code.RhinoCode.RunScript(text, ctx); Report.Step(label, new { ok = true, objects = doc.Objects.Count, undoActive = doc.UndoActive }); }
            catch (Exception ex) { Report.Step(label, new { ok = false, error = ex.GetType().Name + ": " + ex.Message }); }
        }
        Try("C2-bare-ctx", new Rhino.Runtime.Code.Execution.RunContext { AutoApplyParams = true });
        var c1 = new Rhino.Runtime.Code.Execution.RunContext { AutoApplyParams = true }; c1.Inputs.Set("x", 21); c1.Outputs.Set("result", (object?)null);
        Try("C2-inputs-outputs", c1);
        var c2 = new Rhino.Runtime.Code.Execution.RunContext { AutoApplyParams = true }; c2.Inputs.Set("x", 21); c2.Outputs.Set("result", (object?)null); c2.OutputStream = new MemoryStream();
        Try("C2-inputs-outputs-stream", c2);
        var c3 = new Rhino.Runtime.Code.Execution.RunContext { AutoApplyParams = true, RecordDocumentUndo = true }; c3.Inputs.Set("x", 21); c3.Outputs.Set("result", (object?)null); c3.OutputStream = new MemoryStream();
        Try("C2-inputs-outputs-stream-RecordDocumentUndo", c3);
        var c4 = new Rhino.Runtime.Code.Execution.RunContext(true, true) { AutoApplyParams = true }; c4.Inputs.Set("x", 21); c4.Outputs.Set("result", (object?)null);
        Try("C2-default-streams-ctor", c4);
        return Result.Success;
    }
}

[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikeLang : Command
{
    public override string EnglishName => "MCPSpikeLang";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            var reg = Rhino.Runtime.Code.RhinoCode.Languages;
            var t = reg.GetType();
            var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Select(m => m switch
                {
                    MethodInfo mi when !mi.IsSpecialName => $"{mi.ReturnType.Name} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})",
                    PropertyInfo pi => $"{pi.PropertyType.Name} {pi.Name}",
                    EventInfo ei => $"event {ei.Name}",
                    _ => null
                }).Where(x => x != null).Distinct().OrderBy(x => x).ToList();
            Report.Step("LanguageRegistry-" + t.FullName, members);
            var st = typeof(Rhino.Runtime.Code.Languages.ILanguageStatus);
            Report.Step("ILanguageStatus", st.GetMembers().Select(m => m.Name).Distinct().OrderBy(x => x).ToList());
        }
        catch (Exception ex) { Report.Step("lang-error", ex.ToString()); }
        return Result.Success;
    }
}

[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikeLangInit : Command
{
    public override string EnglishName => "MCPSpikeLangInit";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var text = File.ReadAllText(Path.Combine(Report.Dir, "py_in.py"));
        var reg = Rhino.Runtime.Code.RhinoCode.Languages;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var before = reg.QueryLatest(Rhino.Runtime.Code.Languages.LanguageSpec.Python3);
            Report.Step("lang-before", new { count = reg.Count, python3 = before?.Id.ToString(), all = reg.Query().Select(l => l.Id.ToString()).ToList() });
            reg.WaitStatusComplete(Rhino.Runtime.Code.Languages.LanguageSpec.Python3);
            var t1 = sw.ElapsedMilliseconds;
            var py = reg.QueryLatest(Rhino.Runtime.Code.Languages.LanguageSpec.Python3);
            Report.Step("lang-after-WaitStatusComplete", new { ms = t1, count = reg.Count, python3 = py?.Id.ToString(), waiting = py?.Status.IsWaiting });
            if (py != null) { py.Status.WaitReady(); Report.Step("lang-after-WaitReady", new { ms = sw.ElapsedMilliseconds, waiting = py.Status.IsWaiting }); }
            var ctx = new Rhino.Runtime.Code.Execution.RunContext { AutoApplyParams = true }; ctx.Inputs.Set("x", 21); ctx.Outputs.Set("result", (object?)null); ctx.OutputStream = new MemoryStream();
            var t2 = sw.ElapsedMilliseconds;
            Rhino.Runtime.Code.RhinoCode.RunScript(text, ctx);
            Report.Step("lang-run-after-init", new { runMs = sw.ElapsedMilliseconds - t2, stdout = System.Text.Encoding.UTF8.GetString(((MemoryStream)ctx.OutputStream).ToArray()) });
            var t3 = sw.ElapsedMilliseconds;
            Rhino.Runtime.Code.RhinoCode.RunScript(text, ctx);
            Report.Step("lang-run-second", new { runMs = sw.ElapsedMilliseconds - t3 });
        }
        catch (Exception ex) { Report.Step("lang-init-error", new { ms = sw.ElapsedMilliseconds, error = ex.ToString().Substring(0, Math.Min(600, ex.ToString().Length)) }); }
        return Result.Success;
    }
}

[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikePyAdd : Command
{
    public override string EnglishName => "MCPSpikePyAdd";
    public static int Runs;
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Rhino.Runtime.Code.RhinoCode.Languages.WaitStatusComplete(Rhino.Runtime.Code.Languages.LanguageSpec.Python3);
        var run = ++Runs;
        var ctx = new Rhino.Runtime.Code.Execution.RunContext { AutoApplyParams = true }; ctx.Inputs.Set("run", run); ctx.OutputStream = new MemoryStream(); ctx.ErrorStream = new MemoryStream();
        var script = "#! python 3\nimport scriptcontext as sc, Rhino\nfrom Rhino.Geometry import Sphere, Point3d\nfor tag in ('a','b'):\n    at = Rhino.DocObjects.ObjectAttributes(); at.Name = 'py%d-%s' % (run, tag)\n    sc.doc.Objects.AddSphere(Sphere(Point3d(0,0,0),5), at)\nif run == 2:\n    raise ValueError('boom from python')\n";
        try { Rhino.Runtime.Code.RhinoCode.RunScript(script, ctx); Report.Step("pyadd-run" + run, new { ok = true }); }
        catch (Exception ex) { Report.Step("pyadd-run" + run + "-exception", new { type = ex.GetType().FullName, message = ex.Message, stderr = System.Text.Encoding.UTF8.GetString(((MemoryStream)ctx.ErrorStream).ToArray()), inner = ex.InnerException?.GetType().FullName }); }
        return Result.Success;
    }
}

[CommandStyle(Style.ScriptRunner)]
public sealed class MCPSpikeF : Command
{
    public override string EnglishName => "MCPSpikeF";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var serial = doc.RuntimeSerialNumber;
        new Thread(() =>
        {
            Thread.Sleep(700);
            var steps = new List<object>();
            void Snap(string what) => RhinoApp.InvokeOnUiThread(() => { var d = RhinoDoc.FromRuntimeSerialNumber(serial); steps.Add(new { what, names = string.Join(",", d.Objects.Select(o => o.Name).OrderBy(n => n)) }); });
            void Exec(string cmd) { RhinoApp.InvokeOnUiThread(() => RhinoApp.ExecuteCommand(RhinoDoc.FromRuntimeSerialNumber(serial), cmd)); Thread.Sleep(1500); }
            Snap("start");
            Exec("_MCPSpikePyAdd"); Snap("after-py-run1");
            Exec("_MCPSpikePyAdd"); Snap("after-py-run2-which-throws-after-adding");
            Exec("_Undo"); Snap("after-undo-1");
            Exec("_Undo"); Snap("after-undo-2");
            Exec("_Redo"); Snap("after-redo");
            RhinoApp.InvokeOnUiThread(() => Report.Step("F", new { steps }));
        }).Start();
        return Result.Success;
    }
}
