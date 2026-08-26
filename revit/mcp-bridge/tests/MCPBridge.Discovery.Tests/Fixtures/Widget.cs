using System;
using System.Collections.Generic;

namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>
/// A small hand-written reflection fixture (PRD §08's discovery feature is fully unit-testable against a
/// portable, self-contained target -- no real, proprietary RevitAPI.dll/xml required). This project's own
/// csproj sets GenerateDocumentationFile=true, so the compiler emits a real XML-doc sidecar
/// (MCPBridge.Discovery.Tests.xml) next to this test assembly's own DLL from these triple-slash comments --
/// DiscoveryService loads it via the exact same Path.ChangeExtension(assembly.Location, ".xml") convention
/// it uses for RevitAPI.xml in production, so these tests exercise the real join path end to end, not a
/// stand-in.
/// </summary>
public class Widget
{
    /// <summary>Creates a Widget with a default id.</summary>
    public Widget()
    {
    }

    /// <summary>Creates a Widget with a specific id.</summary>
    /// <param name="id">The widget's identifier.</param>
    public Widget(int id)
    {
        Id = id;
    }

    /// <summary>The widget's identifier.</summary>
    public int Id { get; set; }

    /// <summary>The widget's display name.</summary>
    public string Name = "";

    /// <summary>Raised when the widget's state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Describes this widget.</summary>
    /// <returns>A short description.</returns>
    public string Describe() => Name;

    /// <summary>Describes this widget at a given detail level.</summary>
    /// <param name="detailLevel">How much detail to include.</param>
    /// <returns>A description at the requested detail level.</returns>
    public string Describe(int detailLevel) => Name;

    /// <summary>Gets this widget's tags.</summary>
    /// <returns>The tag collection.</returns>
    public ICollection<int> GetTags() => new List<int>();

    /// <summary>Adds tags to this widget.</summary>
    /// <param name="tags">The tags to add.</param>
    public void AddTags(ICollection<int> tags)
    {
    }

    /// <summary>Creates a new default Widget. Used to exercise search_functions' exact-name-match ranking, and to give ListFunctions a static member alongside the instance ones above.</summary>
    public static Widget Create() => new();

    /// <summary>
    /// A deliberately long summary, well over the 300-character truncation threshold PRD §08's response-size
    /// section requires list_functions/search_functions to enforce -- padding padding padding padding
    /// padding padding padding padding padding padding padding padding padding padding padding padding
    /// padding padding padding padding padding padding padding padding padding padding padding padding end.
    /// </summary>
    public void LongSummaryMethod()
    {
    }

    // Deliberately not public -- must never appear in discovery results.
    internal void Hidden()
    {
    }
}
