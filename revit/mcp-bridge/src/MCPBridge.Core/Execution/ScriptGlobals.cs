using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// The globals object exposed to script scope (PRD §06 step 3 / "Cancellation" -
/// cooperative path).
///
/// PHASE 3 (PRD §14, "Real Revit API access from scripts"): Document/UIApplication/
/// UIDocument are the REAL Autodesk.Revit.DB.Document / Autodesk.Revit.UI.UIApplication /
/// UIDocument, not the narrow IScriptDocument/IScriptUiApplication/IScriptUiDocument
/// interfaces they used to be (now deleted). Those interfaces existed to stop a script
/// calling IDocumentAdapter.CreateTransaction/CreateTransactionGroup -- but those are OUR
/// OWN adapter methods, not real Revit API, and the real risk they were standing in for is
/// a script constructing its own Autodesk.Revit.DB.Transaction against the same Document
/// TransactionScriptExecutor has already opened one on. That invariant is now enforced
/// where it actually belongs: <see cref="ScriptApiDenylist"/>, a compile-time semantic
/// check, not the type system. Everything else about the real API (reads, writes, element
/// queries, geometry) rides the executor's existing ambient transaction and needs no new
/// transaction-ownership scheme -- confirmed live before this shipped (PRD §14).
///
/// ISSUE #24 adds the one exception to "everything rides the ambient transaction": a document
/// the script CREATES is a different document, and Revit's one-open-transaction rule is
/// per-document, so the ambient pair does not cover it. Hence CreateProjectDocument/
/// CreateFamilyDocument below -- they create the document AND have the executor open a
/// managed transaction for it, in one step. That keeps the denylist rule above completely
/// unconditional: the script still never constructs a transaction, because it never needs to.
/// The raw Application.NewProjectDocument path is untouched and stays read-only.
///
/// WHY THIS ONE FILE REFERENCES RevitAPI/RevitAPIUI DIRECTLY, and why that is not a
/// precedent: MCPBridge.Core is otherwise entirely decoupled from Revit, working only
/// against the MCPBridge.RevitAdapter interfaces so its decision logic stays unit-testable
/// with fakes (see the revit-connector-development skill's testing strategy). ScriptGlobals
/// is the single genuine exception, because it IS the public script-facing contract: an
/// agent-authored script binds these identifiers by name and calls real Revit API on them,
/// so this type has to name the real types. MCPBridge.Core.csproj's RevitAPI/RevitAPIUI
/// references exist for this file alone -- do not reach for them from anywhere else in Core;
/// add an adapter interface instead, the way every other Revit-touching concern here does.
/// </summary>
public sealed class ScriptGlobals
{
    private readonly IDocumentAdapter _documentAdapter;
    private readonly IUiApplicationAdapter _uiApplicationAdapter;
    private readonly IUiDocumentAdapter? _uiDocumentAdapter;
    private readonly ManagedDocumentTransactions? _documentTransactions;

    // Property casing here is a public, external contract (PRD §06): an agent-authored script
    // binds to these identifiers by name in its scope, so it must match the PRD's published
    // names -- Document, UIApplication, UIDocument -- exactly.
    //
    // These are deliberately delegating PROPERTIES, resolved on access, not values captured in
    // the constructor. Two reasons, both load-bearing:
    //  (a) The constructor stays free of any Revit type, so ScriptGlobals can still be
    //      constructed against RevitAdapter fakes in MCPBridge.Core.Tests -- which is what keeps
    //      the whole non-Revit half of this class (Publish/file exchange, PRD §09, and the
    //      compile-time ScriptApiDenylist checks) unit-testable with no live Revit at all.
    //  (b) An adapter that cannot supply a real Revit object (a test fake) produces a clear,
    //      signposted error naming revit/test-harness at the point a script actually touches the
    //      global (PRD §01 observability-over-silence), rather than failing opaquely at construction
    //      before a test's own body has even run.
    //
    // The raw objects come from the IRaw*Source capability interfaces rather than from IDocumentAdapter
    // &c. directly -- see IRawDocumentSource's doc comment for why that separation is load-bearing and
    // not merely stylistic (it is what keeps MCPBridge.Core.Tests' own assembly free of a RevitAPI
    // reference, without which xUnit silently skips the entire test assembly).
    public Autodesk.Revit.DB.Document Document =>
        Raw<IRawDocumentSource>(_documentAdapter, nameof(Document)).RawDocument;

    public Autodesk.Revit.UI.UIApplication UIApplication =>
        Raw<IRawUiApplicationSource>(_uiApplicationAdapter, nameof(UIApplication)).RawUiApplication;

    public Autodesk.Revit.UI.UIDocument? UIDocument =>
        _uiDocumentAdapter is null
            ? null
            : Raw<IRawUiDocumentSource>(_uiDocumentAdapter, nameof(UIDocument)).RawUiDocument;

    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Creates a NEW, blank, WRITABLE project document (issue #24) -- the connector opens and manages a
    /// Transaction/TransactionGroup for it in the same step, so the script can modify it immediately.
    ///
    /// USE THIS RATHER THAN `UIApplication.Application.NewProjectDocument(...)` whenever the script
    /// intends to write to the document. Both still work and both return a real Document; the raw
    /// Application member is READ-ONLY from a script, because nothing opens a transaction for what it
    /// returns and a script may never open one itself (ScriptApiDenylist check 1, unconditional). This
    /// is the difference between the two paths, and it is the only difference.
    ///
    /// <paramref name="templatePath"/> defaults to the Revit install's own DefaultProjectTemplate --
    /// the PRD §13 fixture-system case, where the point is a blank document needing no template asset.
    ///
    /// The document is committed when the script returns normally and rolled back if it throws, exactly
    /// like the ambient document. It is unsaved and in-memory; it stays in Application.Documents for the
    /// rest of the session, which is how a later execute_script call addresses it (by Title -- there is
    /// no document_id for a created document, PRD §14).
    /// </summary>
    public Autodesk.Revit.DB.Document CreateProjectDocument(string? templatePath = null) =>
        Raw<IRawDocumentSource>(
            RequireDocumentTransactions(nameof(CreateProjectDocument)).CreateAndOpenProjectDocument(templatePath),
            nameof(CreateProjectDocument)).RawDocument;

    /// <summary>
    /// Family-document counterpart of <see cref="CreateProjectDocument"/> -- see that method for the
    /// writable-vs-read-only distinction against the raw Application members. Unlike a project document
    /// there is no install-wide default family template, so <paramref name="templatePath"/> is required.
    /// </summary>
    public Autodesk.Revit.DB.Document CreateFamilyDocument(string templatePath) =>
        Raw<IRawDocumentSource>(
            RequireDocumentTransactions(nameof(CreateFamilyDocument)).CreateAndOpenFamilyDocument(templatePath),
            nameof(CreateFamilyDocument)).RawDocument;

    /// <summary>
    /// Opens a managed Transaction/TransactionGroup for a document this script did NOT create this run --
    /// one found by iterating UIApplication.Application.Documents, e.g. a document a PRIOR execute_script
    /// call created via CreateProjectDocument/CreateFamilyDocument and left open. Without this, such a
    /// document is readable but not writable: it commits and closes its managed transaction the moment the
    /// call that created it returns, and a script may never open its own Transaction (ScriptApiDenylist
    /// check 1, unconditional) -- confirmed live as a real gap while building the test-harness coverage
    /// corpus, whose fixture-system bundles need exactly this (one document, built up across several
    /// separate execute_script calls).
    ///
    /// Same commit/rollback guarantee as CreateProjectDocument/CreateFamilyDocument and the ambient
    /// document: writes commit when THIS script returns normally, roll back if it throws. Returns the
    /// same Document reference passed in -- callers that already hold it don't need the return value.
    ///
    /// Throws if <paramref name="document"/> already has a managed transaction open this run (it's the
    /// ambient document, was created this run, or OpenForWriting was already called on it) -- opening a
    /// second Transaction on a document that already has one open is not a state ManagedDocumentTransactions
    /// can safely track or Revit's own API allows.
    /// </summary>
    public Autodesk.Revit.DB.Document OpenForWriting(Autodesk.Revit.DB.Document document) =>
        Raw<IRawDocumentSource>(
            RequireDocumentTransactions(nameof(OpenForWriting)).OpenExisting(document),
            nameof(OpenForWriting)).RawDocument;

    /// <summary>
    /// The managed-transaction set for this run, or a signposted failure if none was supplied. Null
    /// only when ScriptGlobals was constructed outside TransactionScriptExecutor (tests that don't
    /// exercise document creation) -- never during a real run.
    /// </summary>
    private ManagedDocumentTransactions RequireDocumentTransactions(string memberName) =>
        _documentTransactions
        ?? throw new NotSupportedException(
            $"`{memberName}` needs the executor's managed-transaction set, which this ScriptGlobals was " +
            "constructed without. Only TransactionScriptExecutor supplies one; a script always runs " +
            "through it, so this cannot happen during a real execute_script run.");

    /// <summary>
    /// Resolves an adapter's raw-Revit-object capability, or fails with a message that says exactly
    /// what is missing and where a test needing it belongs. Never returns null: a null global would
    /// surface inside an agent's script as an unexplained NullReferenceException (PRD §01).
    ///
    /// Note this guard is unreachable from MCPBridge.Core.Tests, and that is a property of the JIT
    /// rather than an oversight: it resolves every type a method body references -- including the
    /// Revit-typed return of the property calling this -- before executing any of that body, so a
    /// unit-test script touching Document fails on loading RevitAPI.dll (mixed-mode, unloadable
    /// outside Revit) rather than here. It is defensive cover for a future adapter that implements
    /// IDocumentAdapter but forgets IRawDocumentSource.
    /// </summary>
    private static TSource Raw<TSource>(object adapter, string globalName) where TSource : class =>
        adapter as TSource
        ?? throw new NotSupportedException(
            $"the `{globalName}` script global needs a real Revit object, but {adapter.GetType().Name} " +
            $"does not implement {typeof(TSource).Name}. Only the live adapters do -- " +
            "Autodesk.Revit.DB.Document, UIApplication and UIDocument are sealed and non-constructible " +
            "outside a running Revit session, so a fake genuinely cannot supply one. A test that needs " +
            "to EXECUTE a script against real Revit objects belongs in the tier-2 live harness " +
            "(revit/test-harness), not MCPBridge.Core.Tests.");

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

    /// <summary>
    /// The names a script can bind in its own scope, sorted, for reporting back to an agent that used one
    /// that doesn't exist (issue #84).
    ///
    /// <para>REFLECTED rather than listed by hand, and that is the whole point: a hand-maintained copy of
    /// this list would drift the first time a global was added, and the failure mode of a stale list here
    /// is the exact failure it exists to fix -- an agent told authoritatively that a name it needs does not
    /// exist. Reflection cannot go stale.</para>
    ///
    /// <para>Deliberately NOT a substitute for <c>get_skills</c>, which documents what each of these DOES,
    /// with worked examples. This is a bare name list for the one moment a name list is what's needed: a
    /// CS0103 "does not exist in the current context" on a guessed identifier.</para>
    /// </summary>
    public static IReadOnlyList<string> GlobalNames { get; } =
        typeof(ScriptGlobals)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.MemberType is MemberTypes.Property or MemberTypes.Method)
            // Property accessors surface as get_X/set_X methods; the property itself is already listed.
            .Where(m => m is not MethodInfo method || !method.IsSpecialName)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

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

    /// <summary>
    /// INTERNAL, though the CLASS stays public. Roslyn needs the globals TYPE public so a script can bind
    /// these members by name in its own scope; it never constructs one -- TransactionScriptExecutor passes
    /// the instance in. The constructor takes a <see cref="ManagedDocumentTransactions"/>, which is
    /// internal precisely so an agent script cannot get hold of the executor's transaction set (see that
    /// class's doc comment for the live-verified bypass this closes), and a public constructor taking an
    /// internal parameter type would not compile anyway.
    /// </summary>
    internal ScriptGlobals(
        IDocumentAdapter document,
        IUiApplicationAdapter uiApplication,
        IUiDocumentAdapter? uiDocument,
        CancellationToken cancellationToken,
        string? exportsDirectoryPath = null,
        string? importsDirectoryPath = null,
        bool overwriteOutputFiles = false,
        ManagedDocumentTransactions? documentTransactions = null)
    {
        _documentTransactions = documentTransactions;
        _documentAdapter = document;
        _uiApplicationAdapter = uiApplication;
        _uiDocumentAdapter = uiDocument;
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
                // A rooted sourcePath ending in a separator (or similar) yields no bare file name.
                // Must fail outright here, not fall back to the raw sourcePath as displayName --
                // Path.Combine(ExportsDirectory, sourcePath) returns a rooted sourcePath verbatim,
                // which can point OUTSIDE ExportsDirectory and would then satisfy the
                // already-in-exports containment check below by coincidence, recording a false
                // "published" for a file that was never copied. No valid destination name exists,
                // so there is nothing safe to attempt.
                _publishedFiles.Add(new PublishedFileRecord(sourcePath, sourcePath, PublishedFileRecord.StatusFailed, $"'{sourcePath}' has no valid file name to publish under."));
                return;
            }
        }

        try
        {
            var destinationPath = Path.Combine(ExportsDirectory, displayName);
            var normalizedSource = NormalizeFullPath(sourcePath);
            var normalizedDestination = NormalizeFullPath(destinationPath);
            var normalizedExportsDirectory = NormalizeFullPath(ExportsDirectory);

            if (string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase))
            {
                // Defense in depth: displayName is already constrained to a bare file name above,
                // so destinationPath should always resolve inside ExportsDirectory -- but never treat
                // "source equals destination" as success without confirming that containment holds.
                if (!IsWithinDirectory(normalizedDestination, normalizedExportsDirectory))
                {
                    _publishedFiles.Add(new PublishedFileRecord(displayName, destinationPath, PublishedFileRecord.StatusFailed, $"'{destinationPath}' resolves outside the exports directory."));
                    return;
                }

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

    private static bool IsWithinDirectory(string normalizedPath, string normalizedDirectory)
    {
        var directoryWithSeparator = normalizedDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedDirectory
            : normalizedDirectory + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
