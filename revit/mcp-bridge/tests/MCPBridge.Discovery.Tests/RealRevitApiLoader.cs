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
        var fromEnvironment = !string.IsNullOrEmpty(dllPath);
        if (!fromEnvironment)
        {
            // Fall back to the standard install locations before giving up. The env var was the ONLY route
            // until this was found, and the effect was that every test in this family was silently dead in
            // the one environment that can actually run them: `prlctl exec` runs as NT AUTHORITY\\SYSTEM
            // (dev-environment.md), which does not see the interactive user's variables, and that is how
            // every agent session invokes dotnet test. They reported PASSED throughout -- caveats.md's
            // "return-on-missing-config reports as passed, not skipped" trap, caught here only because a
            // mutation of a NEW test in this family passed when it should have failed.
            //
            // These paths mirror $(RevitInstallDir) in every .csproj here, so they are already the
            // project's assumption about where Revit lives rather than a new one. The env var stays as an
            // override for a non-standard install.
            dllPath = new[] { "2027", "2025" }
                .Select(version => $@"C:\Program Files\Autodesk\Revit {version}\RevitAPI.dll")
                .FirstOrDefault(File.Exists);
        }

        if (string.IsNullOrEmpty(dllPath))
        {
            return null; // No Revit install and no override -- genuinely nothing to load (e.g. a Mac worktree).
        }

        // Setting the variable is an explicit opt-in, so a bad path is a misconfiguration, not a reason to
        // skip. Returning null here would report a typo as a PASS -- the exact failure mode that let this
        // whole file sit dead since it was written.
        if (fromEnvironment && !File.Exists(dllPath))
        {
            throw new FileNotFoundException(
                $"MCPBRIDGE_REVITAPI_DLL is set to '{dllPath}', which does not exist. Unset it to skip these tests.",
                dllPath);
        }

        // The resolver needs every assembly the target references, which means Revit's own folder (RevitAPI
        // references its siblings) plus the running .NET runtime's reference assemblies for the BCL.
        // BCL paths go FIRST: Revit ships its own copies of some System.*.dll, and with Revit's folder
        // ahead of the runtime's those would shadow the real BCL for every colliding simple name, quietly
        // resolving metadata against Revit's older framework instead of the one the add-in runs on
        // (independent PR review finding).
        var paths = new List<string>();
        paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"));
        paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(dllPath)!, "*.dll"));

        // A duplicate simple name makes PathAssemblyResolver throw; first path wins, i.e. the BCL copy.
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
