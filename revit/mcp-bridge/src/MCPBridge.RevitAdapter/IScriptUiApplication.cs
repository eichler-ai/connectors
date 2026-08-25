namespace MCPBridge.RevitAdapter;

/// <summary>
/// What a script sees as `UIApplication` (PRD §06). Deliberately narrower than
/// <see cref="IUiApplicationAdapter"/>: its <c>ActiveUiDocument</c> must be <see cref="IScriptUiDocument"/>,
/// not the full <see cref="IUiDocumentAdapter"/> -- otherwise a script could reach CreateTransaction via
/// `UIApplication.ActiveUiDocument.Document...`, a third path to the exact bug IScriptDocument exists to
/// prevent (the first two being the top-level `Document` global and `UIDocument.Document`). Found by a
/// second independent PR review: the first split (IScriptDocument/IScriptUiDocument) missed this path
/// entirely -- confirmed live-reachable, not hypothetical, since ScriptGlobals.UIApplication was still
/// typed IUiApplicationAdapter at the time.
/// </summary>
public interface IScriptUiApplication
{
    /// <summary>The document active in the foreground when the script began running, if any.</summary>
    IScriptUiDocument? ActiveUiDocument { get; }
}
