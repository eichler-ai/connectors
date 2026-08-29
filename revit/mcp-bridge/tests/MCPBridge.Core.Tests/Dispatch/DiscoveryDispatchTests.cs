using System;
using System.Text.Json;
using System.Threading.Tasks;
using MCPBridge.Core.Discovery;
using MCPBridge.Core.Dispatch;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Dispatch;

/// <summary>
/// Exercises RequestDispatcher's list_functions/search_functions/describe_function routing (PRD §08) --
/// specifically that these three methods are served synchronously, directly from DiscoveryService, with
/// zero ExecutionManager/ExternalEventBridge involvement (unlike execute_script/poll_execution/
/// cancel_execution, covered in RequestDispatcherTests). The deep reflection/cache/ranking behavior itself is
/// covered in MCPBridge.Discovery.Tests and MCPBridge.Core.Tests' own DiscoveryCacheTests -- these tests only
/// verify the dispatcher wires params through and serializes the response/error correctly.
/// </summary>
public class DiscoveryDispatchTests
{
    /// <summary>A tiny public fixture type, purely so DiscoveryService has something real to reflect over without any Revit/XML dependency.</summary>
    public class Sample
    {
        public void DoThing()
        {
        }
    }

    private static ExecutionManager NewExecutionManager() =>
        new(new ExecutionRingBuffer(capacity: 50, retention: TimeSpan.FromMinutes(10)), gracePeriod: TimeSpan.FromSeconds(5));

    private static TransactionScriptExecutor NewScriptExecutor() => new(new RoslynScriptRunner(additionalMetadataReferences: RevitApiReference.References));

    private static ExternalEventBridge<ScriptExecutionOutcome> NewBridge() =>
        new(new MCPBridge.Core.Tests.Fakes.FakeExternalEventRaiser());

    private const string SampleNamespace = "MCPBridge.Core.Tests.Dispatch";
    private const string SampleTypeName = "Sample";

    private static DiscoveryService NewDiscoveryService()
    {
        var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", typeof(Sample).Assembly) });
        return new DiscoveryService(cache);
    }

    private static RequestDispatcher NewDispatcher(bool withDiscovery = true) => new(
        NewExecutionManager(),
        NewBridge(),
        NewScriptExecutor(),
        discoveryService: withDiscovery ? NewDiscoveryService() : null);

    private static JsonRpcRequest Parse(object envelope) => JsonRpcRequest.Parse(JsonSerializer.Serialize(envelope));

    [Fact]
    public async Task ListFunctions_ScopedByNamespaceAndType_ReturnsMemberNamesAndTotalScoped()
    {
        var dispatcher = NewDispatcher();
        var request = Parse(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "list_functions",
            @params = new { instance_id = "inst-1", @namespace = SampleNamespace, type_name = SampleTypeName },
        });

        var json = await dispatcher.DispatchAsync(request);

        Assert.Contains("\"members\":\"DoThing, Sample\"", json); // includes the implicit public parameterless constructor
        Assert.Contains("\"total_scoped\":", json);
        Assert.DoesNotContain("\"next_cursor\"", json); // small scope, one page -- must be OMITTED, not null.
    }

    [Fact]
    public async Task ListFunctions_TypeWithoutNamespace_ReturnsInvalidParamsError()
    {
        var dispatcher = NewDispatcher();
        var request = Parse(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "list_functions",
            @params = new { instance_id = "inst-1", type_name = SampleTypeName },
        });

        var json = await dispatcher.DispatchAsync(request);

        Assert.Contains("\"error\":", json);
        Assert.Contains("-32602", json); // InvalidParams
        Assert.Contains("requires params.namespace", json);
    }

    [Fact]
    public async Task ListFunctions_NoArgs_ReturnsNamespaceList()
    {
        var dispatcher = NewDispatcher();
        var request = Parse(new { jsonrpc = "2.0", id = 3, method = "list_functions", @params = new { instance_id = "inst-1" } });

        var json = await dispatcher.DispatchAsync(request);

        Assert.Contains("\"namespaces\":[", json);
        Assert.Contains($"\"namespace\":\"{SampleNamespace}\"", json);
        Assert.Contains("\"type_count\":", json);
    }

    [Fact]
    public async Task SearchFunctions_ReturnsScoredResults()
    {
        var dispatcher = NewDispatcher();
        var request = Parse(new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "search_functions",
            @params = new { instance_id = "inst-1", query = "DoThing" },
        });

        var json = await dispatcher.DispatchAsync(request);

        Assert.Contains("\"results\":[", json);
        Assert.Contains("\"score\":", json);
        Assert.Contains("\"total_matched\":", json);
    }

    [Fact]
    public async Task DescribeFunction_SingleOverload_ReturnsFullDocShape()
    {
        var dispatcher = NewDispatcher();
        var request = Parse(new
        {
            jsonrpc = "2.0",
            id = 5,
            method = "describe_function",
            // DiscoveryReflector normalizes nested-type '+' to '.' (the same dotted convention used
            // everywhere else in this API), so the member reference must use '.' here too, not the raw
            // CLR FullName's '+'.
            @params = new { instance_id = "inst-1", member = "MCPBridge.Core.Tests.Dispatch.DiscoveryDispatchTests.Sample.DoThing" },
        });

        var json = await dispatcher.DispatchAsync(request);

        Assert.Contains("\"overload_count\":1", json);
        Assert.Contains("\"parameters\":[]", json);
    }

    [Fact]
    public async Task DescribeFunction_UnknownMember_ReturnsJsonRpcErrorWithDiscoverySource()
    {
        var dispatcher = NewDispatcher();
        var request = Parse(new
        {
            jsonrpc = "2.0",
            id = 6,
            method = "describe_function",
            @params = new { instance_id = "inst-1", member = "MCPBridge.Core.Tests.Dispatch.DiscoveryDispatchTests+Sample.NoSuchMethod" },
        });

        var json = await dispatcher.DispatchAsync(request);

        Assert.Contains("\"error\":", json);
        Assert.Contains("mcp-bridge.core.discovery", json);
        Assert.Contains("list_functions", json); // remedy names the tool to use instead
    }

    [Fact]
    public async Task ListFunctions_InvalidCursor_ReturnsInvalidParamsError()
    {
        var dispatcher = NewDispatcher();
        var request = Parse(new
        {
            jsonrpc = "2.0",
            id = 7,
            method = "list_functions",
            @params = new { instance_id = "inst-1", @namespace = SampleNamespace, type_name = SampleTypeName, cursor = "not-a-number" },
        });

        var json = await dispatcher.DispatchAsync(request);

        Assert.Contains("\"error\":", json);
        Assert.Contains("-32602", json); // InvalidParams
    }

    [Fact]
    public async Task ListFunctions_NoDiscoveryServiceWired_ReturnsInternalErrorNotCrash()
    {
        var dispatcher = NewDispatcher(withDiscovery: false);
        var request = Parse(new { jsonrpc = "2.0", id = 8, method = "list_functions", @params = new { instance_id = "inst-1" } });

        var json = await dispatcher.DispatchAsync(request);

        Assert.Contains("\"error\":", json);
        Assert.Contains("discovery is not available", json);
    }

    [Fact]
    public async Task Discovery_DoesNotTouchExecutionManager_EvenWhileAnExecutionIsPending()
    {
        // Regression guard for PRD §08's execution-locus requirement: discovery must answer even while a
        // script execution is Busy on this same dispatcher/ExecutionManager -- it must never route through
        // ExecutionManager.Start (which would collide with, or be blocked by, the in-flight execution).
        var executionManager = NewExecutionManager();
        var dispatcher = new RequestDispatcher(executionManager, NewBridge(), NewScriptExecutor(), discoveryService: NewDiscoveryService());

        // Start (but never complete) an execution to occupy the manager's single active slot.
        executionManager.Start("busy-exec", "1+1", maxDurationMs: 600_000, DateTimeOffset.UtcNow);

        var request = Parse(new
        {
            jsonrpc = "2.0",
            id = 9,
            method = "list_functions",
            @params = new { instance_id = "inst-1", @namespace = SampleNamespace, type_name = SampleTypeName },
        });

        var json = await dispatcher.DispatchAsync(request);

        Assert.Contains("\"members\":\"DoThing, Sample\"", json); // includes the implicit public parameterless constructor
        Assert.DoesNotContain("\"status\":\"busy\"", json);
    }
}
