using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// The globals object exposed to script scope (PRD §06 step 3 / "Cancellation" -
/// cooperative path). Deliberately built on the RevitAdapter interfaces, not raw
/// Revit API types, so scripts run against fakes in unit tests exercise the exact
/// same globals shape a live script would see.
///
/// Forward-compatibility risk, flagged deliberately (adversarial code review,
/// Phase 1): IScriptDocument/IUiApplicationAdapter/IUiDocumentAdapter today
/// expose only what Phase 1's own trivial-expression scripts need (Title) --
/// nowhere near real Revit API access (element queries, geometry, etc.).
/// Because this exact globals shape is what real scripts will bind against by
/// name once discovery/execute_script grows real API surface (Phase 3+),
/// growing these interfaces later either balloons them into re-implementing
/// large parts of the Revit API (defeating the "thin seam" premise) or forces
/// a breaking change to a globals type scripts already depend on. Revisit
/// this class's shape explicitly before Phase 3 lands real API access --
/// don't let it grow ad hoc, member by member.
/// </summary>
public sealed class ScriptGlobals
{
    // Property casing here is a public, external contract (PRD §06): an agent-authored script
    // binds to these identifiers by name in its scope, so it must match the PRD's published
    // names -- Document, UIApplication, UIDocument -- exactly.
    //
    // Document/UIApplication/UIDocument are deliberately typed as IScriptDocument/IScriptUiApplication/
    // IScriptUiDocument, not IDocumentAdapter/IUiApplicationAdapter/IUiDocumentAdapter: CreateTransaction/
    // CreateTransactionGroup exist on IDocumentAdapter for TransactionScriptExecutor's own ambient
    // Transaction/TransactionGroup (which already wraps every script run), not for the script to call --
    // Revit only allows one open Transaction per Document, so a script calling CreateTransaction on the
    // same Document the executor already opened one on always fails, whether reached via `Document`,
    // `UIDocument.Document`, or `UIApplication.ActiveUiDocument.Document` -- a second independent PR
    // review found this third path still reachable after the first two were closed, so ALL THREE globals
    // are narrowed, not just the two that had already been caught. Confirmed live, not hypothetical --
    // see IScriptDocument's doc comment.
    public IScriptDocument Document { get; }
    public IScriptUiApplication UIApplication { get; }
    public IScriptUiDocument? UIDocument { get; }
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// This document's imports/ directory (PRD §09) -- where a human places a file for a script to
    /// consume via ordinary System.IO. Null when no workspace is known for this execution (matches
    /// ExportsDirectory's null-ability below; see its doc comment for why that can happen).
    /// </summary>
    public string? ImportsDirectory { get; }

    /// <summary>
    /// This document's exports/ directory (PRD §09) -- the destination <see cref="Publish"/> copies
    /// into. Exposed directly (not just implicitly via Publish) so a script can also read back its
    /// own or a prior run's exports via plain System.IO. Null only when no workspace is known for
    /// this execution -- TransactionScriptExecutor always supplies one when a document is active;
    /// this stays nullable purely so existing tests that don't care about file exchange at all
    /// don't need to pass one.
    /// </summary>
    public string? ExportsDirectory { get; }

    private readonly bool _overwriteOutputFiles;
    private readonly List<PublishedFileRecord> _publishedFiles = new();

    /// <summary>
    /// Every file this execution's script has published so far, in call order -- read once, after
    /// the run finishes, by TransactionScriptExecutor to build the result's files[] (PRD §09).
    /// Internal, not part of the script-facing surface: a script publishes via <see cref="Publish"/>,
    /// it doesn't inspect what it's already published.
    /// </summary>
    internal IReadOnlyList<PublishedFileRecord> PublishedFiles => _publishedFiles;

    /// <summary>
    /// Per-script override of the default-safe DialogBoxShowing auto-answer policy (PRD §07: "unless
    /// the script explicitly opts into a different per-call policy"). Keyed by
    /// DialogBoxShowingEventArgs.DialogId (a string Revit assigns per dialog template), value is the
    /// raw OverrideResult(int) to use instead of the handler's own default. A script sets this before
    /// triggering the dialog, e.g. `DialogResultOverrides["TaskDialog_Some_Id"] = 1001;`. Deliberately a
    /// flat dictionary, not a richer typed API -- OverrideResult(int) already takes exactly this shape.
    /// </summary>
    public IDictionary<string, int> DialogResultOverrides { get; } = new Dictionary<string, int>();

    public ScriptGlobals(
        IDocumentAdapter document,
        IUiApplicationAdapter uiApplication,
        IUiDocumentAdapter? uiDocument,
        CancellationToken cancellationToken,
        string? exportsDirectoryPath = null,
        string? importsDirectoryPath = null,
        bool overwriteOutputFiles = false)
    {
        Document = document;
        UIApplication = uiApplication;
        UIDocument = uiDocument;
        CancellationToken = cancellationToken;
        ExportsDirectory = exportsDirectoryPath;
        ImportsDirectory = importsDirectoryPath;
        _overwriteOutputFiles = overwriteOutputFiles;
    }

    /// <summary>
    /// Publishes a script output file to this execution's exports/ directory (PRD §09 "Publishing
    /// script outputs"). Copies (never moves) <paramref name="sourcePath"/> to
    /// <c>&lt;exports&gt;/&lt;name ?? Path.GetFileName(sourcePath)&gt;</c> and records the result in
    /// <see cref="PublishedFiles"/> -- every call records exactly one <see cref="PublishedFileRecord"/>,
    /// whether it succeeds or fails, and this method itself NEVER throws: a script's own untrusted
    /// code calls this by name, and a failure on one file (disk full, a locked target, a bad source
    /// path) must never roll back or block the rest of the script or any other file it publishes.
    ///
    /// Collisions are controlled by this execution's overwrite_output_files flag (PRD §09): with the
    /// default false, a Publish call that would overwrite an existing destination file becomes a
    /// status:"failed" entry naming the flag, never a silent skip and never an abort of anything else
    /// the script does.
    ///
    /// <paramref name="name"/>, when given, is constrained to its bare file name (via
    /// Path.GetFileName) before use -- an absolute path or a `..\..` traversal in name can't place
    /// the published file outside this document's exports directory (Publish's whole contract is
    /// "this lands in THIS document's exports directory"). A name that reduces to nothing (e.g. a
    /// bare "..\" or a trailing separator) fails rather than silently falling back to some other name.
    /// </summary>
    public void Publish(string sourcePath, string? name = null)
    {
        if (ExportsDirectory is null)
        {
            // Defensive: no exports directory known for this execution. Should not happen from a
            // real script run (TransactionScriptExecutor always supplies one when a document is
            // active), but scripts are untrusted and this must never throw -- best-effort no-op.
            return;
        }

        string displayName;
        if (!string.IsNullOrEmpty(name))
        {
            displayName = SafeGetFileName(name);
            if (displayName.Length == 0)
            {
                _publishedFiles.Add(new PublishedFileRecord(name, name, PublishedFileRecord.StatusFailed, $"'{name}' is not a valid file name."));
                return;
            }
        }
        else
        {
            displayName = SafeGetFileName(sourcePath);
            if (displayName.Length == 0)
            {
                // Best-effort: fall back to the raw source path as the reported name rather than
                // failing outright -- the copy attempt below will fail on its own if sourcePath is
                // genuinely unusable, and that failure is what actually gets reported.
                displayName = sourcePath;
            }
        }

        try
        {
            var destinationPath = Path.Combine(ExportsDirectory, displayName);
            var normalizedSource = NormalizeFullPath(sourcePath);
            var normalizedDestination = NormalizeFullPath(destinationPath);

            if (string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase))
            {
                // The script already wrote directly into exports/ under this same name -- just
                // record it, don't copy a file onto itself.
                _publishedFiles.Add(new PublishedFileRecord(displayName, destinationPath, PublishedFileRecord.StatusPublished, null));
                return;
            }

            if (File.Exists(destinationPath) && !_overwriteOutputFiles)
            {
                _publishedFiles.Add(new PublishedFileRecord(
                    displayName,
                    destinationPath,
                    PublishedFileRecord.StatusFailed,
                    $"'{destinationPath}' already exists; set overwrite_output_files=true to replace it."));
                return;
            }

            File.Copy(sourcePath, destinationPath, overwrite: _overwriteOutputFiles);
            _publishedFiles.Add(new PublishedFileRecord(displayName, destinationPath, PublishedFileRecord.StatusPublished, null));
        }
        catch (Exception ex)
        {
            var destinationPath = Path.Combine(ExportsDirectory, displayName);
            _publishedFiles.Add(new PublishedFileRecord(displayName, destinationPath, PublishedFileRecord.StatusFailed, ex.Message));
        }
    }

    private static string SafeGetFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
