namespace MCPBridge.RevitAdapter;

/// <summary>
/// Optional capability interfaces exposing the REAL Revit objects an adapter wraps -- the sanctioned
/// Phase 3 seam (PRD §14) that ScriptGlobals.Document/UIApplication/UIDocument are built on, replacing
/// the unsupported reflection-into-a-private-field workaround `skill.md` used to document.
///
/// WHY THESE ARE SEPARATE INTERFACES rather than members on IDocumentAdapter/IUiApplicationAdapter/
/// IUiDocumentAdapter, which is where they would more naturally live:
///
/// Putting a Revit-typed member on IDocumentAdapter forces every implementer to name a Revit type --
/// including MCPBridge.Core.Tests' fakes. That gives MCPBridge.Core.Tests.dll its own direct assembly
/// reference to RevitAPI, and RevitAPI.dll is a mixed-mode C++/CLI assembly that ONLY Revit's own
/// native host can load (Assembly.LoadFrom elsewhere throws "An attempt was made to load a program with
/// an incorrect format"). Measured consequence, live on this dev VM: xunit.runner.visualstudio cannot
/// resolve that reference, SKIPS THE ENTIRE TEST ASSEMBLY, and `dotnet test` still exits 0 -- 300+ tests
/// silently stopped running while the build looked perfectly green. An indirect reference is fine (this
/// assembly has referenced RevitAPI all along, and MCPBridge.Core does now too); it is specifically the
/// TEST assembly's own reference table that must stay clean.
///
/// Keeping the raw accessors on separate interfaces that only the real adapters implement is what makes
/// that possible: a fake implements plain IDocumentAdapter, names no Revit type, and the whole tier-1
/// suite keeps running. It also states something true -- a fake genuinely cannot supply these, since
/// Document/UIApplication/UIDocument are sealed and non-constructible outside a live Revit session --
/// rather than pretending it could and throwing.
///
/// ScriptGlobals type-tests for these and throws a clear, signposted error when an adapter does not
/// implement one, never returning null (PRD §01: a null here would surface inside an agent's script as
/// an unexplained NullReferenceException).
/// </summary>
public interface IRawDocumentSource
{
    /// <summary>The real Autodesk.Revit.DB.Document behind this adapter.</summary>
    Autodesk.Revit.DB.Document RawDocument { get; }
}

/// <summary>See <see cref="IRawDocumentSource"/> for why this is a separate interface.</summary>
public interface IRawUiApplicationSource
{
    /// <summary>The real Autodesk.Revit.UI.UIApplication behind this adapter.</summary>
    Autodesk.Revit.UI.UIApplication RawUiApplication { get; }
}

/// <summary>See <see cref="IRawDocumentSource"/> for why this is a separate interface.</summary>
public interface IRawUiDocumentSource
{
    /// <summary>The real Autodesk.Revit.UI.UIDocument behind this adapter.</summary>
    Autodesk.Revit.UI.UIDocument RawUiDocument { get; }
}
