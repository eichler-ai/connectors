using System;
using System.Linq;
using MCPBridge.Core.Execution;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// A script's global names live in a namespace this project does not own. Revit can introduce a public
/// type with the same short name in any release, and an agent that writes <c>using Autodesk.Revit.DB;</c>
/// -- which skill.md explicitly tells it to do -- then has both in scope.
///
/// <para>This is a TRIPWIRE for that, and it exists because nothing else would notice. Today's one
/// collision (<c>Connector</c>) was found by hand, live, while auditing something else. Without this test
/// the next one arrives via a confused agent rather than a red build.</para>
///
/// <para>What makes the risk tolerable rather than alarming, and worth writing down because it is the
/// reason this is a tripwire and not a redesign: C# resolves these deterministically by CONTEXT, and when
/// it resolves against the author's intent it fails LOUDLY. Verified live against Revit 2027 with
/// <c>Autodesk.Revit.DB.Connector</c> imported:</para>
///
/// <code>
/// using Autodesk.Revit.DB;
/// return Connector.ExportsDirectory;   // -> the connector global
/// return typeof(Connector).FullName;   // -> "Autodesk.Revit.DB.Connector"
/// </code>
///
/// <para>A value context binds the global, a type context binds the imported type. So an agent meaning
/// Autodesk's type in an expression gets our global and then a compile error for the missing member, with
/// the CS0103-style remedy attached -- not silently wrong behaviour. Silent wrongness would need BOTH
/// names to carry a same-named member with different semantics, which is a far narrower coincidence than a
/// name clash.</para>
///
/// <para>Issue #91 is what makes this maintainable at all: it cut the global surface from eleven names to
/// five, and new connector functions now go on <c>Connector</c> rather than into global scope. So this set
/// is close to frozen, and the assertion below is cheap to keep honest.</para>
/// </summary>
public class ScriptGlobalCollisionTests
{
    /// <summary>
    /// Global names that DO match a public Revit type's short name, each one deliberate and verified.
    ///
    /// <para>Adding a name here is a decision, not a formality: it says someone checked how C# resolves
    /// the specific clash and confirmed the script-facing behaviour is still correct. Do not add one to
    /// make a red build green.</para>
    /// </summary>
    private static readonly string[] AcceptedCollisions =
    {
        // Autodesk.Revit.DB.Connector -- an MEP connector, obtained from a ConnectorManager and used as an
        // INSTANCE type. It has no static members an agent would reach through the bare name, which is the
        // only shape that would actually contend with our global. Live-verified above (issue #91, D3).
        "Connector",

        // Autodesk.Revit.DB.Document -- and this one is the whole point of the mechanism rather than a
        // hazard: the global's VALUE is an instance of the very type it shadows (PRD §14), so there is no
        // reading under which an agent gets the wrong thing. `Document.Title` binds the global, `Document
        // d = ...` and `typeof(Document)` bind the type, and both are what the author meant.
        //
        // Worth noting how it got here: it was NOT in this list when the test was written, because nobody
        // had thought of it. The test's first real run added it. That is the argument for the test.
        "Document",
    };

    /// <summary>
    /// Self-skipping, like the rest of the real-RevitAPI coverage here: meaningless on a Mac worktree with
    /// no Revit install, real signal on the Windows VM. Set MCPBRIDGE_REVITAPI_DLL to enable.
    ///
    /// <para>Note the standing hazard with that pattern, documented in caveats.md: a test that returns on a
    /// missing env var reports as PASSED, not skipped, so it can sit dead indefinitely. This one was
    /// confirmed to actually run and actually fail before being trusted -- temporarily emptying
    /// <see cref="AcceptedCollisions"/> fails it on <c>Connector</c>.</para>
    /// </summary>
    [Fact]
    public void NoScriptGlobalCollidesWithARevitTypeName_ExceptTheAcceptedOnes()
    {
        var loaded = RealRevitApiLoader.TryLoad();
        if (loaded is null)
        {
            return; // Not configured in this environment -- skip rather than fail.
        }

        using var context = loaded.Value.Context;

        // Short names only, and that is the point: a fully-qualified name never collides. What an agent
        // writes after a `using` directive is the short name, so that is what has to be compared.
        var revitTypeNames = loaded.Value.Assembly
            .GetExportedTypes()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var collisions = ScriptGlobals.GlobalNames
            .Where(revitTypeNames.Contains)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Set EQUALITY, not "contains": a collision that disappears is also news. It would mean either
        // Revit removed a type or a global was renamed, and the stale entry above should go rather than
        // sit there implying a check that no longer applies.
        Assert.Equal(AcceptedCollisions.OrderBy(n => n, StringComparer.Ordinal).ToArray(), collisions);
    }
}
