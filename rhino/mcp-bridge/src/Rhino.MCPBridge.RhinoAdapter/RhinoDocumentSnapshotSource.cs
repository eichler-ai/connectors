using Rhino.MCPBridge.Core.Connection;
using Rhino.MCPBridge.Core.Protocol;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// The real snapshot: every open RhinoDoc (many on the Mac, one on Windows — PRD §05), with PRD §12
/// identity. Must be called on the main thread; the host wraps it in <see cref="IMainThread"/>.
/// </summary>
internal sealed class RhinoDocumentSnapshotSource : IDocumentSnapshotSource
{
    private readonly Guid _processSalt;
    private readonly bool _caseInsensitivePaths;

    public RhinoDocumentSnapshotSource(Guid processSalt, bool caseInsensitivePaths)
    {
        _processSalt = processSalt;
        _caseInsensitivePaths = caseInsensitivePaths;
    }

    public IReadOnlyList<RegisteredDocument> Snapshot()
    {
        var active = RhinoDoc.ActiveDoc;
        var list = new List<RegisteredDocument>();
        foreach (var doc in RhinoDoc.OpenDocuments())
        {
            if (doc is null) continue;
            string? path = string.IsNullOrEmpty(doc.Path) ? null : ResolvePath(doc.Path);
            var title = string.IsNullOrEmpty(doc.Name) ? "Untitled" : doc.Name;
            var id = path is null
                ? DocumentIdentity.ForUnsaved(_processSalt, title)
                : DocumentIdentity.ForSavedPath(path, _caseInsensitivePaths);
            var isActive = active is not null && active.RuntimeSerialNumber == doc.RuntimeSerialNumber;
            list.Add(new RegisteredDocument(id, title, path, isActive));
        }

        return list;
    }

    /// <summary>Absolute, with symlinks resolved where the OS lets us (PRD §12: /Volumes and symlinked
    /// project folders must not yield two ids for one file). Best-effort: an unresolvable link keeps the full path.</summary>
    private static string ResolvePath(string p)
    {
        try
        {
            var full = Path.GetFullPath(p);
            var target = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? full;
        }
        catch
        {
            return p;
        }
    }
}
