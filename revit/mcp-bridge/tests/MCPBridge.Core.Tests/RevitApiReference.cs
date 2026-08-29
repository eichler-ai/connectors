using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MCPBridge.Core.Tests;

/// <summary>
/// Supplies RevitAPI.dll/RevitAPIUI.dll to Roslyn as METADATA REFERENCE PATHS, which is the only way
/// MCPBridge.Core.Tests can make Revit types bindable from script scope.
///
/// Why paths and not loaded assemblies (PRD §14): RoslynScriptRunner normally collects the script's
/// references from AppDomain.CurrentDomain.GetAssemblies(), which is exactly right in production --
/// inside a live Revit process RevitAPI/RevitAPIUI are already loaded. They cannot be loaded here:
/// RevitAPI.dll is a mixed-mode C++/CLI assembly that only Revit's own native host can load, and
/// Assembly.LoadFrom on it elsewhere throws "An attempt was made to load a program with an incorrect
/// format" (confirmed live on this dev VM). Roslyn only reads managed metadata, which works fine
/// straight from the file, so every compile-time check stays fully unit-testable with no live Revit.
///
/// The corollary, and the reason several tests here assert on compilation rather than on a return
/// value: a script that actually EXECUTES against a Revit type cannot run in this tier at all -- the
/// emitted submission would have to load the assembly. Those assertions live in the tier-2 live
/// harness (revit/test-harness).
///
/// Paths come from assembly metadata written by MCPBridge.Core.Tests.csproj, so the net10.0-windows leg
/// gets Revit 2027's and the net8.0-windows leg gets Revit 2025's, with no version hardcoded here.
/// </summary>
internal static class RevitApiReference
{
    /// <summary>
    /// RevitAPI.dll + RevitAPIUI.dll, for RoslynScriptRunner's additionalMetadataReferencePaths.
    /// </summary>
    internal static string[] Paths { get; } = new[] { Read("RevitApiPath"), Read("RevitApiUiPath") };

    /// <summary>
    /// The same two assemblies as ALREADY-BUILT metadata references, parsed exactly once per test
    /// process (test-quality pass): CreateFromFile re-reads the DLL's metadata every call, and the
    /// suites construct ~100 runners -- the repeated parse was a measurable slice of the tier-1
    /// wall clock. MetadataReference instances are immutable and safe to share across runners; no
    /// runner STATE is shared. Every test helper should pass these, not <see cref="Paths"/>.
    /// </summary>
    internal static System.Collections.Generic.IReadOnlyList<Microsoft.CodeAnalysis.MetadataReference> References { get; } =
        Paths.Select(p => (Microsoft.CodeAnalysis.MetadataReference)Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p)).ToArray();

    private static string Read(string key)
    {
        var value = typeof(RevitApiReference).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"MCPBridge.Core.Tests was built without the '{key}' assembly metadata. It is written by " +
                "MCPBridge.Core.Tests.csproj from $(RevitInstallDir); without it no test here can make " +
                "Revit types bindable from script scope.");
        }

        if (!File.Exists(value))
        {
            throw new InvalidOperationException(
                $"'{key}' points at '{value}', which does not exist. These tests need the matching Revit " +
                "version installed (net10.0-windows -> Revit 2027, net8.0-windows -> Revit 2025); only the " +
                "file's metadata is read, Revit itself never runs.");
        }

        return value;
    }
}
