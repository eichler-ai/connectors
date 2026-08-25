namespace MCPBridge.Core.Execution;

public enum CancellationRequestOutcome
{
    Acknowledged,

    /// <summary>
    /// Second live-wiring review finding: distinct from <see cref="Acknowledged"/> so a caller can detect
    /// "this cancellation resolved a still-Pending execution directly to Cancelled" without having to
    /// re-Poll and re-infer it from the record's resulting status afterward -- which is race-prone, since
    /// a completely different execution could reach the same terminal status in between (see
    /// ExternalEventBridge{TResult}.Abandon()'s callers, which need to know this specifically, not just
    /// "some execution somewhere is now Cancelled").
    /// </summary>
    AcknowledgedWasPending,

    NotFound,
    AlreadyTerminal,
}
