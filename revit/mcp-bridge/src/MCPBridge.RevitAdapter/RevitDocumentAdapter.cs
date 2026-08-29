using Autodesk.Revit.DB;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Real implementation wrapping Autodesk.Revit.DB.Document. Not unit-tested (see RevitTransactionAdapter).
///
/// INTERNAL, DELIBERATELY, AND THIS IS A SECURITY BOUNDARY -- see IDocumentAdapter's own comment for the
/// live evidence. RoslynScriptRunner.LoadableReferences() references every assembly loaded in the Revit
/// AppDomain, which includes this one, so while this type was public an agent script could name and
/// construct it directly and call CreateTransaction/CreateTransactionGroup below -- opening a real,
/// unmanaged Revit transaction that ScriptApiDenylist never sees, because the `new Transaction(...)`
/// happens HERE, in our code, not in the script's own syntax tree the denylist walks. Every consumer of
/// this class lives in this same assembly and reaches it through IDocumentAdapter, so `internal` costs
/// nothing and closes the hole structurally: a script cannot name a type it cannot see.
/// </summary>
internal sealed class RevitDocumentAdapter : IDocumentAdapter, IRawDocumentSource
{
    private readonly Document _document;
    private readonly IUncPathResolver _uncPathResolver;

    public RevitDocumentAdapter(Document document, IUncPathResolver? uncPathResolver = null)
    {
        _document = document;
        _uncPathResolver = uncPathResolver ?? new Win32UncPathResolver();
    }

    public string Title => _document.Title;

    /// <summary>
    /// The real Document this adapter wraps (PRD §14) -- the sanctioned seam ScriptGlobals.Document is
    /// built on. Returns the same reference the adapter was constructed with; no copy, no wrapper.
    /// This is what makes skill.md's old `GetField("_document", ...)` reflection workaround obsolete.
    /// </summary>
    public Document RawDocument => _document;

    /// <summary>
    /// Resolved through DocumentIdentity's shared, process-lifetime cache (keyed on the live
    /// Document reference) -- NOT recomputed here on every access. See DocumentIdentity.ResolveCached's
    /// own doc comment for why every RevitDocumentAdapter wrapping the same live Document, however
    /// many times one gets freshly constructed, must agree on the same id.
    /// </summary>
    public string DocumentId => DocumentIdentity.ResolveCached(_document, _uncPathResolver);

    /// <summary>See IDocumentAdapter's own doc comment for why this exists alongside DocumentId.</summary>
    public bool ReferencesSameUnderlyingDocumentAs(IDocumentAdapter other) =>
        other is RevitDocumentAdapter otherReal && ReferenceEquals(otherReal._document, _document);

    public ITransactionAdapter CreateTransaction(string name) =>
        new RevitTransactionAdapter(new Transaction(_document, name));

    public ITransactionGroupAdapter CreateTransactionGroup(string name) =>
        new RevitTransactionGroupAdapter(new TransactionGroup(_document, name));

    /// <summary>
    /// Best-effort, same posture as DocumentSnapshotHandler.BuildEntry: a document mid-transition
    /// (e.g. detaching from central) can throw from Document.PathName -- treat that as unsaved
    /// rather than letting one odd document break document-identity resolution.
    /// </summary>
    public string? PathName
    {
        get
        {
            try
            {
                var path = _document.PathName;
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch
            {
                return null;
            }
        }
    }

    public bool IsWorkshared
    {
        get
        {
            try
            {
                return _document.IsWorkshared;
            }
            catch
            {
                return false;
            }
        }
    }

    public string? CentralModelPath
    {
        get
        {
            try
            {
                if (!_document.IsWorkshared)
                {
                    return null;
                }

                var centralPath = _document.GetWorksharingCentralModelPath();
                return centralPath is null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(centralPath);
            }
            catch
            {
                return null;
            }
        }
    }
}
