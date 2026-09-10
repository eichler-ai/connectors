using Rhino.MCPBridge.Core.Execution.Python;

namespace Rhino.MCPBridge.Core.Tests.Fakes;

/// <summary>A scripted Python host: records what it was asked to run and answers with whatever the
/// test configured. No interpreter in tier 1.</summary>
internal sealed class FakePythonHost : IPythonHost
{
    public string? UnavailableReason { get; set; }
    public int Loads;
    public List<(string Text, IReadOnlyDictionary<string, object?> Inputs, string ResultName)> Runs { get; } = new();
    public Func<string, IReadOnlyDictionary<string, object?>, PythonRunResult> OnRun { get; set; } = (_, _) => new PythonRunResult();

    public void EnsureLoaded() { Loads++; UnavailableReason = null; }

    public PythonRunResult Run(string text, IReadOnlyDictionary<string, object?> inputs, string resultName)
    {
        Runs.Add((text, inputs, resultName));
        return OnRun(text, inputs);
    }
}
