using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rhino.MCPBridge.Core.Connection;

/// <summary>
/// instances/&lt;pid&gt;.json (PRD §05, §13): how a server finds this Rhino. Written once at plug-in load
/// after the listener has bound, deleted at unload. Carries the per-instance auth token, so it is
/// written owner-only (0600; an owner-only ACL on Windows is phase 2). A server treats a file whose
/// pid is not a live process as stale and deletes it, so a crashed Rhino leaves nothing behind that
/// survives the next scan.
/// </summary>
public sealed class InstanceFile
{
    public const int SchemaVersion = 1;

    [JsonPropertyName("schema")] public int Schema { get; init; } = SchemaVersion;
    [JsonPropertyName("instance_id")] public string InstanceId { get; init; } = "";
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("token")] public string Token { get; init; } = "";
    [JsonPropertyName("rhino_version")] public string RhinoVersion { get; init; } = "";
    [JsonPropertyName("platform")] public string Platform { get; init; } = "";
    [JsonPropertyName("bridge_version")] public string BridgeVersion { get; init; } = "";
    /// <summary>Reserved for the tool-contract fingerprint (PRD §05); empty until execute_script exists.</summary>
    [JsonPropertyName("schema_fingerprint")] public string SchemaFingerprint { get; init; } = "";
    [JsonPropertyName("started_at")] public DateTimeOffset StartedAt { get; init; }

    public static string PathFor(string instancesDir, int pid) => System.IO.Path.Combine(instancesDir, pid + ".json");

    /// <summary>32 random bytes, hex — the same shape as the Revit server's broker token.</summary>
    public static string MintToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>Writes atomically (temp file + rename) with owner-only permissions. Returns the path written.</summary>
    public string Write(string instancesDir)
    {
        Directory.CreateDirectory(instancesDir);
        var path = PathFor(instancesDir, Pid);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        // Created owner-only from the first byte (review of #281): a write-then-chmod would leave the
        // token world-readable for the gap between the two. On Windows the file inherits
        // %LOCALAPPDATA%'s ACL, which is already the current user only; an explicit ACL is phase 2.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var fs = new FileStream(tmp, options))
        using (var w = new StreamWriter(fs))
        {
            w.Write(json);
        }

        File.Move(tmp, path, overwrite: true);
        return path;
    }

    public static InstanceFile? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<InstanceFile>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the file for pid is present; the host re-asserts a missing one on its heartbeat
    /// so a server's mistaken stale-deletion self-heals (review of #281).</summary>
    public static bool Exists(string instancesDir, int pid) => File.Exists(PathFor(instancesDir, pid));

    /// <summary>Best-effort delete at unload; a failure here is logged by the caller, never thrown — the
    /// server's liveness check reclaims the file anyway.</summary>
    public static bool TryDelete(string instancesDir, int pid)
    {
        try
        {
            File.Delete(PathFor(instancesDir, pid));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
