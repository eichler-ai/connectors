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
    /// The Revit version this test assembly's TFM targets, matching the RevitVersion property every
    /// .csproj in this solution sets. Kept as a compile-time constant so the two cannot disagree at
    /// runtime; if a TFM is added to the project, this fails to compile until it is handled here.
    /// </summary>
    internal const string RevitVersionForThisTargetFramework =
        // NET8_0, not NET8_0_WINDOWS. The SDK's implicit defines for a net8.0-windows TFM are NET8_0 (plus
        // NET8_0_OR_GREATER, NETCOREAPP, ...) and, separately, the platform symbols WINDOWS / WINDOWS7_0.
        // There is no combined NET8_0_WINDOWS, so the first version of this #if was never true and BOTH
        // legs compiled to "2027" -- silently reintroducing the exact defect the constant replaced. Caught
        // only by RealRevitApiLoaderTests asserting the running runtime against this value; every other
        // test passed either way, because they pass against both corpora.
#if NET8_0
        "2025";
#else
        "2027";
#endif

    /// <summary>
    /// Returns the real RevitAPI.dll reflected for metadata only, or null when this machine has no Revit
    /// install for the version this TFM targets -- callers skip rather than fail, matching the existing
    /// convention for these optional tests. An explicitly-set MCPBRIDGE_REVITAPI_DLL pointing at a missing
    /// file THROWS instead, since that is a misconfiguration rather than an absent install.
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
            // The version is derived from the TFM, NOT probed in a fixed order, and that distinction is
            // the whole point (review finding). Every .csproj in this solution maps net10.0-windows -> 2027
            // and net8.0-windows -> 2025, and this project multi-targets precisely because the discovery
            // reflection path is version-sensitive. A first-hit-wins probe defeats that: on the dev VM,
            // where both versions are installed, BOTH legs would load 2027 and the net8.0 leg would report
            // a result about a corpus it never touched. That is worse than not running -- the 2025 leg
            // would look like coverage while duplicating the 2027 leg.
            //
            // This mirrors $(RevitInstallDir) rather than inventing a third source of truth for where
            // Revit lives. The env var stays as an override for a non-standard install.
            dllPath = $@"C:\Program Files\Autodesk\Revit {RevitVersionForThisTargetFramework}\RevitAPI.dll";
            if (!File.Exists(dllPath))
            {
                return null; // This machine has no Revit for the version this TFM targets.
            }
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
