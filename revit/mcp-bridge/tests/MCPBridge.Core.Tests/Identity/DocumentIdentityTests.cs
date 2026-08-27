using System;
using MCPBridge.Core.Tests.Fakes;
using MCPBridge.RevitAdapter;
using Xunit;

namespace MCPBridge.Core.Tests.Identity;

public class DocumentIdentityTests
{
    private static readonly FakeUncPathResolver PassthroughUnc = new();

    [Fact]
    public void Workshared_HashesCentralPath_NotLocalPath()
    {
        var document = new FakeDocumentAdapter
        {
            IsWorkshared = true,
            CentralModelPath = @"\\server\share\Central.rvt",
            PathName = @"C:\Users\alice\Local\Central_alice.rvt",
        };

        var idFromCentral = DocumentIdentity.Resolve(document, PassthroughUnc);

        var sameCentralDifferentLocal = new FakeDocumentAdapter
        {
            IsWorkshared = true,
            CentralModelPath = @"\\server\share\Central.rvt",
            PathName = @"C:\Users\bob\Local\Central_bob.rvt",
        };
        var idFromSameCentral = DocumentIdentity.Resolve(sameCentralDifferentLocal, PassthroughUnc);

        Assert.StartsWith("doc-", idFromCentral);
        Assert.Equal(idFromCentral, idFromSameCentral); // same central path -> same id regardless of local copy path

        var differentCentral = new FakeDocumentAdapter
        {
            IsWorkshared = true,
            CentralModelPath = @"\\server\share\Other.rvt",
            PathName = @"C:\Users\alice\Local\Central_alice.rvt",
        };
        Assert.NotEqual(idFromCentral, DocumentIdentity.Resolve(differentCentral, PassthroughUnc));
    }

    [Fact]
    public void Workshared_MappedDriveLetterCentralPath_HashesSameAsItsUncEquivalent()
    {
        // Independent PR review finding: the workshared branch used to skip UNC resolution
        // entirely, so the same central model reached via a mapped drive letter and via its UNC
        // form would hash differently -- exactly the "two instances, one central model, different
        // drive letters" scenario PRD §09 calls out as the case that matters most.
        var unc = new FakeUncPathResolver();
        unc.Map("Z:", @"\\server\connectors");

        var viaDriveLetter = new FakeDocumentAdapter { IsWorkshared = true, CentralModelPath = @"Z:\Central.rvt" };
        var viaUnc = new FakeDocumentAdapter { IsWorkshared = true, CentralModelPath = @"\\server\connectors\Central.rvt" };

        var idViaDriveLetter = DocumentIdentity.Resolve(viaDriveLetter, unc);
        var idViaUnc = DocumentIdentity.Resolve(viaUnc, unc);

        Assert.Equal(idViaUnc, idViaDriveLetter);
    }

    [Fact]
    public void Workshared_WithNoResolvableCentralPath_MintsTmp()
    {
        var document = new FakeDocumentAdapter { IsWorkshared = true, CentralModelPath = null };

        var id = DocumentIdentity.Resolve(document, PassthroughUnc);

        Assert.StartsWith("tmp-", id);
    }

    [Fact]
    public void SavedNonWorkshared_IsCaseInsensitiveStable()
    {
        var lower = new FakeDocumentAdapter { PathName = @"c:\models\house.rvt" };
        var upper = new FakeDocumentAdapter { PathName = @"C:\MODELS\HOUSE.RVT" };

        var lowerId = DocumentIdentity.Resolve(lower, PassthroughUnc);
        var upperId = DocumentIdentity.Resolve(upper, PassthroughUnc);

        Assert.StartsWith("doc-", lowerId);
        Assert.Equal(lowerId, upperId);
    }

    [Fact]
    public void SavedNonWorkshared_MappedDriveLetter_HashesSameAsItsUncEquivalent()
    {
        var unc = new FakeUncPathResolver();
        unc.Map("Z:", @"\\server\connectors");

        var viaDriveLetter = new FakeDocumentAdapter { PathName = @"Z:\House.rvt" };
        var viaUnc = new FakeDocumentAdapter { PathName = @"\\server\connectors\House.rvt" };

        var idViaDriveLetter = DocumentIdentity.Resolve(viaDriveLetter, unc);
        var idViaUnc = DocumentIdentity.Resolve(viaUnc, unc); // UNC passthrough (no mapping entry for "\\")

        Assert.Equal(idViaUnc, idViaDriveLetter);
    }

    [Fact]
    public void SavedNonWorkshared_DifferentPaths_HashDifferently()
    {
        var a = new FakeDocumentAdapter { PathName = @"C:\models\house.rvt" };
        var b = new FakeDocumentAdapter { PathName = @"C:\models\garage.rvt" };

        Assert.NotEqual(DocumentIdentity.Resolve(a, PassthroughUnc), DocumentIdentity.Resolve(b, PassthroughUnc));
    }

    [Fact]
    public void Unsaved_MintsTmpPrefix_WithValidGuidSuffix()
    {
        var document = new FakeDocumentAdapter();

        var id = DocumentIdentity.Resolve(document, PassthroughUnc);

        Assert.StartsWith("tmp-", id);
        Assert.True(Guid.TryParse(id["tmp-".Length..], out _));
    }

    [Fact]
    public void Unsaved_EachCallMintsAFreshGuid()
    {
        var document = new FakeDocumentAdapter();

        var first = DocumentIdentity.Resolve(document, PassthroughUnc);
        var second = DocumentIdentity.Resolve(document, PassthroughUnc);

        Assert.NotEqual(first, second);
    }
}
