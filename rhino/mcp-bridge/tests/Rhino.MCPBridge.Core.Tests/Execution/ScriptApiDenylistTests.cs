using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.Core.Tests.Fakes;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Execution;

/// <summary>
/// PRD §07/§08: every shape the guard blocks or gates, named per case (the Revit skill's rule: a test
/// pins a SHAPE, not one exploit's spelling). Compile-time only -- Document is never dereferenced, so
/// these run with no Rhino; RhinoCommon's managed metadata is enough to bind the members.
/// </summary>
public sealed class ScriptApiDenylistTests
{
    private static readonly RoslynScriptRunner Runner = new();

    private static ScriptExecutionOutcome? Preflight(string script, bool confirm = false) => Runner.TryPreflight(script, confirm);

    private static void AssertDenied(string script, string memberFragment)
    {
        var rejection = Preflight(script);
        Assert.NotNull(rejection);
        var ex = Assert.IsType<ScriptApiDenylistViolationException>(rejection!.Exception);
        Assert.Equal(ScriptApiDenylistViolationException.DeniedCode, ex.Code);
        Assert.Contains(memberFragment, ex.DeniedMember);
    }

    private static void AssertGated(string script, string memberFragment)
    {
        var rejection = Preflight(script);
        Assert.NotNull(rejection);
        var ex = Assert.IsType<ScriptApiDenylistViolationException>(rejection!.Exception);
        Assert.Equal(ScriptApiDenylistViolationException.ConfirmationRequiredCode, ex.Code);
        Assert.Contains(memberFragment, ex.DeniedMember);
        Assert.Null(Preflight(script, confirm: true)); // the flag lifts it
    }

    // --- hard-blocked: the undo record API (spikes §3) ---

    [Theory]
    [InlineData("Document.Undo();", "RhinoDoc.Undo")]
    [InlineData("Document.Redo();", "RhinoDoc.Redo")]
    [InlineData("var sn = Document.BeginUndoRecord(\"x\"); Document.EndUndoRecord(sn);", "RhinoDoc.BeginUndoRecord")]
    [InlineData("Document.ClearUndoRecords(true);", "RhinoDoc.ClearUndoRecords")]
    [InlineData("var d = Document; d.Undo();", "RhinoDoc.Undo")] // through a local
    [InlineData("System.Func<bool> f = () => Document.Undo(); return f();", "RhinoDoc.Undo")] // inside a lambda
    public void UndoRecordApi_IsDenied_WhateverTheSpelling(string script, string member) => AssertDenied(script, member);

    [Fact]
    public void RhinoAppExit_IsDenied() => AssertDenied("Rhino.RhinoApp.Exit();", "RhinoApp.Exit");

    [Theory]
    [InlineData("Rhino.RhinoApp.RunScript(\"_Exit\", false);", "exit")]
    [InlineData("Rhino.RhinoApp.RunScript(\"-_Quit\", false);", "quit")]
    [InlineData("Rhino.RhinoApp.RunScript(\"_Circle 0,0,0 5 _Exit\", false);", "exit")] // anywhere in the string
    public void RunScriptWithAnExitToken_IsDenied(string script, string token) => AssertDenied(script, token);

    // --- hard-blocked: interactive getters (PRD §08, prevention) ---

    [Theory]
    [InlineData("Rhino.Geometry.Point3d p; Rhino.Input.RhinoGet.GetPoint(\"pick\", false, out p);", "RhinoGet.GetPoint")]
    [InlineData("Rhino.DocObjects.ObjRef r; Rhino.Input.RhinoGet.GetOneObject(\"pick\", false, Rhino.DocObjects.ObjectType.AnyObject, out r);", "RhinoGet.GetOneObject")]
    [InlineData("var go = new Rhino.Input.Custom.GetObject();", "GetObject")]
    [InlineData("Rhino.Input.Custom.GetPoint gp = new();", "GetPoint")] // target-typed new
    [InlineData("var gs = new Rhino.Input.Custom.GetString(); gs.Get();", "GetString")]
    public void InteractiveGetters_AreDenied(string script, string member) => AssertDenied(script, member);

    [Fact]
    public void ADerivedGetter_IsDeniedByBaseType()
    {
        // The block is on the base class, so a script-declared subclass does not slip through.
        AssertDenied("class MyGet : Rhino.Input.Custom.GetPoint {} var g = new MyGet();", "MyGet");
    }

    // --- gated: document lifecycle (PRD §07) ---

    [Theory]
    [InlineData("Document.Save();", "RhinoDoc.Save")]
    [InlineData("Document.SaveAs(\"/tmp/x.3dm\");", "RhinoDoc.SaveAs")]
    [InlineData("Document.Export(\"/tmp/x.obj\");", "RhinoDoc.Export")]
    [InlineData("bool already; Rhino.RhinoDoc.Open(\"/tmp/x.3dm\", out already);", "RhinoDoc.Open")]
    [InlineData("Rhino.RhinoDoc.OpenFile(\"/tmp/x.3dm\");", "RhinoDoc.OpenFile")]
    [InlineData("Rhino.RhinoDoc.Create(null);", "RhinoDoc.Create")]
    [InlineData("Rhino.RhinoApp.RunScript(\"_-SaveAs /tmp/x.3dm _Enter\", false);", "saveas")]
    [InlineData("Rhino.RhinoApp.RunScript(\"_-Export /tmp/x.obj _Enter\", false);", "export")]
    public void LifecycleMembers_AreGated_AndTheFlagLiftsThem(string script, string member) => AssertGated(script, member);

    [Fact]
    public void SeveralGatedMembers_AreAllNamed()
    {
        var rejection = Preflight("Document.Save(); Document.Export(\"/tmp/x.obj\");");
        var ex = Assert.IsType<ScriptApiDenylistViolationException>(rejection!.Exception);
        Assert.Contains("RhinoDoc.Save", ex.DeniedMember);
        Assert.Contains("RhinoDoc.Export", ex.DeniedMember);
    }

    // --- not touched ---

    [Theory]
    [InlineData("var s = new System.IO.MemoryStream(); s.Close(); return 1;")] // Stream.Close is not RhinoDoc.Close
    [InlineData("var sphere = new Rhino.Geometry.Sphere(Rhino.Geometry.Point3d.Origin, 5); return sphere.Radius;")]
    [InlineData("Rhino.RhinoApp.RunScript(\"_Circle 0,0,0 5\", false);")] // a harmless command
    [InlineData("return Document.Objects.Count;")] // touches Document only at runtime
    public void OrdinaryScripts_AreNotRefused(string script) => Assert.Null(Preflight(script));

    [Fact]
    public void GlobalsBind_ByPrdCasing_AndConnectorIsTheConnectorType()
    {
        Assert.Null(Preflight("System.Func<Rhino.RhinoDoc> d = () => Document; System.Threading.CancellationToken t = CancellationToken; Eichler.Connectors.Rhino.Connector c = Connector; return 0;"));
        Assert.NotNull(Preflight("return document;")); // wrong casing does not bind
    }

    [Fact]
    public async Task ConnectorMembers_AreCallable()
    {
        var runner = new RoslynScriptRunner();
        var outcome = await runner.RunAsync("return Connector.BridgeVersion + \"/\" + (Connector.RunLabel ?? \"none\");", TestGlobals.Create(label: "hello"), CancellationToken.None);
        Assert.True(outcome.Success, outcome.Exception?.ToString());
        Assert.Equal("test/hello", outcome.ReturnValue);
    }

    [Fact]
    public void CommandTokens_StripPrefixesAndLowerCase()
    {
        Assert.Equal(new[] { "saveas", "/tmp/x.3dm", "enter" }, ScriptApiDenylist.CommandTokens("_-SaveAs /tmp/x.3dm _Enter").ToArray());
        Assert.Equal(new[] { "circle", "0,0,0", "5" }, ScriptApiDenylist.CommandTokens("! _Circle 0,0,0 5").ToArray());
    }
}
