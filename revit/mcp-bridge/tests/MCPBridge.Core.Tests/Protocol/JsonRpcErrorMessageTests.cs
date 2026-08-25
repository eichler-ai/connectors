using System.Collections.Generic;
using System.Text.Json;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Protocol;

public class JsonRpcErrorMessageTests
{
    [Fact]
    public void ToJson_WithoutData_OmitsDataField()
    {
        var id = JsonSerializer.SerializeToElement(1);

        var json = JsonRpcErrorMessage.ToJson(id, JsonRpcErrorCode.InvalidParams, "bad params", data: null);

        Assert.Contains("\"jsonrpc\":\"2.0\"", json);
        Assert.Contains("\"id\":1", json);
        Assert.Contains("\"code\":-32602", json);
        Assert.Contains("\"message\":\"bad params\"", json);
        Assert.DoesNotContain("\"data\"", json);
    }

    [Fact]
    public void ToJson_WithData_UsesSharedDiagnosticRecordShape()
    {
        var id = JsonSerializer.SerializeToElement("abc");
        var diagnostic = DiagnosticRecord.Create(
            DiagnosticSeverity.Error,
            "unknown_execution_id",
            DiagnosticSource.Execution,
            "execution_id 'exec-1' is not known to this add-in instance.",
            detail: new Dictionary<string, object?> { ["execution_id"] = "exec-1" },
            remedy: null);

        var json = JsonRpcErrorMessage.ToJson(id, JsonRpcErrorCode.InvalidParams, "unknown execution", diagnostic);

        Assert.Contains("\"id\":\"abc\"", json);
        Assert.Contains("\"data\":{", json);
        Assert.Contains("\"code\":\"unknown_execution_id\"", json);
        Assert.Contains("\"severity\":\"error\"", json);
    }

    [Fact]
    public void ToJson_IsSingleLine_SafeForNdjsonFraming()
    {
        var id = JsonSerializer.SerializeToElement(1);

        var json = JsonRpcErrorMessage.ToJson(id, JsonRpcErrorCode.InternalError, "boom", null);

        Assert.DoesNotContain("\n", json);
    }
}
