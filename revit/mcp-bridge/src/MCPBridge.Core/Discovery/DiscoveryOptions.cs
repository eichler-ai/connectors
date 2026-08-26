using System;
using System.Collections.Generic;
using System.Reflection;

namespace MCPBridge.Core.Discovery;

/// <summary>
/// Configures <see cref="DiscoveryService"/> with the plain BCL <see cref="Assembly"/> objects it should
/// reflect over. MCPBridge.Core has no compile-time reference to RevitAPI/RevitAPIUI (only
/// MCPBridge.AddIn/MCPBridge.RevitAdapter do) -- the caller (BridgeHost) is expected to pass the exact
/// assemblies Revit has already loaded into this process (e.g. typeof(Autodesk.Revit.DB.Document).Assembly,
/// typeof(Autodesk.Revit.UI.UIApplication).Assembly), never load them a second time from a guessed path.
/// This guarantees reflection always matches the exact assembly/version actually running (PRD §08).
/// </summary>
public sealed class DiscoveryOptions
{
    public IReadOnlyList<Assembly> Assemblies { get; init; } = Array.Empty<Assembly>();
}
