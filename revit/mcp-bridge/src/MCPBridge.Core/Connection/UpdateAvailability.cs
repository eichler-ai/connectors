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
    /// The tag as shown to a person: exactly one leading "v". broker.json carries the release tag as
    /// GitHub publishes it ("v0.1.2"), and the status window used to prepend another -- "vv0.1.1" on
    /// the first live update prompt. Lives here, not in the ribbon command, so a broker that ever
    /// starts writing bare "0.1.2" is caught by the tests rather than by a screenshot.
    /// </summary>
    public static string DisplayTag(string latestAvailableVersion)
    {
        var tag = (latestAvailableVersion ?? string.Empty).Trim();
        return tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? "v" + tag.Substring(1) : "v" + tag;
    }

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

        // Blocker fix (PR #99 independent review): "dev" is the literal fallback value
        // cmd/mcp-server/main.go's `var version = "dev"` ships until a release pipeline sets it via
        // -ldflags -- so every broker in the field currently reports Version: "dev" in broker.json,
        // forever, for now. Without this guard, a plain string-inequality check against any real
        // release tag (e.g. "v0.1.0") always returns true, permanently -- the ribbon would claim an
        // update is available on every single click, even seconds after a successful update, because
        // there is no meaningful comparison to make against a real tag when the broker itself was
        // never built from one. Ordinal/case-sensitive: "dev" is a known sentinel, not a real version
        // string that happens to collide.
        if (string.Equals(runningVersion, "dev", StringComparison.Ordinal))
        {
            return false;
        }

        return !string.Equals(runningVersion, latestAvailableVersion, StringComparison.Ordinal);
    }
}
