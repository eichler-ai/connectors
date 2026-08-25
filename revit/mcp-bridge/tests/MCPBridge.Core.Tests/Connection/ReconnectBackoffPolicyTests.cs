using System;
using MCPBridge.Core.Connection;
using Xunit;

namespace MCPBridge.Core.Tests.Connection;

public class ReconnectBackoffPolicyTests
{
    [Fact]
    public void FirstAttempt_Delays_ByInitialDelay()
    {
        var policy = new ReconnectBackoffPolicy(initialDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(1), policy.DelayForAttempt(0));
    }

    [Fact]
    public void Delay_DoublesEachAttempt_UntilCap()
    {
        var policy = new ReconnectBackoffPolicy(initialDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(1), policy.DelayForAttempt(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.DelayForAttempt(3));
        Assert.Equal(TimeSpan.FromSeconds(16), policy.DelayForAttempt(4));
    }

    [Fact]
    public void Delay_NeverExceedsCap()
    {
        var policy = new ReconnectBackoffPolicy(initialDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), policy.DelayForAttempt(5));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.DelayForAttempt(100));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.DelayForAttempt(int.MaxValue));
    }

    [Fact]
    public void Retries_AreIndefinite_NoMaxAttemptCeiling()
    {
        var policy = new ReconnectBackoffPolicy(initialDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30));
        // No exception, no special "give up" sentinel -- just keeps returning the capped delay.
        var delay = policy.DelayForAttempt(1_000_000);
        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void NegativeAttempt_Throws()
    {
        var policy = new ReconnectBackoffPolicy(initialDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayForAttempt(-1));
    }

    [Fact]
    public void Controller_ResetsAttemptCount_OnSuccess()
    {
        var policy = new ReconnectBackoffPolicy(initialDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30));
        var controller = new ReconnectLoopController(policy);

        Assert.Equal(TimeSpan.FromSeconds(1), controller.OnConnectFailed());
        Assert.Equal(TimeSpan.FromSeconds(2), controller.OnConnectFailed());
        Assert.Equal(TimeSpan.FromSeconds(4), controller.OnConnectFailed());

        controller.OnConnectSucceeded();

        // Back to the first delay after a reconnect, whatever caused the drop.
        Assert.Equal(TimeSpan.FromSeconds(1), controller.OnConnectFailed());
    }

    [Fact]
    public void Controller_AttemptCount_StartsAtZero()
    {
        var policy = new ReconnectBackoffPolicy(initialDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30));
        var controller = new ReconnectLoopController(policy);

        Assert.Equal(0, controller.AttemptCount);
        controller.OnConnectFailed();
        Assert.Equal(1, controller.AttemptCount);
    }

    [Fact]
    public void InvalidConstructorArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReconnectBackoffPolicy(initialDelay: TimeSpan.Zero, maxDelay: TimeSpan.FromSeconds(30)));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReconnectBackoffPolicy(initialDelay: TimeSpan.FromSeconds(10), maxDelay: TimeSpan.FromSeconds(5)));
    }
}
