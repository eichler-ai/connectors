using System;
using MCPBridge.Core.Connection;
using MCPBridge.Core.Execution;

namespace MCPBridge.AddIn;

/// <summary>
/// Owns the background connection thread lifecycle for one Revit process. This class
/// intentionally does no decision-making of its own -- it just starts/stops the
/// Core-driven reconnect loop against real infrastructure (real TCP sockets, real
/// broker.json paths). Not unit-tested for that reason (see the RevitAdapter
/// concrete classes' doc comments for the same rationale); the decision logic it
/// calls into (ReconnectBackoffPolicy, ExecutionManager, BrokerDiscovery) is fully
/// unit-tested in MCPBridge.Core.Tests.
/// </summary>
internal sealed class BridgeHost
{
    private readonly Guid _instanceId;
    private readonly ExecutionManager _executionManager;
    private readonly ReconnectBackoffPolicy _backoffPolicy;

    public BridgeHost(Guid instanceId, ExecutionManager executionManager, ReconnectBackoffPolicy backoffPolicy)
    {
        _instanceId = instanceId;
        _executionManager = executionManager;
        _backoffPolicy = backoffPolicy;
    }

    public void Start()
    {
        // TODO(phase 01 live wiring): start the background TCP connection thread here,
        // using BrokerDiscovery + ReconnectLoopController + a real TcpClient transport,
        // and register the RevitScriptExecutionHandler/ExternalEvent pair. Deferred to
        // the live-harness wiring step since it needs a live Revit session to exercise
        // (see tests/MCPBridge.Integration.Tests).
        //
        // Also call RoslynAssemblyIsolation.EnsureInitialized() here, before any script
        // ever compiles. It's a partial mitigation, not full isolation (see its own doc
        // comment for why -- true isolation needs a shadow-load bootstrap), and nothing
        // currently calls it anywhere in the codebase. Flagged deliberately (adversarial
        // code review, Phase 1) so that caveat doesn't quietly become "solved" the moment
        // this stub is filled in.
    }

    public void Stop()
    {
    }
}
