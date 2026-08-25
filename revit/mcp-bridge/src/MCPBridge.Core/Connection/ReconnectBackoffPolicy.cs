using System;

namespace MCPBridge.Core.Connection;

/// <summary>
/// Pure backoff calculation for the add-in's dial-out/reconnect loop (PRD §05):
/// starts at <paramref name="initialDelay"/> (default 1s), doubles each attempt,
/// capped at <paramref name="maxDelay"/> (default within the ~15-30s range the
/// PRD specifies), and never "gives up" -- retries are indefinite by design, so
/// there is no maximum-attempt ceiling here, only a maximum delay.
/// </summary>
public sealed class ReconnectBackoffPolicy
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;

    public ReconnectBackoffPolicy(TimeSpan initialDelay, TimeSpan maxDelay)
    {
        if (initialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay), "initialDelay must be positive.");
        }

        if (maxDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "maxDelay must be >= initialDelay.");
        }

        _initialDelay = initialDelay;
        _maxDelay = maxDelay;
    }

    /// <summary>Default policy per PRD §05: 1s rising to a 30s cap.</summary>
    public static ReconnectBackoffPolicy Default { get; } =
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

    public TimeSpan DelayForAttempt(int attemptNumber)
    {
        if (attemptNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber), "attemptNumber cannot be negative.");
        }

        // delay = min(initialDelay * 2^attemptNumber, maxDelay), computed with a
        // shift bounded well below 63 so it can never overflow a long even for a
        // very large attemptNumber -- once the shift alone would exceed maxDelay,
        // there's no need to actually compute the (potentially enormous) power.
        var shift = Math.Min(attemptNumber, 62);
        var scaled = _initialDelay.Ticks <= _maxDelay.Ticks >> shift
            ? _initialDelay.Ticks << shift
            : long.MaxValue;

        return scaled >= _maxDelay.Ticks ? _maxDelay : TimeSpan.FromTicks(scaled);
    }
}
