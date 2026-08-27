using Autodesk.Revit.DB;

namespace MCPBridge.RevitAdapter;

/// <summary>Real implementation wrapping Autodesk.Revit.DB.Document. Not unit-tested (see RevitTransactionAdapter).</summary>
public sealed class RevitDocumentAdapter : IDocumentAdapter
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
    /// Resolved through DocumentIdentity's shared, process-lifetime cache (keyed on the live
    /// Document reference) -- NOT recomputed here on every access. See DocumentIdentity.ResolveCached's
    /// own doc comment for why every RevitDocumentAdapter wrapping the same live Document, however
    /// many times one gets freshly constructed, must agree on the same id.
    /// </summary>
    public string DocumentId => DocumentIdentity.ResolveCached(_document, _uncPathResolver);

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
