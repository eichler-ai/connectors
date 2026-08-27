using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Computes a Revit document's stable `document_id` per PRD §09's four-state table.
///
/// Lives in RevitAdapter, not Core, specifically so <see cref="ResolveCached"/> can key its cache on
/// the raw Autodesk.Revit.DB.Document reference (Core is not allowed to reference Revit API types at
/// all, and MCPBridge.Core.csproj already references MCPBridge.RevitAdapter.csproj -- a reference the
/// other way around would be circular). <see cref="Resolve"/> itself is still pure logic against the
/// <see cref="IDocumentAdapter"/>/<see cref="IUncPathResolver"/> seam and fully unit-testable with
/// fakes (MCPBridge.Core.Tests already references this assembly).
///
/// <list type="bullet">
/// <item>Workshared -&gt; UNC-resolve then hash <see cref="IDocumentAdapter.CentralModelPath"/>
/// (never the local, per-user copy path, which is regenerated on every fresh local copy).</item>
/// <item>Saved, non-workshared -&gt; UNC-resolve <see cref="IDocumentAdapter.PathName"/> (so a
/// mapped drive letter and its UNC-equivalent path hash identically), then hash it.</item>
/// <item>Unsaved (no path at all) -&gt; a fresh `tmp-&lt;guid&gt;` minted on every call to
/// <see cref="Resolve"/>. This method has no notion of "the same document across two calls" --
/// see <see cref="ResolveCached"/> for the caller-side caching every real call site actually uses.</item>
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
            if (string.IsNullOrEmpty(document.CentralModelPath))
            {
                return MintTmp();
            }

            var resolvedCentral = uncResolver.Resolve(document.CentralModelPath);
            return "doc-" + HashNormalizedPath(resolvedCentral);
        }

        if (!string.IsNullOrEmpty(document.PathName))
        {
            var resolved = uncResolver.Resolve(document.PathName);
            return "doc-" + HashNormalizedPath(resolved);
        }

        return MintTmp();
    }

    // Process-lifetime, keyed by the live Document reference (not by path -- an unsaved document has
    // none). This is the ONE cache every real call site shares: RevitDocumentAdapter.DocumentId (used
    // to build the exports/imports workspace for execute_script/Publish) and
    // DocumentSnapshotHandler's register snapshot both resolve through ResolveCached, so they always
    // agree on the same id for the same live document -- an agent isn't told doc-A by `register` while
    // Publish quietly writes into a workspace computed as doc-B for the same open document.
    //
    // Independent PR review finding: resolving fresh (uncached) on every call is not just wrong for an
    // unsaved document (Resolve mints a brand-new tmp-<guid> every time by design -- see its own doc
    // comment), it silently scatters that document's published files across a new, never-revisited
    // workspace directory on every single execute_script call, with no retention sweep to reclaim them.
    private static readonly ConditionalWeakTable<Document, string> Cache = new();

    /// <summary>
    /// Cached, re-resolve-while-unsaved wrapper around <see cref="Resolve"/>, keyed by the live
    /// <paramref name="document"/> reference: unresolved -&gt; resolve and cache. Cached and still
    /// `tmp-` -&gt; re-resolve (a `tmp-` id is never assumed final, since the document may have been
    /// saved since the last call) and, if that now returns a `doc-` id, update the cache to it. Cached
    /// and already `doc-` -&gt; treated as final, never re-resolved (matches Resolve's own contract: a
    /// durable id, once minted, doesn't change).
    ///
    /// Deliberately does NOT rename any workspace folder or register an alias on that tmp-&gt;doc
    /// transition (an earlier version of this feature did both -- independent PR review found the
    /// rename unreachable in practice, since nothing re-resolves identity on save itself, only on the
    /// next register/execute_script call, by which point the "old" workspace folder this cache would
    /// have renamed was never the one anything actually wrote into; and found the alias never read
    /// anywhere in production). The honest, simpler behavior instead: a `tmp-` document's already-
    /// published files stay under its `tmp-` workspace for the rest of that session; once the document
    /// gets its `doc-` id (on the next call to this method after a save), it gets a fresh workspace
    /// going forward. PRD §09's "promotion on first save" (rename + alias) is not implemented by this
    /// pass -- see the PRD's own note on this.
    /// </summary>
    public static string ResolveCached(Document document, IUncPathResolver uncResolver)
    {
        if (!Cache.TryGetValue(document, out var cachedId))
        {
            var freshId = SafeResolve(document, uncResolver, fallback: null) ?? MintTmp();
            Cache.Add(document, freshId);
            return freshId;
        }

        if (!cachedId.StartsWith("tmp-", StringComparison.Ordinal))
        {
            return cachedId; // durable doc- id -- treated as final.
        }

        var resolved = SafeResolve(document, uncResolver, fallback: cachedId);
        if (resolved is null || !resolved.StartsWith("doc-", StringComparison.Ordinal))
        {
            return cachedId; // still unsaved, or resolution failed -- keep the existing tmp- id.
        }

        Cache.AddOrUpdate(document, resolved);
        return resolved;
    }

    private static string? SafeResolve(Document document, IUncPathResolver uncResolver, string? fallback)
    {
        try
        {
            return Resolve(new RevitDocumentAdapter(document, uncResolver), uncResolver);
        }
        catch
        {
            // Best-effort: a document mid-transition (e.g. detaching from central) can throw from
            // Document's own accessors -- degrade to the fallback rather than letting one odd
            // document break identity resolution for the caller.
            return fallback;
        }
    }

    private static string MintTmp() => "tmp-" + Guid.NewGuid();

    private static string HashNormalizedPath(string path)
    {
        var normalized = path.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..HashHexLength];
    }
}
