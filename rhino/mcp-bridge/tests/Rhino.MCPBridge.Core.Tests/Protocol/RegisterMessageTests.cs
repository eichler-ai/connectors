using System.Text.Json;
using Rhino.MCPBridge.Core.Protocol;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Protocol;

public sealed class RegisterMessageTests
{
    [Fact]
    public void SerialisesEveryFieldTheServerRegistryReads()
    {
        var id = Guid.NewGuid();
        var snap = new RegisterSnapshot(id, 4242, "8.35.26251.13002", "macos", "dev", new[]
        {
            new RegisteredDocument("doc-abc123abc123", "Tower", "/Users/x/Tower.3dm", true),
            new RegisteredDocument("tmp-def456def456", "Untitled", null, false),
        });
        var json = RegisterMessage.ToJson(snap);
        Assert.DoesNotContain("\n", json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("register", root.GetProperty("method").GetString());
        Assert.False(root.TryGetProperty("id", out _)); // a notification
        var p = root.GetProperty("params");
        Assert.Equal(id.ToString(), p.GetProperty("instance_id").GetString());
        Assert.Equal(4242, p.GetProperty("pid").GetInt32());
        Assert.Equal("8.35.26251.13002", p.GetProperty("rhino_version").GetString());
        Assert.Equal("macos", p.GetProperty("platform").GetString());
        Assert.Equal("dev", p.GetProperty("bridge_version").GetString());
        var docs = p.GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal(2, docs.Count);
        Assert.Equal("doc-abc123abc123", docs[0].GetProperty("document_id").GetString());
        Assert.True(docs[0].GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, docs[1].GetProperty("path").ValueKind);
        Assert.False(docs[1].GetProperty("active").GetBoolean());
    }

    [Fact]
    public void NoDocuments_IsAnEmptyArray_NotAbsent()
    {
        var json = RegisterMessage.ToJson(new RegisterSnapshot(Guid.NewGuid(), 1, "8", "windows", "dev", Array.Empty<RegisteredDocument>()));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("params").GetProperty("documents").GetArrayLength());
    }
}
