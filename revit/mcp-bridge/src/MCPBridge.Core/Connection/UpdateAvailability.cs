using System;

namespace MCPBridge.Core.Connection;

/// <summary>
/// Whether a newer connector release is available, purely from broker.json's own Version/
/// LatestAvailableVersion fields (PRD §12) -- opaque string comparison against GitHub's release tag,
/// the same shape install.ps1 itself already uses. Deliberately does NOT compare against the add-in's
/// own embedded build version (MCPBridgeEmbedVersion) -- broker and add-in ship in the same release
/// zip, so the broker's self-reported Version is what matters here, not a second, redundant source.
/// </summary>
public static class UpdateAvailability
{
    /// <summary>
    /// True when both <paramref name="runningVersion"/> and <paramref name="latestAvailableVersion"/>
    /// are non-null/non-empty (whitespace-only counts as empty) and differ by plain ordinal string
    /// comparison. Either missing/unknown means "no update shown" -- never a false positive from
    /// incomplete data. No semver ordering: a running version that happens to be "newer" than
    /// "latest" by string comparison still reports available, matching install.ps1's own opaque
    /// equality-against-release-tag comparison shape.
    /// </summary>
    public static bool IsAvailable(string? runningVersion, string? latestAvailableVersion)
    {
        if (string.IsNullOrWhiteSpace(runningVersion) || string.IsNullOrWhiteSpace(latestAvailableVersion))
        {
            return false;
        }

        return !string.Equals(runningVersion, latestAvailableVersion, StringComparison.Ordinal);
    }
}
