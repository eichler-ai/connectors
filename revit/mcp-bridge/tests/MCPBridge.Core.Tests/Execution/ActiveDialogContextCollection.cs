using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>
/// Serialises every test class that reads or writes the <c>ActiveDialogContext</c> process-wide
/// static: the executor sets it for a run and clears it in its finally, and xUnit runs test CLASSES in
/// parallel by default, so a dispatcher test's ClearActive could land between another class's SetActive
/// and its observation. That was #151 -- DialogResultOverrides_WrittenByAScript failing only under a
/// full-suite run and never in isolation. The static is correct in production, where ExecutionManager
/// guarantees one run at a time; this collection gives the tests the same guarantee.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ActiveDialogContextCollection
{
    public const string Name = "ActiveDialogContext static";
}
