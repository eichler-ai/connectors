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
        // PR #2 review, Fix 1's confirmed architecture decision (see MCPBridge.Core.Execution.
        // ExternalEventBridge<TResult> and RoslynScriptRunner's doc comments for the full
        // reasoning): when this wiring lands, it composes as
        //   var externalEvent = ExternalEvent.Create(revitScriptExecutionHandler);
        //   var raiser = new RevitExternalEventRaiser(externalEvent);
        //   var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(raiser);
        //   var handler = new RevitScriptExecutionHandler(bridge); // bridge implements IScriptExecutionCallback
        // and the TCP-handling thread calls bridge.RunAsync(app => {
        //   // fully synchronous work item: no await anywhere in here.
        //   executionManager.MarkRunning(executionId, now);
        //   var outcome = transactionScriptExecutor.ExecuteAsync(..., executionManager.GetCancellationToken(executionId))
        //       .GetAwaiter().GetResult(); // deadlock-safe only because RoslynScriptRunner rejects any
        //                                  // script containing its own top-level `await` before compiling it.
        //   executionManager.CompleteSuccess/.CompleteError/.CompleteCancelled(...);
        //   return outcome;
        // }), which returns a Task the TCP thread can await without blocking, and which already
        // surfaces a Denied/TimedOut Raise() as a failed Task (Fix 5) instead of hanging.
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
