using System.Linq;
using Rhino.MCPBridge.Core.Connection;
using Rhino.MCPBridge.Core.Protocol;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// The real snapshot: every open RhinoDoc (many on the Mac, one on Windows — PRD §05) plus every open
/// Grasshopper definition (instance-level, PRD §10), with PRD §12 identity. Must be called on the main
/// thread; the host wraps it in <see cref="IMainThread"/>.
/// </summary>
internal sealed class RhinoDocumentSnapshotSource : IDocumentSnapshotSource
{
    private readonly Guid _processSalt;
    private readonly bool _caseInsensitivePaths;
    private readonly Action<string> _log;

    public RhinoDocumentSnapshotSource(Guid processSalt, bool caseInsensitivePaths, Action<string> log)
    {
        _processSalt = processSalt;
        _caseInsensitivePaths = caseInsensitivePaths;
        _log = log;
    }

    public DocumentSnapshot Snapshot()
    {
        var active = RhinoDoc.ActiveDoc;
        var list = new List<RegisteredDocument>();
        foreach (var doc in RhinoDoc.OpenDocuments())
        {
            if (doc is null) continue;
            string? path = string.IsNullOrEmpty(doc.Path) ? null : ResolvePathForIdentity(doc.Path, _log);
            var title = string.IsNullOrEmpty(doc.Name) ? "Untitled" : doc.Name;
            var id = path is null
                ? DocumentIdentity.ForUnsaved(_processSalt, title)
                : DocumentIdentity.ForSavedPath(path, _caseInsensitivePaths);
            var isActive = active is not null && active.RuntimeSerialNumber == doc.RuntimeSerialNumber;
            list.Add(new RegisteredDocument(id, title, path, isActive));
        }

        return new DocumentSnapshot(list, SnapshotGrasshopper());
    }

    /// <summary>Open Grasshopper definitions, or empty. NEVER force-loads Grasshopper: a register happens
    /// often and loading Grasshopper has real cost and UI side effects, so this returns empty unless the
    /// Grasshopper assembly is already in the process. The GH-typed enumeration lives in its own method so
    /// the JIT resolves Grasshopper.dll only when that guard has already passed.</summary>
    private IReadOnlyList<GrasshopperDocument> SnapshotGrasshopper()
    {
        if (!GrasshopperWatcher.GrasshopperLoaded())
        {
            return Array.Empty<GrasshopperDocument>();
        }

        try
        {
            return EnumerateGrasshopper();
        }
        catch (Exception ex)
        {
            _log($"grasshopper snapshot failed (definitions omitted from this register): {ex.Message}");
            return Array.Empty<GrasshopperDocument>();
        }
    }

    private IReadOnlyList<GrasshopperDocument> EnumerateGrasshopper()
    {
        var server = global::Grasshopper.Instances.DocumentServer;
        if (server is null)
        {
            return Array.Empty<GrasshopperDocument>();
        }

        var activeId = ActiveGrasshopperDocumentId();
        var list = new List<GrasshopperDocument>();
        foreach (global::Grasshopper.Kernel.GH_Document ghdoc in server)
        {
            if (ghdoc is null) continue;
            string? path = string.IsNullOrEmpty(ghdoc.FilePath) ? null : ResolvePathForIdentity(ghdoc.FilePath, _log);
            var title = string.IsNullOrEmpty(ghdoc.DisplayName) ? "Untitled" : ghdoc.DisplayName;
            var id = GrasshopperIdentity.IdOf(ghdoc, _processSalt, _caseInsensitivePaths, _log);
            var isActive = activeId is { } a && a == ghdoc.DocumentID;
            list.Add(new GrasshopperDocument(id, title, path, isActive, ghdoc.Enabled, ghdoc.ObjectCount));
        }

        return list;
    }

    /// <summary>The active definition's <c>DocumentID</c>, read via reflection. Grasshopper's notion of the
    /// active document lives on <c>Grasshopper.Instances.ActiveCanvas</c> (a <c>GH_Canvas</c>, a WinForms
    /// <c>Control</c>) — a type this assembly cannot name without pulling the WinForms framework reference
    /// the headless build deliberately drops. Reflection reaches its <c>Document.DocumentID</c> without a
    /// compile-time dependency; any failure just leaves no definition marked active.</summary>
    private static Guid? ActiveGrasshopperDocumentId()
    {
        try
        {
            var instances = Type.GetType("Grasshopper.Instances, Grasshopper");
            var canvas = instances?.GetProperty("ActiveCanvas")?.GetValue(null);
            var ghdoc = canvas?.GetType().GetProperty("Document")?.GetValue(canvas);
            if (ghdoc?.GetType().GetProperty("DocumentID")?.GetValue(ghdoc) is Guid id)
            {
                return id;
            }
        }
        catch
        {
            // No active document detectable; none is marked active.
        }

        return null;
    }

    /// <summary>Absolute, with symlinks resolved where the OS lets us (PRD §12: /Volumes and symlinked
    /// project folders must not yield two ids for one file). A resolution failure keeps the full path
    /// AND is logged, because it means this document may get a different id than the same file opened
    /// elsewhere -- an identity split that must not be silent (review of #281).</summary>
    internal static string ResolvePathForIdentity(string p, Action<string> log)
    {
        string full;
        try
        {
            full = Path.GetFullPath(p);
        }
        catch (Exception ex)
        {
            log($"document path {p} could not be made absolute ({ex.Message}); its document_id may differ from the same file opened elsewhere");
            return p;
        }

        try
        {
            var target = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? full;
        }
        catch (Exception ex)
        {
            log($"document path {full} could not have its links resolved ({ex.Message}); using the unresolved path for its document_id");
            return full;
        }
    }
}
