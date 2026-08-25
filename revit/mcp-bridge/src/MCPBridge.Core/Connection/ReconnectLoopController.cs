using System;
using System.Threading;

namespace MCPBridge.Core.Connection;

/// <summary>
/// Drives attempt-count bookkeeping for the reconnect loop (PRD §05). One instance
/// covers first connect, reconnect-after-crash, and "Revit open, broker never showed
/// up" identically -- there's exactly one path here, not three special cases.
/// </summary>
public sealed class ReconnectLoopController
{
    private readonly ReconnectBackoffPolicy _policy;
    private int _attemptCount;

    public ReconnectLoopController(ReconnectBackoffPolicy policy)
    {
        _policy = policy;
    }

    public int AttemptCount => _attemptCount;

    /// <summary>Call when a connect attempt fails; returns how long to wait before retrying.</summary>
    public TimeSpan OnConnectFailed()
    {
        var delay = _policy.DelayForAttempt(_attemptCount);
        Interlocked.Increment(ref _attemptCount);
        return delay;
    }

    /// <summary>Call on every successful connect -- resets backoff so a future drop starts over at the initial delay.</summary>
    public void OnConnectSucceeded()
    {
        Interlocked.Exchange(ref _attemptCount, 0);
    }
}
