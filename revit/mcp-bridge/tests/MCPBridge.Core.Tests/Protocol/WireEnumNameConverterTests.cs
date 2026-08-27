using System.Text.Json;
using System.Text.Json.Serialization;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Protocol;

/// <summary>
/// WireEnumNameConverter is the project's stand-in for .NET 9+'s
/// JsonStringEnumMemberNameAttribute, needed because the Bridge multi-targets
/// net8.0-windows alongside net10.0-windows (PRD §11). It decodes the shared §01
/// diagnostic-record shape, so its failure MODES matter as much as its happy path --
/// an independent review found it shipped with no tests at all, and with two latent
/// defects (wrong exception type for a non-string token; an opaque, process-lifetime
/// TypeInitializationException on a duplicated wire name) that only tests would pin.
///
/// The deliberate divergences from the framework converter it replaces -- case-SENSITIVE
/// reads, no integer values, no [Flags] -- are asserted here rather than merely documented,
/// so a later "helpful" relaxation fails loudly instead of quietly widening the wire
/// contract.
/// </summary>
public class WireEnumNameConverterTests
{
    // ---- round-tripping, across every enum that actually uses this converter ----

    [Theory]
    [InlineData(ExecutionStatus.Completed, "success")] // the non-obvious one
    [InlineData(ExecutionStatus.Pending, "pending")]
    [InlineData(ExecutionStatus.Unrecoverable, "unrecoverable")]
    public void ExecutionStatus_RoundTrips(ExecutionStatus value, string wire)
    {
        var json = JsonSerializer.Serialize(value);
        Assert.Equal($"\"{wire}\"", json);
        Assert.Equal(value, JsonSerializer.Deserialize<ExecutionStatus>(json));
    }

    [Theory]
    [InlineData(DiagnosticSeverity.Debug, "debug")]
    [InlineData(DiagnosticSeverity.Warning, "warning")]
    [InlineData(DiagnosticSeverity.Error, "error")]
    public void DiagnosticSeverity_RoundTrips(DiagnosticSeverity value, string wire)
    {
        var json = JsonSerializer.Serialize(value);
        Assert.Equal($"\"{wire}\"", json);
        Assert.Equal(value, JsonSerializer.Deserialize<DiagnosticSeverity>(json));
    }

    [Theory]
    [InlineData(AuthRole.AddIn, "add-in")]             // hyphenated: cannot be a C# identifier
    [InlineData(AuthRole.AgentClient, "agent-client")]
    public void AuthRole_RoundTrips(AuthRole value, string wire)
    {
        var json = JsonSerializer.Serialize(value);
        Assert.Equal($"\"{wire}\"", json);
        Assert.Equal(value, JsonSerializer.Deserialize<AuthRole>(json));
    }

    // ---- read-path failure modes: all must be JsonException, never anything else ----

    [Theory]
    [InlineData("2")]      // an ordinal -- JsonStringEnumConverter WOULD have accepted this
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Read_NonStringToken_ThrowsJsonException(string json)
    {
        // The regression this pins: reader.GetString() throws InvalidOperationException,
        // which escapes every `catch (JsonException)` a caller has written. DiagnosticRecord
        // promises a malformed payload deserializes rather than throwing mid-parse, and that
        // promise is only keepable if the failure is a JsonException.
        var ex = Record.Exception(() => JsonSerializer.Deserialize<ExecutionStatus>(json));

        Assert.NotNull(ex);
        Assert.IsAssignableFrom<JsonException>(ex);
    }

    [Theory]
    [InlineData("\"Success\"")]  // framework converter was case-INsensitive; this one is not
    [InlineData("\"SUCCESS\"")]
    [InlineData("\"Completed\"")] // the C# member name is not the wire name
    [InlineData("\"\"")]
    [InlineData("\"nonsense\"")]
    public void Read_UnknownOrDifferentlyCasedValue_ThrowsJsonException(string json)
    {
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ExecutionStatus>(json));

        Assert.Contains("ExecutionStatus", ex.Message);
    }

    // ---- write-path failure mode ----

    [Fact]
    public void Write_UndefinedValueCastIntoEnum_ThrowsRatherThanEmittingANumber()
    {
        // Previously emitted "7", which this same converter's Read then rejects -- a
        // silently asymmetric round trip.
        var undefined = (ExecutionStatus)7;

        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Serialize(undefined));

        Assert.Contains("ExecutionStatus", ex.Message);
    }

    // ---- fallback, and the two static-initialization guards ----

    private enum NoAttributes { Alpha, Beta }

    [Fact]
    public void MemberWithoutAttribute_FallsBackToItsDeclaredFieldName()
    {
        var options = new JsonSerializerOptions { Converters = { new WireEnumNameConverter<NoAttributes>() } };

        Assert.Equal("\"Beta\"", JsonSerializer.Serialize(NoAttributes.Beta, options));
        Assert.Equal(NoAttributes.Alpha, JsonSerializer.Deserialize<NoAttributes>("\"Alpha\"", options));
    }

    private enum DuplicateWireNames
    {
        [WireEnumName("same")] First,
        [WireEnumName("same")] Second,
    }

    [Fact]
    public void DuplicateWireName_FailsNamingBothOffendingMembers()
    {
        // The point of the fix: ToDictionary's bare "An item with the same key has already
        // been added" named neither member, and the CLR caches the type-init failure, so a
        // one-character typo became an unexplained protocol outage for the whole process.
        var options = new JsonSerializerOptions { Converters = { new WireEnumNameConverter<DuplicateWireNames>() } };

        var ex = Record.Exception(() => JsonSerializer.Serialize(DuplicateWireNames.First, options));

        Assert.NotNull(ex);
        var root = ex is TypeInitializationException { InnerException: { } inner } ? inner : ex;
        Assert.Contains("same", root.Message);
        Assert.Contains(nameof(DuplicateWireNames.First), root.Message);
        Assert.Contains(nameof(DuplicateWireNames.Second), root.Message);
    }

    [Flags]
    private enum FlagsShaped { None = 0, A = 1, B = 2 }

    [Fact]
    public void FlagsEnum_IsRejectedUpFrontRatherThanHalfWorking()
    {
        var options = new JsonSerializerOptions { Converters = { new WireEnumNameConverter<FlagsShaped>() } };

        var ex = Record.Exception(() => JsonSerializer.Serialize(FlagsShaped.A | FlagsShaped.B, options));

        Assert.NotNull(ex);
        var root = ex is TypeInitializationException { InnerException: { } inner } ? inner : ex;
        Assert.IsType<NotSupportedException>(root);
        Assert.Contains("Flags", root.Message);
    }
}
