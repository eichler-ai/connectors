using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.Core.Tests.Fakes;

/// <summary>
/// Script globals for tier 1. The document is null: RhinoDoc cannot be constructed outside Rhino
/// (its members P/Invoke into the native core), and every tier-1 script is one that never touches
/// it -- the compile-time checks (denylist, globals binding, return formatting) need only the TYPE,
/// which RhinoCommon.dll provides as plain managed metadata. Anything that dereferences Document is
/// tier 2 by construction.
/// </summary>
internal static class TestGlobals
{
    public static ScriptGlobals Create(CancellationToken token = default, string? label = null) =>
        new(document: null!, cancellationToken: token, bridgeVersion: "test", runLabel: label);
}
