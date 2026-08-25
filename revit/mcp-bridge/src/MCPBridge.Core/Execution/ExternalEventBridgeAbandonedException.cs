using System;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Thrown to fault a work item's Task when <see cref="ExternalEventBridge{TResult}.Abandon"/> discards it
/// while still queued -- e.g. a Pending execution that was cancelled before Revit's idle loop ever
/// entered Execute() for it (BridgeHost.cs's third-review hard requirement: a stale queued raise must not
/// be able to wedge the bridge for the life of the process). Distinct from
/// <see cref="ExternalEventRaiseDeniedException"/>: that one means Raise() itself was rejected by Revit.
/// This one means Raise() may well have succeeded (or is still genuinely Pending) but the work item is
/// being deliberately discarded by this side before Execute() ever ran it -- most commonly because the
/// execution it belonged to was already resolved to Cancelled while still Pending, so running the script
/// now would be observably wrong (see ExecutionManager.ApplyCancellation's own doc comment).
/// </summary>
public sealed class ExternalEventBridgeAbandonedException : Exception
{
    public const string Code = "external-event-bridge-abandoned";

    public ExternalEventBridgeAbandonedException()
        : base(
            $"the queued work item was abandoned (code: {Code}) before Revit's idle loop ever ran it -- " +
            "most likely because the execution it belonged to was cancelled while still Pending.")
    {
    }
}
