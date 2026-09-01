using System;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// A single reading of THIS process's memory (issue #31), captured in-process by the add-in and pushed
/// on the heartbeat ping so the broker can surface it in list_instances. PrivateMB (committed) is the
/// headline signal for the document-model growth #31 tracks -- WorkingSet is OS-trimmed and noisy;
/// ManagedMB (the CLR heap) separates the connector's own managed footprint from Revit's native
/// document memory, which is what a gcdump showed to be the bulk of the growth.
/// </summary>
public sealed class MemorySnapshot
{
    private const long BytesPerMb = 1024 * 1024;

    [JsonPropertyName("private_mb")]
    public long PrivateMB { get; set; }

    [JsonPropertyName("working_set_mb")]
    public long WorkingSetMB { get; set; }

    [JsonPropertyName("managed_mb")]
    public long ManagedMB { get; set; }

    /// <summary>
    /// Samples the current process. A FRESH Process handle plus Refresh() defeats the cached-counter
    /// trap (a reused Process object keeps returning its first reading). GetTotalMemory(false) does NOT
    /// force a collection -- the heartbeat path must stay cheap and side-effect-free.
    /// </summary>
    public static MemorySnapshot Capture()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return new MemorySnapshot
        {
            PrivateMB = p.PrivateMemorySize64 / BytesPerMb,
            WorkingSetMB = p.WorkingSet64 / BytesPerMb,
            ManagedMB = GC.GetTotalMemory(false) / BytesPerMb,
        };
    }
}
