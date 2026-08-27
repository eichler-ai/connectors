using System;
using System.Security.Cryptography;
using System.Text;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Identity;

/// <summary>
/// Computes a Revit document's stable `document_id` per PRD §09's four-state table. Pure logic,
/// tested against <see cref="IDocumentAdapter"/>/<see cref="IUncPathResolver"/> fakes -- the real
/// Revit-API-touching accessors live in RevitAdapter (RevitDocumentAdapter, Win32UncPathResolver).
///
/// <list type="bullet">
/// <item>Workshared -&gt; hash <see cref="IDocumentAdapter.CentralModelPath"/> (never the local,
/// per-user copy path, which is regenerated on every fresh local copy).</item>
/// <item>Saved, non-workshared -&gt; UNC-resolve <see cref="IDocumentAdapter.PathName"/> (so a
/// mapped drive letter and its UNC-equivalent path hash identically), then hash it.</item>
/// <item>Unsaved (no path at all) -&gt; a fresh `tmp-&lt;guid&gt;` minted on every call. This class
/// has no notion of "the same document across two calls" -- the caller is responsible for
/// caching a document's identity for its own lifetime (the same way DocumentSnapshotHandler
/// already caches per live Document reference via a ConditionalWeakTable), exactly as PRD §09's
/// "session-scoped GUID minted on open" implies.</item>
/// </list>
///
/// Hashing: SHA-256 of the case-normalized (invariant-lowercase) path, hex-encoded and truncated
/// to 16 characters. Case normalization mirrors Windows path comparison semantics
/// (OrdinalIgnoreCase, per PRD §09) so the same file opened via two differently-cased paths -- or
/// via a mapped drive letter vs. its UNC-resolved equivalent -- hashes identically. The exact
/// algorithm/length is not itself part of the wire contract (only the `doc-`/`tmp-` prefix is);
/// 16 hex characters is short enough to keep workspace directory names manageable while staying
/// collision-resistant at this project's scale (a handful of documents per machine, not millions).
/// </summary>
public static class DocumentIdentity
{
    private const int HashHexLength = 16;

    public static string Resolve(IDocumentAdapter document, IUncPathResolver uncResolver)
    {
        if (document.IsWorkshared)
        {
            return string.IsNullOrEmpty(document.CentralModelPath)
                ? MintTmp()
                : "doc-" + HashNormalizedPath(document.CentralModelPath);
        }

        if (!string.IsNullOrEmpty(document.PathName))
        {
            var resolved = uncResolver.Resolve(document.PathName);
            return "doc-" + HashNormalizedPath(resolved);
        }

        return MintTmp();
    }

    private static string MintTmp() => "tmp-" + Guid.NewGuid();

    private static string HashNormalizedPath(string path)
    {
        var normalized = path.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..HashHexLength];
    }
}
