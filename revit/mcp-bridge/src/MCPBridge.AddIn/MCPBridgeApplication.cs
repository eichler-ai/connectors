using System;
using Autodesk.Revit.UI;
using MCPBridge.Core.Connection;
using MCPBridge.Core.Execution;

namespace MCPBridge.AddIn;

/// <summary>
/// The add-in's entry point (PRD §04: "the add-in stays intentionally thin"). Wires
/// OnStartup/OnShutdown and delegates everything else to MCPBridge.Core -- no
/// protocol, threading, or execution decision logic lives here.
/// </summary>
public sealed class MCPBridgeApplication : IExternalApplication
{
    /// <summary>Minted once per Revit process at OnStartup; stable for the process's lifetime (PRD §05).</summary>
    public static Guid InstanceId { get; private set; }

    private BridgeHost? _host;

    public Result OnStartup(UIControlledApplication application)
    {
        InstanceId = Guid.NewGuid();

        var ringBuffer = ExecutionRingBuffer.CreateDefault();
        var executionManager = ExecutionManager.CreateDefault(ringBuffer);

        _host = new BridgeHost(InstanceId, executionManager, ReconnectBackoffPolicy.Default);
        _host.Start();

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _host?.Stop();
        _host = null;
        return Result.Succeeded;
    }
}
