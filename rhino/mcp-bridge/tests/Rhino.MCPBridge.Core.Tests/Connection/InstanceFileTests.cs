using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Rhino.MCPBridge.Core.Connection;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Connection;

public sealed class InstanceFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcpbridge-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static InstanceFile Sample(int pid = 1234) => new()
    {
        InstanceId = Guid.NewGuid().ToString(), Pid = pid, Port = 50123, Token = InstanceFile.MintToken(),
        RhinoVersion = "8.35", Platform = "macos", BridgeVersion = "dev", StartedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Write_CreatesTheDirectory_NamesTheFileByPid_AndRoundTrips()
    {
        var f = Sample(777);
        var path = f.Write(_dir);
        Assert.Equal(Path.Combine(_dir, "777.json"), path);
        var back = InstanceFile.TryRead(path)!;
        Assert.Equal(f.InstanceId, back.InstanceId);
        Assert.Equal(f.Port, back.Port);
        Assert.Equal(f.Token, back.Token);
        Assert.Equal(InstanceFile.SchemaVersion, back.Schema);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp")); // atomic: no temp file left
    }

    [Fact]
    public void Write_UsesTheWireFieldNames()
    {
        var path = Sample().Write(_dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var name in new[] { "schema", "instance_id", "pid", "port", "token", "rhino_version", "platform", "bridge_version", "schema_fingerprint", "started_at" })
        {
            Assert.True(doc.RootElement.TryGetProperty(name, out _), name);
        }
    }

    [Fact]
    public void Write_IsOwnerOnly()
    {
        var path = Sample().Write(_dir);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Phase 2: an explicit, non-inherited DACL granting only the current user (PRD §13), the
            // Windows counterpart of the 0600 mode below. This exercises InstanceFile.OwnerOnlyDacl
            // through a real write.
            Assert.True(File.Exists(path));
            Assert.NotNull(InstanceFile.TryRead(path));
            Assert.Single(Directory.GetFiles(_dir));
            AssertWindowsOwnerOnly(path);
            return;
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsOwnerOnly(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected); // inheritance disabled, so no other identity leaks in
        var me = WindowsIdentity.GetCurrent().User;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.Equal(me, r.IdentityReference)); // only the current user appears
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Allow && r.FileSystemRights.HasFlag(FileSystemRights.Read));
    }

    [Fact]
    public void Write_NeverLeavesTheTokenWorldReadable_EvenMidWrite()
    {
        var path = Sample().Write(_dir);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Created WITH the owner-only DACL (FileSystemAclExtensions.Create), so there is no
            // create-then-secure gap, and the atomic rename leaves no *.tmp with a wider ACL behind.
            AssertWindowsOwnerOnly(path);
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
            return;
        }

        // The temp file is created owner-only; there is no chmod-after-write gap to race.
        // Observed through the final file's mode plus the absence of a *.tmp with a wider mode.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Write_Twice_Overwrites()
    {
        Sample(5).Write(_dir);
        var second = Sample(5); second.Write(_dir);
        Assert.Equal(second.Token, InstanceFile.TryRead(InstanceFile.PathFor(_dir, 5))!.Token);
    }

    [Fact]
    public void MintToken_Is64Hex_AndUnique()
    {
        var a = InstanceFile.MintToken(); var b = InstanceFile.MintToken();
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9a-f]+$", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TryRead_ReturnsNullForMissingOrMalformed()
    {
        Assert.Null(InstanceFile.TryRead(Path.Combine(_dir, "nope.json")));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{not json");
        Assert.Null(InstanceFile.TryRead(Path.Combine(_dir, "bad.json")));
    }

    [Fact]
    public void TryDelete_IsIdempotent()
    {
        Sample(9).Write(_dir);
        Assert.True(InstanceFile.TryDelete(_dir, 9));
        Assert.True(InstanceFile.TryDelete(_dir, 9)); // File.Delete on a missing file is not an error
        Assert.False(File.Exists(InstanceFile.PathFor(_dir, 9)));
    }
}
