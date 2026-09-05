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

    /// <summary>
    /// The Status window's add-in line (self-update-architecture.md §6.2, issue #209), in the
    /// <c>installed · running</c> shape: the version the installer's pointer (<c>addin\current.json</c>)
    /// says a NEW Revit would load, against the version THIS process actually loaded, with the one-line
    /// remedy only when they differ. Under the shim an add-in update is a pointer flip that closes
    /// nothing, so a running Revit keeps the previous add-in until it is restarted -- this line is how
    /// the user learns that. Degrades to the single running value when the two agree, when the running
    /// build is the unreleased "dev" sentinel (which cannot be compared with anything, as in
    /// <see cref="IsAvailable"/>), or -- defensively; the shim is the only layout, so a shim-loaded
    /// add-in always has a pointer -- when the pointer could not be read.
    /// </summary>
    public static string AddInStatusLine(string? runningVersion, string? installedPointerVersion)
    {
        var running = (runningVersion ?? string.Empty).Trim();
        if (running.Length == 0 || string.Equals(running, "dev", StringComparison.Ordinal))
        {
            return "dev build";
        }

        var runningTag = FolderTag(running);
        var installed = (installedPointerVersion ?? string.Empty).Trim();
        if (installed.Length == 0)
        {
            return runningTag;
        }

        // Compared the way the folder was named: one leading "v" either way, case-insensitively, so a
        // pointer written as "0.1.5" and an assembly stamped "v0.1.5" do not read as a pending restart.
        // Assumes both sides are the SAME tag shape -- the release tag, which the pipeline stamps into
        // the assembly (MCPBRIDGE_VERSION) and the installer writes into current.json verbatim. A
        // 4-part assembly version against a 3-part tag would always differ here; nothing feeds that.
        var installedTag = FolderTag(installed);
        if (string.Equals(installedTag, runningTag, StringComparison.OrdinalIgnoreCase))
        {
            return runningTag;
        }

        return $"{installedTag} installed · running {runningTag} — restart Revit to load it";
    }

    /// <summary>
    /// <see cref="DisplayTag"/> for a release tag; anything that is not one (install.ps1's
    /// <c>local-&lt;timestamp&gt;</c> tag for a <c>-LocalPackagePath</c> install) is shown as written,
    /// rather than as "vlocal-…".
    /// </summary>
    private static string FolderTag(string tag)
    {
        var t = tag.Trim();
        var bare = t.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? t.Substring(1) : t;
        return bare.Length > 0 && char.IsDigit(bare[0]) ? DisplayTag(t) : t;
    }
}
