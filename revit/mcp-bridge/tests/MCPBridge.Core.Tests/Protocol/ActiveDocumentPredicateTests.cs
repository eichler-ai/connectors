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
    public void SameWrapperReference_IsActive_EvenWhenIdsDisagree()
    {
        // The reference arm is exact when it holds and covers the one id form that can still
        // disagree across wrappers: the per-resolution GUID fallback for a document whose Title
        // accessor threw mid-transition.
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

    [Fact]
    public void DifferentWrappers_SameTmpIdentity_IsActive()
    {
        // PR #50 review finding (F1). tmp- ids are title-derived and wrapper-independent now, so
        // an unsaved active document's two wrappers resolve to the SAME tmp- id -- and the old
        // exclusion of tmp- ids from the identity arm resurrected the original always-false bug
        // for exactly the unsaved-document class (the reference arm is measured-false across
        // wrappers). Equality must count for every id form.
        Assert.True(ActiveDocumentPredicate.IsActive(false, "tmp-1234567890ABCDEF", "tmp-1234567890ABCDEF"));
    }

    [Theory]
    // Different documents -- different ids, whatever the prefixes: never active. (Two different
    // unsaved documents can't share an id: Revit auto-uniquifies open titles, and the GUID
    // fallback mints fresh values that never compare equal.)
    [InlineData("tmp-aaa", "tmp-bbb")]
    [InlineData("doc-A", "tmp-aaa")]
    [InlineData("tmp-aaa", "doc-A")]
    public void DifferentIds_AreNotActive_WhateverThePrefix(string documentId, string activeDocumentId)
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
