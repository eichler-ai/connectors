using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Partial mitigation for the "version collisions with other add-ins" concern in
/// PRD §06's "Roslyn isolation &amp; memory lifecycle": installs an
/// AssemblyLoadContext.Default.Resolving handler that, for any Microsoft.CodeAnalysis*
/// request the default context's normal probing can't already satisfy, loads it from
/// MCPBridge's own directory into a dedicated, non-collectible AssemblyLoadContext
/// rather than falling through to whatever another add-in already loaded.
///
/// KNOWN LIMITATION (see the phase-01 implementation report): because MCPBridge.Core
/// itself references Microsoft.CodeAnalysis.CSharp.Scripting as an ordinary
/// PackageReference, the default context's own AssemblyDependencyResolver already
/// satisfies that request from Core's own deps.json before Resolving ever fires --
/// so this hook only protects the case where the *other* add-in's bundled version
/// collides in a way default resolution can't already handle (e.g. a strict version
/// match failure), not full isolation of our own statically-linked copy. Full
/// isolation would need a shadow-load bootstrap (loading MCPBridge.Core.dll itself
/// into a custom ALC from a Roslyn-free entry point) -- a larger change flagged as a
/// follow-up rather than attempted here.
/// </summary>
public static class RoslynAssemblyIsolation
{
    private static int _initialized;
    private static AssemblyLoadContext? _roslynContext;

    public static bool IsInitialized => Volatile.Read(ref _initialized) == 1;

    public static void EnsureInitialized()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
        {
            return;
        }

        _roslynContext = new AssemblyLoadContext("MCPBridge.RoslynIsolation", isCollectible: false);
        var baseDirectory = Path.GetDirectoryName(typeof(RoslynAssemblyIsolation).Assembly.Location) ?? AppContext.BaseDirectory;

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            if (name.Name is null || !IsRoslynAssembly(name.Name))
            {
                return null;
            }

            var candidatePath = Path.Combine(baseDirectory, name.Name + ".dll");
            return File.Exists(candidatePath) ? _roslynContext.LoadFromAssemblyPath(candidatePath) : null;
        };
    }

    private static bool IsRoslynAssembly(string assemblyName) =>
        assemblyName.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal);
}
