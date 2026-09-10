using System.Security.Cryptography;
using System.Text;

namespace Rhino.MCPBridge.Core.Connection;

/// <summary>
/// PRD §12 document identity, the pure half: a saved document is <c>doc-&lt;hash of its normalised
/// absolute path&gt;</c>, stable across reopen and across two Rhinos with the same file; an unsaved one
/// is <c>tmp-&lt;hash of a per-process salt + its title&gt;</c>, stable for the session and distinct
/// across concurrent Rhinos (the same rule the Revit connector settled on after its wrapper-identity
/// bug). Path resolution (symlinks, /Volumes) is the adapter's job; this takes the resolved path.
/// </summary>
public static class DocumentIdentity
{
    public const string SavedPrefix = "doc-";
    public const string UnsavedPrefix = "tmp-";
    public const string GrasshopperPrefix = "gh-";

    /// <param name="resolvedAbsolutePath">Already absolute, symlinks resolved.</param>
    /// <param name="caseInsensitive">True on Windows and macOS (APFS default), false on Linux — the
    /// same file opened with different casing must hash identically where the OS treats it as the same file.</param>
    public static string ForSavedPath(string resolvedAbsolutePath, bool caseInsensitive)
    {
        var normalised = resolvedAbsolutePath.Replace('\\', '/');
        if (caseInsensitive)
        {
            normalised = normalised.ToLowerInvariant();
        }

        return SavedPrefix + ShortHash(normalised);
    }

    /// <param name="processSalt">Minted once per Rhino process (the instance id serves).</param>
    public static string ForUnsaved(Guid processSalt, string title) => UnsavedPrefix + ShortHash(processSalt.ToString("N") + "\n" + title);

    public static string ForGrasshopperPath(string resolvedAbsolutePath, bool caseInsensitive) =>
        GrasshopperPrefix + ForSavedPath(resolvedAbsolutePath, caseInsensitive).Substring(SavedPrefix.Length);

    private static string ShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant(); // 12 hex chars
    }
}
