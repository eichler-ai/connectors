using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// One script language the bridge can run (PRD §06: C# and Python are equal peers, selected by the
/// required <c>language</c> parameter). The executor and dispatcher see only this: pre-flight the
/// text on the connection thread, run it on the main thread inside the run command.
/// </summary>
internal interface IScriptRunner
{
    /// <summary>The wire name of the language: "csharp" or "python".</summary>
    string Language { get; }

    /// <summary>True once the runner's one-time warm-up has finished, which is when pre-flight on the
    /// connection thread becomes cheap enough to do there.</summary>
    bool IsWarm { get; }

    /// <summary>Null when scripts can run; otherwise why they cannot yet (a language still loading, a
    /// host that failed to initialise). Reported verbatim under <c>language-not-available</c>.</summary>
    string? UnavailableReason { get; }

    /// <summary>The text-only rejection (compile error, denied member, unconfirmed lifecycle call), or
    /// null when the script may run. Identical to the check RunAsync performs first.</summary>
    ScriptExecutionOutcome? TryPreflight(string scriptText, bool confirmLifecycleActions);

    Task<ScriptExecutionOutcome> RunAsync(string scriptText, ScriptGlobals globals, CancellationToken cancellationToken, bool confirmLifecycleActions);
}

/// <summary>The runners keyed by language name.</summary>
internal sealed class ScriptRunners
{
    private readonly Dictionary<string, IScriptRunner> _byLanguage;

    public ScriptRunners(params IScriptRunner[] runners)
    {
        _byLanguage = runners.ToDictionary(r => r.Language);
    }

    public IReadOnlyList<string> Languages => _byLanguage.Keys.OrderBy(k => k).ToList();

    public IScriptRunner? Get(string language) => _byLanguage.TryGetValue(language, out var r) ? r : null;
}
