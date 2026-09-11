using Rhino.MCPBridge.Core.Connection;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// The PRD §12 <c>gh-</c> identity of a Grasshopper definition, isolated so the register snapshot
/// (<see cref="RhinoDocumentSnapshotSource"/>) and the run host's ghdoc resolution
/// (<see cref="RhinoRunHost"/>) compute it identically — an id that lists must resolve. Grasshopper-typed,
/// so callers guard on <see cref="GrasshopperWatcher.GrasshopperLoaded"/> before this JIT-resolves
/// <c>Grasshopper.dll</c>.
/// </summary>
internal static class GrasshopperIdentity
{
    public static string IdOf(global::Grasshopper.Kernel.GH_Document ghdoc, Guid processSalt, bool caseInsensitivePaths, Action<string> log)
    {
        string? path = string.IsNullOrEmpty(ghdoc.FilePath) ? null : RhinoDocumentSnapshotSource.ResolvePathForIdentity(ghdoc.FilePath, log);
        var title = string.IsNullOrEmpty(ghdoc.DisplayName) ? "Untitled" : ghdoc.DisplayName;
        return path is null
            ? DocumentIdentity.ForGrasshopperUnsaved(processSalt, title)
            : DocumentIdentity.ForGrasshopperPath(path, caseInsensitivePaths);
    }
}
