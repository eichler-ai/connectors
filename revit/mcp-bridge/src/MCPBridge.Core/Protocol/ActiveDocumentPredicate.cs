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
    /// <item><description><paramref name="isSameDocumentReference"/> is exact when it holds --
    /// kept as the fast path, and the only thing that can match a document whose identity
    /// resolution degraded to the per-resolution GUID fallback (a Title accessor throwing
    /// mid-transition).</description></item>
    /// <item><description>The §09 identity comparison survives a fresh wrapper for EVERY open
    /// document now: path-derived for saved documents, and title-derived (per-process salt) for
    /// unsaved ones since the v1 remediation series made tmp- ids wrapper-independent.</description></item>
    /// </list>
    ///
    /// <para>
    /// <c>tmp-</c> ids are deliberately INCLUDED in the identity arm. They were excluded while
    /// they were per-wrapper GUIDs (equality was meaningless and a stale match would have been a
    /// false positive) -- but the moment the ids became stable and title-derived, the exclusion
    /// itself resurrected the original bug for exactly the unsaved-document class: the reference
    /// arm is measured-false across wrappers, so an unsaved ACTIVE document reported
    /// <c>active: false</c> in every snapshot (PR #50 review finding). Two different unsaved
    /// documents can't collide (Revit auto-uniquifies open titles), and the GUID-fallback case
    /// can't false-positive (fresh GUIDs never compare equal), so plain equality is both correct
    /// and safe for every id form.
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

        return string.Equals(documentId, activeDocumentId, StringComparison.Ordinal);
    }
}
