using System.Collections.Generic;
using System.Text.Json;
using MCPBridge.Core.Diagnostics;
using Xunit;

namespace MCPBridge.Core.Tests.Diagnostics;

public class DiagnosticRecordTests
{
    [Theory]
    [InlineData(DiagnosticSeverity.Debug, "debug")]
    [InlineData(DiagnosticSeverity.Info, "info")]
    [InlineData(DiagnosticSeverity.Warning, "warning")]
    [InlineData(DiagnosticSeverity.Error, "error")]
    public void Severity_Serialize_ProducesExactLowercaseGoWireValue(DiagnosticSeverity severity, string expectedWireValue)
    {
        var json = JsonSerializer.Serialize(severity);

        Assert.Equal($"\"{expectedWireValue}\"", json);
    }

    [Fact]
    public void SerializeThenDeserialize_RoundTrips()
    {
        // Fix 5: DiagnosticRecord previously had only a private constructor -- serializable but
        // not deserializable, so a Server-authored (or any externally-produced) record could
        // never be read back off the wire. The [JsonConstructor]-annotated public constructor
        // makes that possible while Create(...) remains the normal construction path.
        var original = DiagnosticRecord.Create(
            DiagnosticSeverity.Warning,
            "execution-transition-raced-terminal",
            DiagnosticSource.Execution,
            "execution abc-123 was already terminal",
            detail: new Dictionary<string, object?> { ["execution_id"] = "abc-123" },
            remedy: new[] { "call poll_execution again" });

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<DiagnosticRecord>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Severity, roundTripped!.Severity);
        Assert.Equal(original.Code, roundTripped.Code);
        Assert.Equal(original.Source, roundTripped.Source);
        Assert.Equal(original.Message, roundTripped.Message);
        Assert.Equal(original.Remedy, roundTripped.Remedy);
        Assert.True(roundTripped.Detail.ContainsKey("execution_id"));
    }

    [Fact]
    public void Source_ForExecutionArea_MatchesModuleNamingConvention()
    {
        var record = DiagnosticRecord.Create(
            DiagnosticSeverity.Error,
            "execution-failed",
            DiagnosticSource.Execution,
            "message",
            detail: null,
            remedy: null);

        Assert.Equal("mcp-bridge.core.execution", record.Source);
    }

    [Theory]
    [InlineData(DiagnosticSource.Execution, "mcp-bridge.core.execution")]
    [InlineData(DiagnosticSource.Connection, "mcp-bridge.core.connection")]
    [InlineData(DiagnosticSource.Discovery, "mcp-bridge.core.discovery")]
    [InlineData(DiagnosticSource.Dialogs, "mcp-bridge.core.dialogs")]
    public void Source_MapsEachArea(DiagnosticSource area, string expected)
    {
        var record = DiagnosticRecord.Create(DiagnosticSeverity.Info, "code", area, "msg", null, null);
        Assert.Equal(expected, record.Source);
    }

    [Fact]
    public void Create_RequiresNonEmptyMessage()
    {
        Assert.Throws<System.ArgumentException>(() =>
            DiagnosticRecord.Create(DiagnosticSeverity.Error, "code", DiagnosticSource.Execution, "", null, null));
    }

    [Fact]
    public void Create_RequiresNonEmptyCode()
    {
        Assert.Throws<System.ArgumentException>(() =>
            DiagnosticRecord.Create(DiagnosticSeverity.Error, "", DiagnosticSource.Execution, "msg", null, null));
    }

    [Fact]
    public void Detail_DefaultsToEmpty_NotNull()
    {
        var record = DiagnosticRecord.Create(DiagnosticSeverity.Warning, "code", DiagnosticSource.Execution, "msg", null, null);
        Assert.NotNull(record.Detail);
        Assert.Empty(record.Detail);
    }

    [Fact]
    public void Remedy_DefaultsToEmpty_NotNull()
    {
        var record = DiagnosticRecord.Create(DiagnosticSeverity.Warning, "code", DiagnosticSource.Execution, "msg", null, null);
        Assert.NotNull(record.Remedy);
        Assert.Empty(record.Remedy);
    }

    [Fact]
    public void Detail_And_Remedy_ArePreserved()
    {
        var detail = new Dictionary<string, object?> { ["execution_id"] = "abc-123" };
        var remedy = new[] { "call poll_execution again" };

        var record = DiagnosticRecord.Create(
            DiagnosticSeverity.Warning, "code", DiagnosticSource.Execution, "msg", detail, remedy);

        Assert.Equal("abc-123", record.Detail["execution_id"]);
        Assert.Single(record.Remedy, "call poll_execution again");
    }

    [Fact]
    public void ToString_IsHumanReadableAndIncludesMessage()
    {
        var record = DiagnosticRecord.Create(
            DiagnosticSeverity.Error, "exec-timeout", DiagnosticSource.Execution,
            "execution 1234 did not complete", null, null);

        Assert.Contains("execution 1234 did not complete", record.ToString());
    }
}
