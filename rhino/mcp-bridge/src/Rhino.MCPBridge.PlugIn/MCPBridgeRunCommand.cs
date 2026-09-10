using Rhino.Commands;
using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.RhinoAdapter;

namespace Rhino.MCPBridge.PlugIn;

/// <summary>
/// The command every script runs inside (rhino/docs/PRD.md §06/§07, spikes §3): started by the run
/// host through RhinoApp.ExecuteCommand, it takes the parked body from <see cref="RunQueue"/> and
/// runs it. Being a command is what makes a run one undo entry and lets ExecuteCommand(_Undo)
/// afterwards revert exactly it. Hidden from autocomplete: a person typing it runs nothing.
/// </summary>
[CommandStyle(Style.Hidden | Style.ScriptRunner)]
public sealed class MCPBridgeRunCommand : Command
{
    public override string EnglishName => UndoRunExecutor.RunCommandName;

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return RunQueue.TakeAndRun() ? Result.Success : Result.Nothing;
    }
}
