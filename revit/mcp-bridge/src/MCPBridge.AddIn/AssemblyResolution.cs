using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace MCPBridge.AddIn;

/// <summary>
/// Safety net for resolving MCPBridge.Core's transitive NuGet dependencies (Roslyn) when Revit's
/// add-in host does not already do it.
///
/// Revit 2025+ loads each add-in into its own <see cref="AssemblyLoadContext"/>. In the normal case
/// that ALC honours the add-in's own <c>.deps.json</c>, so the Roslyn assemblies sitting next to
/// MCPBridge.AddIn.dll (see CopyLocalLockFileAssemblies in the csproj) resolve with no help from us.
/// This class exists only for the case where it does not.
///
/// It deliberately does NOT mutate any process-wide resolver state. An earlier version reflected into
/// AssemblyLoadContext's private static AssemblyResolve field, cleared it, and re-registered the
/// seized handlers onto AppDomain.CurrentDomain -- that reorders Revit's own resolvers and every other
/// add-in's, process-wide, and was never actually needed: the FileNotFoundException it was written to
/// work around turned out to be caused by a stale second copy of this add-in deployed under %APPDATA%
/// (predating this class entirely) that Revit was loading instead of the freshly-built one under
/// %ProgramData%. Live-tested root cause, not a guess -- see git history on this file for the debugging
/// trail if the assembly-resolution failure ever resurfaces for a genuinely different reason.
/// </summary>
internal static class AssemblyResolution
{
    private static bool s_registered;
    private static AssemblyDependencyResolver? s_resolver;
    private static string? s_probeDirectory;

    internal static void Register()
    {
        if (s_registered)
        {
            return;
        }

        s_registered = true;

        var self = typeof(AssemblyResolution).Assembly;
        var location = self.Location;
        if (string.IsNullOrEmpty(location))
        {
            return; // single-file/in-memory load: nothing to probe against.
        }

        s_probeDirectory = Path.GetDirectoryName(location);

        // The correct API for "resolve a plugin's own .deps.json-declared dependencies". Reads
        // MCPBridge.AddIn.deps.json, which does list Microsoft.CodeAnalysis.Scripting 4.11.0.
        try
        {
            s_resolver = new AssemblyDependencyResolver(location);
        }
        catch (InvalidOperationException)
        {
            // No .deps.json next to the assembly -- fall through to plain directory probing.
        }

        // Hook the ALC this add-in was actually loaded into (Revit's per-add-in ALC), not Default:
        // anything MCPBridge.AddIn/MCPBridge.Core reference is resolved by THAT context, and loading
        // into it keeps type identity consistent (Assembly.LoadFrom would land in Default and can
        // produce two distinct Microsoft.CodeAnalysis.Scripting types).
        var ownContext = AssemblyLoadContext.GetLoadContext(self);
        if (ownContext is not null)
        {
            ownContext.Resolving += OnResolving;
        }

        if (!ReferenceEquals(ownContext, AssemblyLoadContext.Default))
        {
            AssemblyLoadContext.Default.Resolving += OnResolving;
        }
    }

    private static Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
    {
        var path = s_resolver?.ResolveAssemblyToPath(name);

        // The build-time .deps.json records package assets by their package-relative path
        // ("lib/net8.0/Microsoft.CodeAnalysis.Scripting.dll"), while CopyLocalLockFileAssemblies
        // flattens them next to the add-in. AssemblyDependencyResolver normally still finds them via
        // its app-directory probe, but fall back explicitly rather than depend on that.
        if (path is null && !string.IsNullOrEmpty(name.Name) && !string.IsNullOrEmpty(s_probeDirectory))
        {
            var candidate = Path.Combine(s_probeDirectory, name.Name + ".dll");
            if (File.Exists(candidate))
            {
                path = candidate;
            }
        }

        return path is null ? null : context.LoadFromAssemblyPath(path);
    }
}
