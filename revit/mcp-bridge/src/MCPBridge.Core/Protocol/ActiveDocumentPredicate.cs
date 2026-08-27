namespace MCPBridge.Core.Protocol;

/// <summary>
/// Decides whether one open document is the instance's active/foreground document, for the
/// <c>active</c> flag on each <see cref="RegisteredDocument"/> in the `register` snapshot
/// (PRD §05).
///
/// <para>
/// Lives in Core, as pure logic over two already-resolved identity strings plus one boolean,
/// rather than in the AddIn project where its only caller sits. That's deliberate: the AddIn
/// project is Revit-API glue and is not unit-tested at all, and this predicate previously
/// shipped as a single inline <c>ReferenceEquals</c> that was WRONG IN EVERY CASE for the
/// entire life of the feature (see <see cref="IsActive"/>). A predicate with that history
/// belongs behind the Core/RevitAdapter seam where a regression test can pin it.
/// </para>
/// </summary>
public static class ActiveDocumentPredicate
{
    /// <summary>
    /// The original implementation was <c>ReferenceEquals(document, activeDocument)</c> alone.
    /// Revit does not promise that two API calls asking for "the same" document hand back the
    /// same managed <c>Document</c> wrapper, and live instrumentation confirmed it does not:
    /// <c>Application.Documents</c>'s enumeration and <c>ActiveUIDocument.Document</c> returned
    /// different wrappers for one open document, so the reference test was false even for a
    /// document that genuinely was active, and <c>active</c> was reported false for every
    /// document in every response the connector ever sent.
    ///
    /// <para>Two arms, and both are load-bearing:</para>
    /// <list type="bullet">
    /// <item><description><paramref name="isSameDocumentReference"/> is exact when it holds,
    /// and is the ONLY arm that can identify an unsaved document (whose id is a per-wrapper
    /// <c>tmp-</c> GUID that need not match across call sites; see PRD §09's known gap).</description></item>
    /// <item><description>The §09 identity comparison is path-derived, so it survives a fresh
    /// wrapper ; the arm that actually fixes the bug in the normal, saved-document case.</description></item>
    /// </list>
    ///
    /// <para>
    /// <c>tmp-</c> ids are excluded from the identity arm on purpose. Two DIFFERENT unsaved
    /// documents can each hold a <c>tmp-</c> id, and comparing them is meaningless ; but worse,
    /// a stale-but-equal <c>tmp-</c> id would be a FALSE POSITIVE, marking the wrong document
    /// active. Excluding them can only ever cost a false negative (an unsaved active document
    /// reported inactive when the wrapper check also misses), which is the safe direction.
    /// </para>
    /// </summary>
    /// <param name="isSameDocumentReference">Whether the two documents are the same managed object.</param>
    /// <param name="documentId">The §09 identity of the document being described.</param>
    /// <param name="activeDocumentId">The §09 identity of the active document, or null if there isn't one.</param>
    public static bool IsActive(bool isSameDocumentReference, string? documentId, string? activeDocumentId)
    {
        if (isSameDocumentReference)
        {
            return true;
        }

        if (documentId is null || activeDocumentId is null)
        {
            return false;
        }

        if (activeDocumentId.StartsWith("tmp-", StringComparison.Ordinal) ||
            documentId.StartsWith("tmp-", StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(documentId, activeDocumentId, StringComparison.Ordinal);
    }
}
