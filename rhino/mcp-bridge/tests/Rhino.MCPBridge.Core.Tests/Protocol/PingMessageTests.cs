using Rhino.MCPBridge.Core.Protocol;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Protocol;

public class PingMessageTests
{
    [Fact]
    public void ToJson_IsANotification_WithMethodPingAndNoId()
    {
        // PRD §05 heartbeat: must be a notification (no "id"), not a request -- the Go
        // broker's IsNotification() classifies wire messages on exactly that distinction
        // (transport/rpc.go), and a stray "id" here would misroute it as a request the
        // broker expects a response to.
        var json = PingMessage.ToJson();

        Assert.Contains("\"jsonrpc\":\"2.0\"", json);
        Assert.Contains("\"method\":\"ping\"", json);
        Assert.DoesNotContain("\"id\"", json);
    }

    [Fact]
    public void ToJson_WithMemoryAndState_CarriesExecutionState()
    {
        var json = PingMessage.ToJson(new MemorySnapshot { PrivateMB = 1, WorkingSetMB = 2, ManagedMB = 3 }, "busy");
        Assert.Contains("\"execution_state\":\"busy\"", json);
        Assert.DoesNotContain("execution_state", PingMessage.ToJson(new MemorySnapshot { PrivateMB = 1, WorkingSetMB = 2, ManagedMB = 3 }));
    }

    [Fact]
    public void ToJson_HasNoParams()
    {
        var json = PingMessage.ToJson();

        Assert.DoesNotContain("\"params\"", json);
    }

    [Fact]
    public void ToJson_IsSingleLine_SafeForNdjsonFraming()
    {
        var json = PingMessage.ToJson();

        Assert.DoesNotContain("\n", json);
    }

    [Fact]
    public void ToJson_WithMemory_CarriesTheSampleUnderParamsMemory()
    {
        // Issue #31: the heartbeat optionally carries a memory sample. It must still be a notification
        // (no id), and the sample must land under params.memory with the snake_case wire field names the
        // Go broker's registry.MemorySample unmarshals.
        var json = PingMessage.ToJson(new MemorySnapshot { PrivateMB = 4096, WorkingSetMB = 1200, ManagedMB = 512 });

        Assert.Contains("\"method\":\"ping\"", json);
        Assert.DoesNotContain("\"id\"", json);
        Assert.Contains("\"params\"", json);
        Assert.Contains("\"memory\"", json);
        Assert.Contains("\"private_mb\":4096", json);
        Assert.Contains("\"working_set_mb\":1200", json);
        Assert.Contains("\"managed_mb\":512", json);
        Assert.DoesNotContain("\n", json);
    }

    [Fact]
    public void ToJson_Bare_StillHasNoParams_SoTheMemoryOverloadIsOptIn()
    {
        // The bare heartbeat is unchanged: params is omitted entirely (WhenWritingNull), not emitted as
        // null -- so an older/quiet path and the broker's bare-ping handling see exactly what they did.
        Assert.DoesNotContain("\"params\"", PingMessage.ToJson());
    }

    [Fact]
    public void MemorySnapshot_Capture_ReturnsNonNegativeReadingsForThisProcess()
    {
        var m = MemorySnapshot.Capture();

        // Process.PrivateMemorySize64 is 0 on macOS (.NET reports it only on Windows and Linux), so the
        // committed reading is asserted where the platform provides it and the working set everywhere.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.True(m.PrivateMB > 0, "a live process has committed memory");
        }
        Assert.True(m.WorkingSetMB > 0, "a live process has a working set");
        Assert.True(m.ManagedMB > 0, "the CLR heap is non-empty in a running test host");
    }
}
