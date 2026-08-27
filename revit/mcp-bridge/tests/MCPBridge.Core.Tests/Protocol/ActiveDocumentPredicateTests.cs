using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Protocol;

/// <summary>
/// Regression tests for the `active` flag on `register`'s document list (PRD §05).
///
/// The predicate this covers was, for the entire life of the feature, a bare
/// ReferenceEquals between a document from Application.Documents and
/// ActiveUIDocument.Document. Revit hands back different managed wrappers for the same
/// document across those two call sites (confirmed by live instrumentation:
/// refEquals=False with identical §09 ids), so the flag was false for EVERY document in
/// EVERY response the connector ever sent. These tests exist so that can't come back.
/// </summary>
public class ActiveDocumentPredicateTests
{
    [Fact]
    public void SameWrapperReference_IsActive()
    {
        Assert.True(ActiveDocumentPredicate.IsActive(true, "doc-A", "doc-A"));
    }

    [Fact]
    public void SameWrapperReference_IsActive_EvenForUnsavedDocumentsWithMismatchedTmpIds()
    {
        // The reference arm is the ONLY thing that can identify an unsaved document, precisely
        // because its tmp- id is per-wrapper and need not match across call sites (PRD §09 gap).
        Assert.True(ActiveDocumentPredicate.IsActive(true, "tmp-aaa", "tmp-bbb"));
    }

    [Fact]
    public void DifferentWrappers_SameIdentity_IsActive()
    {
        // THE bug. Revit really does hand back different wrappers for one document; before the
        // fix this returned false and `active` was never true for anything.
        Assert.True(ActiveDocumentPredicate.IsActive(false, "doc-B2C26C25F6039853", "doc-B2C26C25F6039853"));
    }

    [Fact]
    public void DifferentWrappers_DifferentIdentity_IsNotActive()
    {
        Assert.False(ActiveDocumentPredicate.IsActive(false, "doc-A", "doc-B"));
    }

    [Fact]
    public void NoActiveDocument_IsNotActive()
    {
        Assert.False(ActiveDocumentPredicate.IsActive(false, "doc-A", null));
    }

    [Theory]
    // Two DIFFERENT unsaved documents, or one whose tmp- id was minted twice: comparing tmp-
    // ids is meaningless, and a stale-but-equal one would mark the WRONG document active.
    // Excluding them can only cost a false negative, which is the safe direction to fail in.
    [InlineData("tmp-same", "tmp-same")]
    [InlineData("tmp-aaa", "tmp-bbb")]
    [InlineData("doc-A", "tmp-aaa")]
    [InlineData("tmp-aaa", "doc-A")]
    public void TmpIdsAreNeverMatchedByIdentityAlone(string documentId, string activeDocumentId)
    {
        Assert.False(ActiveDocumentPredicate.IsActive(false, documentId, activeDocumentId));
    }

    [Fact]
    public void IdentityComparisonIsOrdinal_NotCaseInsensitive()
    {
        // §09 ids are hex hashes with a fixed prefix; a case difference is a different id, not
        // the same one spelled differently.
        Assert.False(ActiveDocumentPredicate.IsActive(false, "doc-abc", "doc-ABC"));
    }

    [Fact]
    public void NullDocumentId_IsNotActive()
    {
        Assert.False(ActiveDocumentPredicate.IsActive(false, null, "doc-A"));
    }
}
