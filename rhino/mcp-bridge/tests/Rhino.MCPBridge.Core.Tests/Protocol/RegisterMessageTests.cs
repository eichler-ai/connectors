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
            new RegisteredDocument("doc-abc123abc123", "Tower", "/Users/x/Tower.3dm", true,
                new Rhino.MCPBridge.Core.Execution.LastRun { ExecutionId = "exec-1", AgentClientId = "srv-a", FinishedAt = "2026-09-10T00:00:00Z", Status = "success", Label = "box", ChangedDocument = true }),
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
        // PRD §05: last_run per document, absent when none.
        var lr = docs[0].GetProperty("last_run");
        Assert.Equal("exec-1", lr.GetProperty("execution_id").GetString());
        Assert.Equal("srv-a", lr.GetProperty("agent_client_id").GetString());
        Assert.Equal("box", lr.GetProperty("label").GetString());
        Assert.True(lr.GetProperty("changed_document").GetBoolean());
        Assert.False(docs[1].TryGetProperty("last_run", out _));
        Assert.Equal("idle", p.GetProperty("execution_state").GetString());
    }

    [Fact]
    public void NoDocuments_IsAnEmptyArray_NotAbsent()
    {
        var json = RegisterMessage.ToJson(new RegisterSnapshot(Guid.NewGuid(), 1, "8", "windows", "dev", Array.Empty<RegisteredDocument>()));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("params").GetProperty("documents").GetArrayLength());
    }

    [Fact]
    public void SerialisesGrasshopperDocuments_InstanceLevel()
    {
        var snap = new RegisterSnapshot(Guid.NewGuid(), 1, "8.35", "macos", "dev",
            Array.Empty<RegisteredDocument>(), "idle", new[]
            {
                new GrasshopperDocument("gh-abc123abc123", "Tower.gh", "/Users/x/Tower.gh", isActive: true, isEnabled: true, componentCount: 42),
                new GrasshopperDocument("gh-def456def456", "Untitled", null, isActive: false, isEnabled: false, componentCount: 0),
            });
        var json = RegisterMessage.ToJson(snap);
        using var doc = JsonDocument.Parse(json);
        var gh = doc.RootElement.GetProperty("params").GetProperty("gh_documents").EnumerateArray().ToList();
        Assert.Equal(2, gh.Count);
        Assert.Equal("gh-abc123abc123", gh[0].GetProperty("gh_document_id").GetString());
        Assert.Equal("Tower.gh", gh[0].GetProperty("title").GetString());
        Assert.Equal("/Users/x/Tower.gh", gh[0].GetProperty("path").GetString());
        Assert.True(gh[0].GetProperty("active").GetBoolean());
        Assert.True(gh[0].GetProperty("enabled").GetBoolean());
        Assert.Equal(42, gh[0].GetProperty("component_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, gh[1].GetProperty("path").ValueKind);
        Assert.False(gh[1].GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void NoGrasshopperDocuments_OmitsTheField_SoAGrasshopperlessRegisterIsUnchanged()
    {
        // A session with Grasshopper never loaded must serialise exactly as before this field existed.
        var json = RegisterMessage.ToJson(new RegisterSnapshot(Guid.NewGuid(), 1, "8", "windows", "dev", Array.Empty<RegisteredDocument>()));
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("params").TryGetProperty("gh_documents", out _));
    }
}
