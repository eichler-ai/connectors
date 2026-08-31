using System.Linq;
using MCPBridge.Core.Execution;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>
/// Issue #84: an agent had no path from a script that named a global wrongly to the list of globals that
/// actually exist. These cover the C# half of the fix; the Go half (execute_script's tool description) is
/// covered in <c>revit/mcp-server/internal/mcpserver/tools_test.go</c>.
/// </summary>
public class ScriptGlobalsDiscoverabilityTests
{
    /// <summary>
    /// A TRIPWIRE, not a behaviour test, and the assertion is a literal list on purpose.
    ///
    /// <para>Three places name these globals for an agent: this reflected list (used in the compile-error
    /// remedy), <c>execute_script</c>'s tool description in <c>tools.go</c>, and <c>skill.md</c>'s "In
    /// scope" table. Only the first cannot go stale, because it reflects. The other two are prose in a
    /// different language and a different binary, and nothing in the compiler relates them to this type.
    /// So the moment someone adds or removes a global, this test fails and says what else to update.</para>
    ///
    /// <para>Issue #91 cut this from eleven names to five, and the four it did NOT cut are the point: the
    /// Revit entry points stay bare globals because they are Autodesk's objects, not the connector's
    /// functions. The seven connector members moved behind <c>Connector</c> and are documented by XML doc
    /// comment beside the facade, which is why the three places above no longer enumerate them at all.
    /// Adding a bare global here now means claiming it is Revit's, not ours -- if it is ours, it belongs
    /// on Connector, and <c>ConnectorApiSurfaceTests</c> is the test that will notice.</para>
    ///
    /// <para>Writing the expected set out by hand is exactly right here: a test that recomputed it by
    /// reflection would pass for any list at all, which is the one thing this must not do.</para>
    /// </summary>
    [Fact]
    public void GlobalNames_AreTheDocumentedSet_OrThreePlacesNeedUpdating()
    {
        var expected = new[]
        {
            "CancellationToken",
            "Connector",
            "Document",
            "UIApplication",
            "UIDocument",
        };

        Assert.Equal(
            expected,
            ScriptGlobals.GlobalNames.ToArray());

        // The failure message a future reader needs is in this comment, since Assert.Equal's own output
        // only shows the diff: if this failed, you changed ScriptGlobals' public surface. Update
        //   1. revit/mcp-server/internal/mcpserver/tools.go   -- execute_script's Description
        //   2. revit/mcp-server/internal/mcpserver/skill.md   -- the "In scope" table
        //   3. the expected list above
        // Adding a global that an agent cannot discover is the defect issue #84 was filed for.
    }

    /// <summary>
    /// The reflected list must not leak members that are not script-bindable. <c>object</c>'s own methods
    /// are in scope for any C# expression regardless and are not connector globals; listing them would
    /// make the remedy's "a script's scope carries exactly these" claim false.
    /// </summary>
    [Fact]
    public void GlobalNames_ExcludeInheritedObjectMembersAndPropertyAccessors()
    {
        Assert.DoesNotContain("ToString", ScriptGlobals.GlobalNames);
        Assert.DoesNotContain("GetHashCode", ScriptGlobals.GlobalNames);
        Assert.DoesNotContain("Equals", ScriptGlobals.GlobalNames);
        Assert.DoesNotContain("GetType", ScriptGlobals.GlobalNames);

        // Properties surface a get_X/set_X MethodInfo alongside the PropertyInfo; only the property itself
        // is a name a script can bind.
        Assert.DoesNotContain(ScriptGlobals.GlobalNames, n => n.StartsWith("get_", System.StringComparison.Ordinal));
        Assert.DoesNotContain(ScriptGlobals.GlobalNames, n => n.StartsWith("set_", System.StringComparison.Ordinal));
    }
}
