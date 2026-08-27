using System;
using System.Collections.Concurrent;
using System.IO;

namespace MCPBridge.Core.Workspace;

/// <summary>
/// The per-document file-exchange workspace tree (PRD §09): `imports/`, `exports/`, `logs/`,
/// `scripts/`, and `tmp/&lt;instance-id&gt;/`, rooted at `%USERPROFILE%\RevitMCPExchange\&lt;document-id&gt;\`
/// in local mode -- a deliberately separate root from this add-in's own internal app data
/// (`%LOCALAPPDATA%\Connectors\Revit\`, see <see cref="Connection.BrokerDiscoveryOptions"/>),
/// since this tree is human-facing and meant to be browsed directly.
///
/// Injectable-root factory shape mirrors <see cref="Connection.BrokerDiscoveryOptions.Local"/> so
/// tests can substitute a temp directory for %USERPROFILE%. Directories are created best-effort,
/// idempotently, the first time each path is actually asked for -- matching this codebase's
/// existing Directory.CreateDirectory + try/catch convention (see BridgeHost.Start's discovery
/// cache dir handling).
/// </summary>
public sealed class WorkspacePaths
{
    private static readonly ConcurrentDictionary<string, string> Aliases = new(StringComparer.Ordinal);

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

    /// <summary>Per-execution NDJSON logs; ages out (not implemented in this pass).</summary>
    public string Logs => EnsureDirectory(Path.Combine(DocumentRoot, "logs"));

    /// <summary>History of executed script text; ages out (not implemented in this pass).</summary>
    public string Scripts => EnsureDirectory(Path.Combine(DocumentRoot, "scripts"));

    /// <summary>
    /// Scratch space for one instance sharing this workspace (PRD §09: "tmp/ is the one directory
    /// that isn't [collision-free on its own], so it gets an instance_id subfolder"). Defaults to
    /// this instance's own <see cref="InstanceId"/> if none is given.
    /// </summary>
    public string Tmp(string? instanceId = null) =>
        EnsureDirectory(Path.Combine(DocumentRoot, "tmp", instanceId ?? InstanceId));

    /// <summary>Touches every directory except the per-instance tmp/ one, so they all exist up front.</summary>
    public void EnsureDirectoriesExist()
    {
        _ = Imports;
        _ = Exports;
        _ = Logs;
        _ = Scripts;
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

    /// <summary>
    /// Best-effort "promotion on first save" support (PRD §09): renames an existing document-root
    /// folder in place from its old identity to its new one, so exports/logs/scripts carry over
    /// rather than orphaning under a stale `tmp-&lt;guid&gt;` id. Never throws -- any failure (the
    /// destination already exists, a locked file, a permissions issue) degrades to leaving the old
    /// folder in place; the caller is still expected to register an alias regardless, so an agent
    /// still holding the old id keeps working via <see cref="ResolveAlias"/>.
    /// </summary>
    public static bool TryPromoteDocumentRoot(string oldDocumentId, string newDocumentId, string? userProfileRoot = null)
    {
        try
        {
            var root = userProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var workspaceRoot = Path.Combine(root, "RevitMCPExchange");
            var oldPath = Path.Combine(workspaceRoot, oldDocumentId);
            var newPath = Path.Combine(workspaceRoot, newDocumentId);

            if (!Directory.Exists(oldPath) || Directory.Exists(newPath))
            {
                return false;
            }

            Directory.Move(oldPath, newPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Registers a short-lived, in-memory-only alias from an old document_id to its new one (PRD
    /// §09: "the broker keeps a short-lived alias from the old ID to the new one ... only needs to
    /// survive one save-to-next-poll window"). Not persisted across a process restart by design.
    /// </summary>
    public static void RegisterAlias(string oldDocumentId, string newDocumentId) =>
        Aliases[oldDocumentId] = newDocumentId;

    /// <summary>Resolves a possibly-stale document_id through any alias registered for it, or returns it unchanged.</summary>
    public static string ResolveAlias(string documentId) =>
        Aliases.TryGetValue(documentId, out var resolved) ? resolved : documentId;
}
