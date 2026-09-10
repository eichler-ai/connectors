using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.Core.Execution.Python;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Execution.Python;

/// <summary>Every shape from ScriptApiDenylistTests, in Python (implementation-plan.md tier 1 "Python
/// guard"): aliased imports, star imports, the bound globals, getattr strings, command strings, and
/// the shapes that must NOT be refused.</summary>
public sealed class PythonScriptGuardTests
{
    private static ScriptApiDenylistViolationException Denied(string script)
    {
        var ex = Assert.Throws<ScriptApiDenylistViolationException>(() => PythonScriptGuard.Analyze(script));
        return ex;
    }

    // ---- hard-blocked: undo / exit --------------------------------------------------------------

    [Theory]
    [InlineData("doc.Undo()", "Rhino.RhinoDoc.Undo")]
    [InlineData("doc.BeginUndoRecord('x')", "Rhino.RhinoDoc.BeginUndoRecord")]
    [InlineData("import scriptcontext as sc\nsc.doc.Redo()", "Rhino.RhinoDoc.Redo")]
    [InlineData("import scriptcontext\nscriptcontext.doc.ClearUndoRecords()", "Rhino.RhinoDoc.ClearUndoRecords")]
    [InlineData("import Rhino\nRhino.RhinoDoc.ActiveDoc.Undo()", "Rhino.RhinoDoc.Undo")]
    [InlineData("from Rhino import RhinoDoc\nRhinoDoc.ActiveDoc.Undo()", "Rhino.RhinoDoc.Undo")]
    [InlineData("import Rhino as R\nR.RhinoApp.Exit()", "Rhino.RhinoApp.Exit")]
    [InlineData("from Rhino import RhinoApp as app\napp.Exit()", "Rhino.RhinoApp.Exit")]
    [InlineData("import rhinoscriptsyntax as rs\nrs.Exit()", "rhinoscriptsyntax.Exit")]
    [InlineData("import os\nos._exit(0)", "os._exit")]
    [InlineData("import os\nos.abort()", "os.abort")]
    [InlineData("u = getattr(doc, 'Undo')\nu()", "Rhino.RhinoDoc.Undo")]
    [InlineData("(doc).Undo()", "Rhino.RhinoDoc.Undo")]
    [InlineData("import scriptcontext\n((scriptcontext.doc)).Redo()", "Rhino.RhinoDoc.Redo")]
    [InlineData("x = f'{doc.Undo()}'", "Rhino.RhinoDoc.Undo")]
    [InlineData("import rhinoscriptsyntax as rs\nrs.Command('_-Undo')", "rhinoscriptsyntax.Command(\"undo\")")]
    [InlineData("import rhinoscriptsyntax as rs\nrs.Command(str('_Exit'))", "rhinoscriptsyntax.Command(\"exit\")")]
    [InlineData("import rhinoscriptsyntax as rs\nrs.Command('_-Undo' + 'Multiple')", "rhinoscriptsyntax.Command(\"undo\")")]
    [InlineData("import rhinoscriptsyntax as rs\nrs.Command(\"_Line 0,0,0 1,1,1 _Enter _Exit\", False)", "rhinoscriptsyntax.Command(\"exit\")")]
    [InlineData("import Rhino\nRhino.RhinoApp.RunScript('_Redo', False)", "Rhino.RhinoApp.RunScript(\"redo\")")]
    [InlineData("import Rhino\nRhino.RhinoApp.ExecuteCommand(doc, '_Quit')", "Rhino.RhinoApp.ExecuteCommand(\"quit\")")]
    [InlineData("from rhinoscriptsyntax import *\nCommand('_Undo')", "rhinoscriptsyntax.Command(\"undo\")")]
    public void UndoAndExit_AreDenied(string script, string member)
    {
        var ex = Denied(script);
        Assert.Equal(ScriptApiDenylistViolationException.DeniedCode, ex.Code);
        Assert.Equal(member, ex.DeniedMember);
        Assert.Contains("undo", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- hard-blocked: interactive getters and dialogs -----------------------------------------

    [Theory]
    [InlineData("import Rhino\nrc, pt = Rhino.Input.RhinoGet.GetPoint('pick', False)", "Rhino.Input.RhinoGet.GetPoint")]
    [InlineData("from Rhino.Input import RhinoGet\nRhinoGet.GetOneObject('x', False, 0)", "Rhino.Input.RhinoGet.GetOneObject")]
    [InlineData("from Rhino.Input import RhinoGet as g\ng.GetString('x', False, '')", "Rhino.Input.RhinoGet.GetString")]
    [InlineData("import Rhino.Input as ri\nri.RhinoGet.GetNumber('x', False, 0)", "Rhino.Input.RhinoGet.GetNumber")]
    [InlineData("from Rhino.Input import *\nRhinoGet.GetPoint('x', False)", "Rhino.Input.RhinoGet.GetPoint")]
    [InlineData("import rhinoscriptsyntax as rs\nobj = rs.GetObject('pick')", "rhinoscriptsyntax.GetObject")]
    [InlineData("import rhinoscriptsyntax as rs\npt = rs.GetPoint()", "rhinoscriptsyntax.GetPoint")]
    [InlineData("from rhinoscriptsyntax import GetString\nGetString('name')", "rhinoscriptsyntax.GetString")]
    [InlineData("from rhinoscriptsyntax import *\nobjs = GetObjects()", "rhinoscriptsyntax.GetObjects")]
    [InlineData("import rhinoscriptsyntax as rs\nrs.MessageBox('hi')", "rhinoscriptsyntax.MessageBox")]
    [InlineData("import rhinoscriptsyntax as rs\nrs.OpenFileName()", "rhinoscriptsyntax.OpenFileName")]
    [InlineData("import Rhino\ngp = Rhino.Input.Custom.GetPoint()\ngp.Get()", "Rhino.Input.Custom.GetPoint")]
    [InlineData("from Rhino.Input.Custom import GetObject\ngo = GetObject()", "Rhino.Input.Custom.GetObject")]
    [InlineData("import Rhino\nRhino.UI.Dialogs.ShowMessage('x', 'y')", "Rhino.UI.Dialogs.ShowMessage")]
    [InlineData("from Rhino.UI import Dialogs\nDialogs.ShowColorDialog(None)", "Rhino.UI.Dialogs.ShowColorDialog")]
    [InlineData("import rhinoscriptsyntax as rs\ngetattr(rs, 'GetPoint')('x')", "rhinoscriptsyntax.GetPoint")]
    [InlineData("import rhinoscriptsyntax as rs\nprint(f'{rs.GetPoint()}')", "rhinoscriptsyntax.GetPoint")]
    [InlineData("import rhinoscriptsyntax as rs\nprint(f'{rs.GetPoint()!r:>10}')", "rhinoscriptsyntax.GetPoint")]
    [InlineData("import rhinoscriptsyntax as rs\nprint(f'{{literal}} {rs.GetPoint()}')", "rhinoscriptsyntax.GetPoint")]
    public void InteractiveGetters_AreDenied(string script, string member)
    {
        var ex = Denied(script);
        Assert.Equal(ScriptApiDenylistViolationException.DeniedCode, ex.Code);
        Assert.Equal(member, ex.DeniedMember);
        Assert.Contains("programmatically", ex.Message);
    }

    // ---- hard-blocked: dynamic code ------------------------------------------------------------

    [Theory]
    [InlineData("exec('doc.Undo()')", "exec")]
    [InlineData("eval('1')", "eval")]
    [InlineData("compile('x', 'f', 'exec')", "compile")]
    [InlineData("m = __import__('Rhino')", "__import__")]
    [InlineData("import importlib\nimportlib.import_module('os')", "importlib.import_module")]
    [InlineData("import builtins\nbuiltins.exec('x')", "builtins.exec")]
    [InlineData("__builtins__.eval('doc.Undo()')", "__builtins__.eval")]
    [InlineData("name = 'Undo'\ngetattr(doc, name)()", "getattr(doc, <expression>)")]
    [InlineData("getattr(doc, 'Und' + 'o')()", "getattr(doc, <expression>)")]
    [InlineData("getattr(doc, 'Und' 'o')()", "getattr(doc, <expression>)")]
    [InlineData("getattr(objs[0], nm)", "getattr(<expression>, <expression>)")]
    public void DynamicCode_IsDenied(string script, string member)
    {
        var ex = Denied(script);
        Assert.Equal(ScriptApiDenylistViolationException.DeniedCode, ex.Code);
        Assert.Equal(member, ex.DeniedMember);
        Assert.Contains("cannot analyse", ex.Message);
    }

    // ---- confirmation-gated: lifecycle ----------------------------------------------------------

    [Theory]
    [InlineData("doc.Save()", "Rhino.RhinoDoc.Save")]
    [InlineData("doc.SaveAs('/tmp/x.3dm')", "Rhino.RhinoDoc.SaveAs")]
    [InlineData("doc.Export('/tmp/x.obj')", "Rhino.RhinoDoc.Export")]
    [InlineData("import Rhino\nRhino.RhinoDoc.Open('/tmp/x.3dm')", "Rhino.RhinoDoc.Open")]
    [InlineData("import Rhino\nd = Rhino.RhinoDoc.Create(None)", "Rhino.RhinoDoc.Create")]
    [InlineData("import scriptcontext as sc\nsc.doc.Import('/tmp/x.dwg')", "Rhino.RhinoDoc.Import")]
    [InlineData("import rhinoscriptsyntax as rs\nrs.Command('_-Save')", "rhinoscriptsyntax.Command(\"save\")")]
    [InlineData("import rhinoscriptsyntax as rs\nrs.Command('-_Export \"/tmp/x.obj\" _Enter')", "rhinoscriptsyntax.Command(\"export\")")]
    [InlineData("import Rhino\nRhino.RhinoApp.RunScript('_Close', False)", "Rhino.RhinoApp.RunScript(\"close\")")]
    [InlineData("import Rhino\nRhino.RhinoApp.ExecuteCommand(doc, '_New')", "Rhino.RhinoApp.ExecuteCommand(\"new\")")]
    public void Lifecycle_RequiresConfirmation(string script, string member)
    {
        var analysis = PythonScriptGuard.Analyze(script);
        Assert.True(analysis.RequiresLifecycleConfirmation);
        Assert.Equal(new[] { member }, analysis.LifecycleMembers);
    }

    [Fact]
    public void Lifecycle_ListsEachMemberOnce_InOrder()
    {
        var analysis = PythonScriptGuard.Analyze("doc.Save()\ndoc.Save()\ndoc.Export('a')\nimport rhinoscriptsyntax as rs\nrs.Command('_Save')\nrs.Command('_Save')");
        Assert.Equal(new[] { "Rhino.RhinoDoc.Save", "Rhino.RhinoDoc.Export", "rhinoscriptsyntax.Command(\"save\")" }, analysis.LifecycleMembers);
    }

    [Fact]
    public void ADeniedMemberWins_OverALifecycleOne()
    {
        // The denied call comes second in the text; it still refuses outright.
        var ex = Denied("doc.Save()\ndoc.Undo()");
        Assert.Equal("Rhino.RhinoDoc.Undo", ex.DeniedMember);
    }

    // ---- allowed --------------------------------------------------------------------------------

    [Theory]
    [InlineData("import Rhino\ndoc.Objects.AddSphere(Rhino.Geometry.Sphere(Rhino.Geometry.Point3d.Origin, 5))\nresult = doc.Objects.Count")]
    [InlineData("import rhinoscriptsyntax as rs\nrs.AddCircle((0,0,0), 5)\nids = rs.ObjectsByLayer('Default')")]
    [InlineData("import rhinoscriptsyntax as rs\nv = rs.GetDocumentUserText('key')\nu = rs.GetUserText(None, 'k')")]
    [InlineData("# doc.Undo() in a comment\ns = 'doc.Undo()'\nt = \"\"\"rs.GetPoint()\"\"\"")]
    [InlineData("undo = 1\nUndo = 2\nGetPoint = 3\nexit = False")]
    [InlineData("def Undo(self):\n    pass\nclass GetPoint:\n    pass")]
    [InlineData("class Tool:\n    def Exit(self): pass\nt = Tool()\nt.Exit()")]
    [InlineData("import Rhino\nRhino.RhinoApp.WriteLine('hi')\nRhino.RhinoApp.RunScript('_Circle 0,0,0 5', False)")]
    [InlineData("import rhinoscriptsyntax as rs\nrs.Command('_Line 0,0,0 1,1,1', echo=False)")]
    [InlineData("import Rhino\nRhino.Input.Custom.GetPoint")]
    [InlineData("doc.Modified = False\nn = doc.Name\np = doc.Path")]
    [InlineData("import scriptcontext as sc\nsc.doc.Views.Redraw()")]
    [InlineData("x = getattr(doc, 'Objects')\nfor o in x: pass")]
    [InlineData("import Rhino.Geometry as rg\npt = rg.Point3d(1, 2, 3)")]
    [InlineData("from Rhino.Geometry import Sphere, Point3d\ns = Sphere(Point3d.Origin, 1)")]
    [InlineData("import System\nSystem.Console.WriteLine('x')")]
    [InlineData("while not cancel.IsRequested:\n    cancel.Check()\n    break")]
    [InlineData("import sys\nif not doc.Objects.Count:\n    sys.exit()\ntry:\n    pass\nexcept SystemExit:\n    pass\nexit()")]
    [InlineData("import rhinoscriptsyntax as rs\nv = getattr(rs.coercerhinoobject(id), 'Attributes')\nn = getattr(objs[0], 'Name')")]
    [InlineData("print(f'{doc.Name} has {doc.Objects.Count:>4} objects')")]
    [InlineData("s = f'{{doc.Undo()}}'")]
    public void OrdinaryScripts_AreAllowed(string script)
    {
        var analysis = PythonScriptGuard.Analyze(script);
        Assert.False(analysis.RequiresLifecycleConfirmation, string.Join(",", analysis.LifecycleMembers));
    }

    [Fact]
    public void AStringMentioningACommand_OutsideARunScriptCall_IsNotGated()
    {
        var analysis = PythonScriptGuard.Analyze("label = '_Save'\nprint(label)");
        Assert.False(analysis.RequiresLifecycleConfirmation);
    }

    [Fact]
    public void KeyedOnType_NotOnBareName_SoAnUnrelatedObjectsUndoIsAllowed()
    {
        // The documented limit of a text walk: only the known document idioms resolve to RhinoDoc.
        var analysis = PythonScriptGuard.Analyze("class Stack:\n    def Undo(self): pass\nStack().Undo()\nother.Undo()");
        Assert.False(analysis.RequiresLifecycleConfirmation);
    }

    [Fact]
    public void MultipleImportsOnOneLine_AndSemicolons_AreResolved()
    {
        Denied("import os, rhinoscriptsyntax as rs; rs.GetPoint()");
        Denied("import Rhino, os; os._exit(0)");
        Denied("from Rhino.Input import (RhinoGet,\n    Custom)\nRhinoGet.GetPoint('x', False)");
    }
}
