using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Loads the real RevitAPI.dll for METADATA reflection only, so discovery can be tested against the actual
/// Revit API corpus rather than only against this project's hand-written fixtures.
///
/// <para>Why this exists rather than a plain <c>Assembly.LoadFrom</c>: RevitAPI.dll is a native x64
/// mixed-mode C++/CLI assembly, and this dev VM is Windows on ARM64 with only an ARM64 .NET runtime
/// installed. <c>LoadFrom</c> therefore fails with "The assembly architecture is not compatible with the
/// current process architecture" -- it cannot be made to work in this test host at any TFM. That is not a
/// new limitation; it is why <see cref="RealRevitApiTests"/>' original test could only ever pass by
/// silently returning when MCPBRIDGE_REVITAPI_DLL was unset, which is the default everywhere.</para>
///
/// <para><see cref="MetadataLoadContext"/> sidesteps it entirely: discovery reflects over names,
/// signatures and XML docs and never EXECUTES a line of Revit code, so metadata is all it ever needed.
/// Architecture stops being relevant.</para>
/// </summary>
internal static class RealRevitApiLoader
{
    /// <summary>
    /// Returns the real RevitAPI.dll reflected for metadata only, or null when this environment has no
    /// Revit install configured (MCPBRIDGE_REVITAPI_DLL unset or pointing at a missing file) -- callers
    /// skip rather than fail, matching the existing convention for these optional tests.
    ///
    /// <para>The returned <see cref="MetadataLoadContext"/> owns the assembly and must outlive every use of
    /// it, so it is handed back for the caller to dispose.</para>
    /// </summary>
    public static (MetadataLoadContext Context, Assembly Assembly)? TryLoad()
    {
        var dllPath = Environment.GetEnvironmentVariable("MCPBRIDGE_REVITAPI_DLL");
        if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
        {
            return null;
        }

        // The resolver needs every assembly the target references, which means Revit's own folder (RevitAPI
        // references its siblings) plus the running .NET runtime's reference assemblies for the BCL.
        var paths = new List<string>();
        var revitDir = Path.GetDirectoryName(dllPath)!;
        paths.AddRange(Directory.GetFiles(revitDir, "*.dll"));
        paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"));

        // A duplicate simple name makes PathAssemblyResolver throw; Revit's folder and the BCL can both
        // ship one (System.*.dll in particular). First path wins, which is the Revit-local copy.
        var deduped = paths
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        var context = new MetadataLoadContext(new PathAssemblyResolver(deduped));
        try
        {
            return (context, context.LoadFromAssemblyPath(dllPath));
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }
}
