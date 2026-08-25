using System;
using System.Collections.Generic;
using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Protocol;

public class RegisterMessageTests
{
    [Fact]
    public void ToJson_IncludesAuthToken_ForHandshake()
    {
        var instanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var message = new RegisterMessage(
            instanceId: instanceId,
            pid: 4242,
            revitVersion: "2027",
            documents: Array.Empty<RegisteredDocument>(),
            authToken: "s3cr3t");

        var json = message.ToJson();

        Assert.Contains("\"method\":\"register\"", json);
        Assert.Contains("\"token\":\"s3cr3t\"", json);
        Assert.Contains("\"instance_id\":\"11111111-1111-1111-1111-111111111111\"", json);
        Assert.Contains("\"pid\":4242", json);
        Assert.Contains("\"revit_version\":\"2027\"", json);
        Assert.Contains("\"jsonrpc\":\"2.0\"", json);
    }

    [Fact]
    public void ToJson_IsSingleLine_SafeForNdjsonFraming()
    {
        var message = new RegisterMessage(
            Guid.NewGuid(), 1, "2027",
            new[] { new RegisteredDocument("doc-abc123", "Project1.rvt", @"C:\models\Project1.rvt", isWorkshared: false, isActive: true) },
            "tok");

        var json = message.ToJson();

        Assert.DoesNotContain("\n", json);
    }

    [Fact]
    public void ToJson_SerializesDocumentList()
    {
        var docs = new List<RegisteredDocument>
        {
            new("doc-abc123", "Project1.rvt", @"C:\models\Project1.rvt", isWorkshared: true, isActive: true),
        };
        var message = new RegisterMessage(Guid.NewGuid(), 1, "2027", docs, "tok");

        var json = message.ToJson();

        Assert.Contains("\"document_id\":\"doc-abc123\"", json);
        Assert.Contains("\"workshared\":true", json);
        Assert.Contains("\"active\":true", json);
    }
}
