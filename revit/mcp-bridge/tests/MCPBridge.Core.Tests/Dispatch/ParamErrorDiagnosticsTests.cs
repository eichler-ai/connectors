using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Discovery;
using MCPBridge.Core.Dispatch;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Dispatch;

/// <summary>
/// Issue #69: every bad-parameter error the add-in returns must carry a full PRD §01 diagnostic record,
/// not just a message. Before this, JsonRpcParamException reached an agent as a bare InvalidParams string
/// with no `code` to branch on and no `remedy`, while the Go broker's equivalent validation carried both
/// -- the same logical failure in two shapes depending on which side caught it first.
///
/// <para>These tests deliberately assert on the SERIALIZED RESPONSE, driven through
/// <see cref="RequestDispatcher.DispatchAsync"/>, rather than on the exception object. That is the whole
/// point: the defect was never in the exception, it was in six catch sites each passing `null` where the
/// record belongs. A test that constructed a JsonRpcParamException and inspected its Diagnostic property
/// would stay green with every one of those `null`s restored -- i.e. with the bug fully reinstated.</para>
/// </summary>
public class ParamErrorDiagnosticsTests
{
    /// <summary>A tiny public fixture type, so DiscoveryService has something real to reflect over with no Revit dependency.</summary>
    public class Sample
    {
        public void DoThing()
        {
        }
    }

    private const string SampleNamespace = "MCPBridge.Core.Tests.Dispatch";

    private static ExecutionManager NewExecutionManager() =>
        new(new ExecutionRingBuffer(capacity: 50, retention: TimeSpan.FromMinutes(10)), gracePeriod: TimeSpan.FromSeconds(5));

    private static DiscoveryService NewDiscoveryService()
    {
        var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", typeof(Sample).Assembly) });
        return new DiscoveryService(cache);
    }

    private static RequestDispatcher NewDispatcher(ExecutionManager? executionManager = null) => new(
        executionManager ?? NewExecutionManager(),
        new ExternalEventBridge<ScriptExecutionOutcome>(new MCPBridge.Core.Tests.Fakes.FakeExternalEventRaiser()),
        new TransactionScriptExecutor(new RoslynScriptRunner(additionalMetadataReferences: RevitApiReference.References)),
        discoveryService: NewDiscoveryService());

    private static JsonRpcRequest Parse(object envelope) => JsonRpcRequest.Parse(JsonSerializer.Serialize(envelope));

    /// <summary>Dispatches one request and returns its `error.data` element, failing loudly if the response is not an error or carries no record at all.</summary>
    private static async Task<JsonElement> ErrorDataAsync(object envelope, ExecutionManager? executionManager = null)
    {
        var json = await NewDispatcher(executionManager).DispatchAsync(Parse(envelope));
        using var doc = JsonDocument.Parse(json);
        Assert.True(
            doc.RootElement.TryGetProperty("error", out var error),
            $"expected a JSON-RPC error response, got: {json}");
        Assert.True(
            error.TryGetProperty("data", out var data),
            $"error carries no `data` diagnostic record (issue #69 -- this is exactly the regression): {json}");
        return data.Clone();
    }

    private static string Str(JsonElement data, string name) => data.GetProperty(name).GetString() ?? "";

    // Each row is one distinct param-error path that can reach the wire. Kept as one table rather than a
    // test per case so that adding a new validation path has an obvious place to land -- and so the
    // "every record is complete" invariant below is asserted uniformly over all of them rather than
    // re-typed (and eventually forgotten) per case.
    public static TheoryData<string, object, string, string> ParamErrorCases() => new()
    {
        // -- protocol layer: JsonRpcRequest's own accessors --------------------------------------------
        {
            "execute_script with no script param",
            new { jsonrpc = "2.0", id = 1, method = "execute_script", @params = new { execution_id = "e1" } },
            "missing-required-param", "mcp-bridge.core.protocol"
        },
        {
            "execute_script with no execution_id param",
            new { jsonrpc = "2.0", id = 1, method = "execute_script", @params = new { script = "1;" } },
            "missing-required-param", "mcp-bridge.core.protocol"
        },
        {
            "poll_execution with no execution_id param",
            new { jsonrpc = "2.0", id = 1, method = "poll_execution", @params = new { } },
            "missing-required-param", "mcp-bridge.core.protocol"
        },
        {
            "cancel_execution with no execution_id param",
            new { jsonrpc = "2.0", id = 1, method = "cancel_execution", @params = new { } },
            "missing-required-param", "mcp-bridge.core.protocol"
        },
        {
            "search_functions with no query param",
            new { jsonrpc = "2.0", id = 1, method = "search_functions", @params = new { } },
            "missing-required-param", "mcp-bridge.core.protocol"
        },
        {
            "execute_script with a non-numeric timeout_ms",
            new { jsonrpc = "2.0", id = 1, method = "execute_script", @params = new { execution_id = "e1", script = "1;", timeout_ms = "soon" } },
            "invalid-param-type", "mcp-bridge.core.protocol"
        },
        {
            "execute_script with a non-boolean overwrite_output_files",
            new { jsonrpc = "2.0", id = 1, method = "execute_script", @params = new { execution_id = "e1", script = "1;", overwrite_output_files = "yes" } },
            "invalid-param-type", "mcp-bridge.core.protocol"
        },
        {
            "list_functions with a non-string namespace",
            new { jsonrpc = "2.0", id = 1, method = "list_functions", @params = new { @namespace = 7 } },
            "invalid-param-type", "mcp-bridge.core.protocol"
        },
        {
            // A required param that is PRESENT but the wrong JSON type is a different code from one that
            // is absent, even though both produce the same sentence. That split is the reason `code`
            // exists: "you forgot this" and "fix the value you already sent" have different next steps.
            "execute_script with a non-string script",
            new { jsonrpc = "2.0", id = 1, method = "execute_script", @params = new { execution_id = "e1", script = 42 } },
            "invalid-param-type", "mcp-bridge.core.protocol"
        },

        // -- discovery layer: DiscoveryService's own param rules ---------------------------------------
        {
            "list_functions given type_name without namespace",
            new { jsonrpc = "2.0", id = 1, method = "list_functions", @params = new { type_name = "Sample" } },
            "missing-required-param", "mcp-bridge.core.discovery"
        },
        {
            "describe_function given neither member nor member_id",
            new { jsonrpc = "2.0", id = 1, method = "describe_function", @params = new { } },
            "missing-required-param", "mcp-bridge.core.discovery"
        },
        {
            "list_functions given a structurally unparseable cursor",
            new { jsonrpc = "2.0", id = 1, method = "list_functions", @params = new { cursor = "not-a-cursor" } },
            "invalid-cursor", "mcp-bridge.core.discovery"
        },
        {
            // Parses as an offset, but its scope hash belongs to some other query -- the branch that
            // stops a caller paging one listing with another listing's cursor.
            "list_functions given a cursor issued for a different query",
            new { jsonrpc = "2.0", id = 1, method = "list_functions", @params = new { cursor = "0:deadbeef" } },
            "invalid-cursor", "mcp-bridge.core.discovery"
        },
    };

    [Theory]
    [MemberData(nameof(ParamErrorCases))]
    public async Task EveryParamErrorCarriesACompleteDiagnosticRecord(string description, object envelope, string expectedCode, string expectedSource)
    {
        var data = await ErrorDataAsync(envelope);

        Assert.Equal(expectedCode, Str(data, "code"));
        Assert.Equal(expectedSource, Str(data, "source"));
        Assert.Equal("error", Str(data, "severity"));
        Assert.False(string.IsNullOrWhiteSpace(Str(data, "message")), $"{description}: message must be specific (PRD §01)");

        // The remedy is the half PRD §01 calls for "wherever there's a next step", and a param error
        // ALWAYS has one -- there is no bad-parameter failure a caller cannot act on. Asserted as
        // non-empty strings, not merely a present array, because an empty array serializes fine and
        // reads as satisfied.
        var remedies = data.GetProperty("remedy").EnumerateArray().Select(r => r.GetString() ?? "").ToList();
        Assert.NotEmpty(remedies);
        Assert.All(remedies, r => Assert.False(string.IsNullOrWhiteSpace(r), $"{description}: empty remedy string"));
    }

    [Theory]
    [MemberData(nameof(ParamErrorCases))]
    public async Task EveryParamErrorNamesTheOffendingParameterInItsDetail(string description, object envelope, string expectedCode, string expectedSource)
    {
        _ = expectedCode;
        _ = expectedSource;
        var data = await ErrorDataAsync(envelope);
        var detail = data.GetProperty("detail");

        // §01's detail is where the machine-readable specifics go. For a param error the specific that
        // matters is WHICH param -- singular ("param") for the common case, plural ("params") for the
        // one rule that is satisfied by either of two. Without this an agent gets a code it can branch
        // on but still has to parse the prose to learn what to change.
        var namesAParam = detail.TryGetProperty("param", out _) || detail.TryGetProperty("params", out _);
        Assert.True(namesAParam, $"{description}: detail names no parameter -- got {detail}");
    }

    /// <summary>
    /// The specific cross-wire mismatch issue #69 was filed over: the Go broker rejects an empty
    /// member/member_id pair with `missing-required-param` plus a detail listing both params, and the C#
    /// side must answer the identical condition the identical way. Pinned as its own test because it is
    /// the one case where a divergence is observable by simply routing the request differently -- the
    /// broker's guard normally answers first, which is why this stayed invisible for so long.
    /// </summary>
    [Fact]
    public async Task DescribeFunctionMissingBothParamsMatchesTheBrokerSideShape()
    {
        var data = await ErrorDataAsync(
            new { jsonrpc = "2.0", id = 1, method = "describe_function", @params = new { } });

        Assert.Equal("missing-required-param", Str(data, "code"));
        var named = data.GetProperty("detail").GetProperty("params").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(new[] { "member", "member_id" }, named);
    }

    /// <summary>
    /// The seventh catch site in the same handler: ExecutionManager.Start's ArgumentException for a
    /// colliding execution_id. Not a JsonRpcParamException (it is an ordinary .NET argument guard from a
    /// module with no protocol concerns), but it produced the same bare InvalidParams, so it is fixed and
    /// pinned alongside the rest rather than left as the one remaining shapeless param error.
    /// </summary>
    [Fact]
    public async Task ACollidingExecutionIdAlsoCarriesADiagnosticRecord()
    {
        // The id must belong to an already-TERMINAL execution: re-sending the id of the still-active one
        // is deliberately answered with Busy-pointing-at-itself (ExecutionManager.Start's own doc comment
        // -- an idempotent answer to a broker retry), and only a collision against a different, finished
        // execution reaches the ArgumentException path this test is about.
        var manager = NewExecutionManager();
        manager.Start("taken", "1;", maxDurationMs: 1000, DateTimeOffset.UtcNow);
        manager.CompleteSuccess("taken", DateTimeOffset.UtcNow, result: "1", stdOut: null, notices: Array.Empty<DiagnosticRecord>());

        var data = await ErrorDataAsync(
            new { jsonrpc = "2.0", id = 1, method = "execute_script", @params = new { execution_id = "taken", script = "1;" } },
            manager);

        Assert.Equal("invalid-execution-id", Str(data, "code"));
        Assert.Equal("mcp-bridge.core.execution", Str(data, "source"));
        Assert.Equal("taken", Str(data.GetProperty("detail"), "execution_id"));
        Assert.NotEmpty(data.GetProperty("remedy").EnumerateArray());
    }

    /// <summary>
    /// JsonRpcRequest.Parse's two throws are asserted on the exception directly, and ONLY these two --
    /// they are genuinely unreachable through DispatchAsync, because a message that fails Parse never
    /// becomes a request to dispatch. BridgeHost's read loop skips such a line entirely (it has no usable
    /// `id` to echo a response against), so these records exist for the log/diagnostic path, not the
    /// wire. Stated here so a future reader does not mistake the missing dispatch-level coverage for an
    /// oversight and "fix" it by inventing a response that cannot be addressed.
    /// </summary>
    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1}", "method")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"poll_execution\"}", "id")]
    public void MalformedEnvelopesCarryARecordEvenThoughItNeverReachesTheWire(string line, string expectedField)
    {
        var ex = Assert.Throws<JsonRpcParamException>(() => JsonRpcRequest.Parse(line));

        Assert.Equal("malformed-request", ex.Diagnostic.Code);
        Assert.Equal("mcp-bridge.core.protocol", ex.Diagnostic.Source);
        Assert.Equal(expectedField, ex.Diagnostic.Detail["field"]);
        Assert.NotEmpty(ex.Diagnostic.Remedy);
    }

    /// <summary>
    /// The exception cannot be constructed without a record, which is what makes the fix structural
    /// rather than a sweep of eleven throw sites that a twelfth can silently miss. Asserted through the
    /// default-code path specifically, since that is the one overload shape a new throw site is most
    /// likely to reach for.
    /// </summary>
    [Fact]
    public void AParamExceptionAlwaysHasARecordEvenWithNothingButAMessage()
    {
        var ex = new JsonRpcParamException("params.thing is wrong.", DiagnosticSource.Discovery);

        Assert.Equal(JsonRpcParamException.DefaultCode, ex.Diagnostic.Code);
        Assert.Equal("mcp-bridge.core.discovery", ex.Diagnostic.Source);
        Assert.Equal("params.thing is wrong.", ex.Diagnostic.Message);
        Assert.Equal(DiagnosticSeverity.Error, ex.Diagnostic.Severity);
    }

    /// <summary>
    /// Every DiagnosticSource must map to a source tag, including the Protocol value this change added.
    /// ToSourceTag's `_ =>` arm throws, so a value added to the enum without a tag is a runtime failure
    /// at the moment a diagnostic is built -- i.e. inside a catch block, on an error path, which is the
    /// worst possible place to discover it.
    /// </summary>
    [Fact]
    public void EveryDiagnosticSourceHasADistinctModuleTag()
    {
        var tags = Enum.GetValues<DiagnosticSource>().Select(s => s.ToSourceTag()).ToList();

        Assert.All(tags, tag => Assert.StartsWith("mcp-bridge.core.", tag));
        Assert.Equal(tags.Count, tags.Distinct(StringComparer.Ordinal).Count());
    }
}
