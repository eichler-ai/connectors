using System.Text.Json;
using MCPBridge.Core.Execution;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>
/// Cross-component fix: the wire vocabulary must match the Go broker's Status type
/// (execution.go) exactly -- lowercase, and "success" (not "completed"). See
/// ExecutionStatus.cs's JsonStringEnumMemberName attributes.
/// </summary>
public class ExecutionStatusTests
{
    [Theory]
    [InlineData(ExecutionStatus.Pending, "pending")]
    [InlineData(ExecutionStatus.Running, "running")]
    [InlineData(ExecutionStatus.Completed, "success")] // the non-obvious one: Completed -> "success"
    [InlineData(ExecutionStatus.Error, "error")]
    [InlineData(ExecutionStatus.Cancelled, "cancelled")]
    [InlineData(ExecutionStatus.Unrecoverable, "unrecoverable")]
    public void Serialize_ProducesExactLowercaseGoWireValue(ExecutionStatus status, string expectedWireValue)
    {
        var json = JsonSerializer.Serialize(status);

        Assert.Equal($"\"{expectedWireValue}\"", json);
    }

    [Theory]
    [InlineData("\"pending\"", ExecutionStatus.Pending)]
    [InlineData("\"running\"", ExecutionStatus.Running)]
    [InlineData("\"success\"", ExecutionStatus.Completed)]
    [InlineData("\"error\"", ExecutionStatus.Error)]
    [InlineData("\"cancelled\"", ExecutionStatus.Cancelled)]
    [InlineData("\"unrecoverable\"", ExecutionStatus.Unrecoverable)]
    public void Deserialize_GoLowercaseWireValue_ParsesToExpectedMember(string wireJson, ExecutionStatus expected)
    {
        var status = JsonSerializer.Deserialize<ExecutionStatus>(wireJson);

        Assert.Equal(expected, status);
    }
}
