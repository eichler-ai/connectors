using Microsoft.CodeAnalysis.Scripting;

namespace MCPBridge.Core.Execution;

/// <summary>
/// One compiled script plus everything derived from its text that a later run would otherwise have to
/// recompute -- currently just its <see cref="ScriptApiAnalysis"/> (PRD §14). Both halves are cached
/// together, keyed by verbatim script text, because both depend on exactly that and nothing else; the
/// per-request confirmation flag deliberately lives outside this type (see ScriptApiAnalysis).
/// </summary>
internal sealed class CompiledScript
{
    public CompiledScript(Script<object> script, ScriptApiAnalysis analysis)
    {
        Script = script;
        Analysis = analysis;
    }

    public Script<object> Script { get; }

    public ScriptApiAnalysis Analysis { get; }
}
