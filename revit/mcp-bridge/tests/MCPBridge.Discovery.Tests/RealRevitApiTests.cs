using System;
using System.IO;
using System.Reflection;
using MCPBridge.Core.Discovery;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Optional, self-skipping coverage against the real RevitAPI.dll/xml -- meaningless on this Mac dev
/// worktree (no Revit install), but free extra confidence once this suite runs on the Windows VM where the
/// real DLL exists. Set MCPBRIDGE_REVITAPI_DLL to the full path of a RevitAPI.dll (with RevitAPI.xml sitting
/// next to it, the normal Revit install layout) to enable; unset (the default everywhere else) skips at
/// runtime rather than failing.
/// </summary>
public class RealRevitApiTests
{
    [Fact]
    public void ListFunctions_AgainstRealRevitApiDll_ScopedByDocumentNamespace_ReturnsMembers()
    {
        var dllPath = Environment.GetEnvironmentVariable("MCPBRIDGE_REVITAPI_DLL");
        if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
        {
            return; // Not configured in this environment -- skip rather than fail.
        }

        var assembly = Assembly.LoadFrom(dllPath);
        var service = new DiscoveryService(new DiscoveryOptions { Assemblies = new[] { assembly } });

        var result = service.ListFunctions(namespaceFilter: null, typeFilter: "Autodesk.Revit.DB.Document", cursor: null, pageSize: 500);

        Assert.NotEmpty(result.Members);
        Assert.Contains(result.Members, m => m.Name == "Delete");
    }
}
