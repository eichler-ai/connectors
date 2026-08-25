namespace MCPBridge.RevitAdapter;

/// <summary>
/// What a script sees as `UIDocument` (PRD §06). Deliberately narrower than <see cref="IUiDocumentAdapter"/>:
/// its <c>Document</c> must be <see cref="IScriptDocument"/>, not the full <see cref="IDocumentAdapter"/> --
/// otherwise a script could reach CreateTransaction/CreateTransactionGroup via `UIDocument.Document...`
/// even after the same fix was applied to the top-level `Document` global. Confirmed live: found by an
/// independent PR review, not hypothetical -- RoslynScriptRunnerTests already exercises
/// `UIDocument.Document.Title` as a real script-scope binding, so `UIDocument.Document.CreateTransaction`
/// was an equally real, uncaught path to the exact bug IScriptDocument exists to prevent.
/// </summary>
public interface IScriptUiDocument
{
    IScriptDocument Document { get; }
}
