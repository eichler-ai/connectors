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
    private readonly Action<string> _log;

    public RhinoDocumentSnapshotSource(Guid processSalt, bool caseInsensitivePaths, Action<string> log)
    {
        _processSalt = processSalt;
        _caseInsensitivePaths = caseInsensitivePaths;
        _log = log;
    }

    public IReadOnlyList<RegisteredDocument> Snapshot()
    {
        var active = RhinoDoc.ActiveDoc;
        var list = new List<RegisteredDocument>();
        foreach (var doc in RhinoDoc.OpenDocuments())
        {
            if (doc is null) continue;
            string? path = string.IsNullOrEmpty(doc.Path) ? null : ResolvePath(doc.Path, _log);
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
    /// project folders must not yield two ids for one file). A resolution failure keeps the full path
    /// AND is logged, because it means this document may get a different id than the same file opened
    /// elsewhere -- an identity split that must not be silent (review of #281).</summary>
    private static string ResolvePath(string p, Action<string> log)
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
