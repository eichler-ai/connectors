using System;
using System.IO;

namespace MCPBridge.Core.Workspace;

/// <summary>
/// The per-document file-exchange workspace tree (PRD §09): `imports/`, `exports/`, `logs/`,
/// `scripts/` and `tmp/&lt;instance-id&gt;/`, rooted at
/// `%USERPROFILE%\RevitMCPExchange\&lt;document-id&gt;\` in local mode -- a deliberately separate
/// root from this add-in's own internal app data (`%LOCALAPPDATA%\Connectors\Revit\`, see
/// <see cref="Connection.BrokerDiscoveryOptions"/>), since this tree is human-facing and meant to
/// be browsed directly. `logs/`/`scripts/` are the per-execution audit trail
/// (<see cref="ExecutionAuditTrail"/>, issue #13) and age out on its retention sweep, as does
/// `tmp/`; `imports/`/`exports/` are user-owned and NEVER swept (§09's retention-by-ownership
/// split). Audit directories are created lazily on first write, so a workspace whose document
/// never ran a script doesn't grow empty directories a browsing human would puzzle over.
///
/// Injectable-root factory shape mirrors <see cref="Connection.BrokerDiscoveryOptions.Local"/> so
/// tests can substitute a temp directory for %USERPROFILE%. Directories are created best-effort,
/// idempotently, the first time each path is actually asked for -- matching this codebase's
/// existing Directory.CreateDirectory + try/catch convention (see BridgeHost.Start's discovery
/// cache dir handling).
/// </summary>
public sealed class WorkspacePaths
{
    public string DocumentId { get; }
    public string InstanceId { get; }

    /// <summary>`&lt;root&gt;/RevitMCPExchange/&lt;document-id&gt;/` -- the document's own workspace root.</summary>
    public string DocumentRoot { get; }

    private WorkspacePaths(string documentId, string instanceId, string documentRoot)
    {
        DocumentId = documentId;
        InstanceId = instanceId;
        DocumentRoot = documentRoot;
    }

    /// <summary>
    /// Local mode: %USERPROFILE%\RevitMCPExchange\&lt;document-id&gt;\ (PRD §09). <paramref
    /// name="userProfileRoot"/> lets tests substitute a temp directory for %USERPROFILE%.
    /// </summary>
    public static WorkspacePaths Local(string documentId, string instanceId, string? userProfileRoot = null)
    {
        var root = userProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documentRoot = Path.Combine(root, "RevitMCPExchange", documentId);
        return new WorkspacePaths(documentId, instanceId, documentRoot);
    }

    /// <summary>Files placed here for a script to consume; never auto-deleted (PRD §09).</summary>
    public string Imports => EnsureDirectory(Path.Combine(DocumentRoot, "imports"));

    /// <summary>Images, IFC, families written by scripts via ScriptGlobals.Publish; never auto-deleted.</summary>
    public string Exports => EnsureDirectory(Path.Combine(DocumentRoot, "exports"));

    /// <summary>Per-execution NDJSON audit logs (one PRD §01 record per line); aged out by the retention sweep.</summary>
    public string Logs => EnsureDirectory(Path.Combine(DocumentRoot, "logs"));

    /// <summary>Per-execution verbatim script history; aged out by the retention sweep.</summary>
    public string Scripts => EnsureDirectory(Path.Combine(DocumentRoot, "scripts"));

    /// <summary>
    /// `&lt;root&gt;/RevitMCPExchange/` -- the root ABOVE every document's workspace, which is what the
    /// retention sweep walks (it ages out audit files for every document, not just the one that
    /// happened to trigger it).
    /// </summary>
    public string ExchangeRoot => Path.GetDirectoryName(DocumentRoot)!;

    /// <summary>
    /// Scratch space for this instance sharing this workspace (PRD §09: "tmp/ is the one directory
    /// that isn't [collision-free on its own], so it gets an instance_id subfolder"). Independent PR
    /// review finding: an earlier version let a caller compute a DIFFERENT instance's tmp path via an
    /// optional override parameter -- no real caller wants that; this always uses the instance this
    /// WorkspacePaths was constructed for.
    /// </summary>
    public string Tmp() => EnsureDirectory(Path.Combine(DocumentRoot, "tmp", InstanceId));

    /// <summary>
    /// Touches the user-facing exchange directories (imports/exports) so they exist before a script
    /// runs. Deliberately NOT the audit directories (Logs/Scripts) or Tmp -- those are created
    /// lazily on first actual write, so a document that never runs a script grows no empty
    /// directories a browsing human would puzzle over.
    /// </summary>
    public void EnsureDirectoriesExist()
    {
        _ = Imports;
        _ = Exports;
    }

    private static string EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // Best-effort: a locked-down profile, offline-redirected folder, or full disk must not
            // take down the caller over a workspace directory that can be created lazily later.
        }

        return path;
    }
}
