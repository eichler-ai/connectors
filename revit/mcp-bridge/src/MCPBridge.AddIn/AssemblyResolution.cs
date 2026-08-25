using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace MCPBridge.AddIn;

/// <summary>
/// Safety net for resolving MCPBridge.Core's transitive NuGet dependencies when Revit's add-in host
/// does not already do it.
///
/// Revit 2025+ loads each add-in into its own <see cref="AssemblyLoadContext"/>. In the normal case
/// that ALC honours the add-in's own <c>.deps.json</c>, so transitive dependencies sitting next to
/// MCPBridge.AddIn.dll (see CopyLocalLockFileAssemblies in the csproj) resolve with no help from us.
/// This class exists only for the case where it does not.
///
/// Deliberately excludes Microsoft.CodeAnalysis* (Roslyn) names: RoslynAssemblyIsolation already owns
/// resolving those, into its own dedicated, non-Default AssemblyLoadContext (see its own doc comment).
/// An earlier version of this class had no such exclusion and, since it registers before
/// RoslynAssemblyIsolation.EnsureInitialized() runs (this is called from OnStartup; that from
/// BridgeHost.Start()), always won the race for Roslyn names via its own AssemblyDependencyResolver --
/// answering the request from Default before RoslynAssemblyIsolation's handler ever got a chance,
/// defeating the isolation it exists for. Found by independent PR review, not hypothetical.
///
/// Also deliberately does NOT fall back to an unscoped same-directory probe for names
/// AssemblyDependencyResolver doesn't resolve: that would serve *any* unresolved assembly name from
/// this add-in's folder regardless of version/PublicKeyToken, including names Revit itself or another
/// add-in is trying to resolve for unrelated reasons -- exactly the kind of process-wide, version-blind
/// side effect this class's design is meant to avoid. AssemblyDependencyResolver's own .deps.json-scoped
/// resolution (including its own app-directory probing for assets it already knows about) is enough;
/// live testing confirmed it resolves MCPBridge.AddIn's actual dependencies correctly on its own.
///
/// It deliberately does NOT mutate any process-wide resolver state either. An earlier version reflected
/// into AssemblyLoadContext's private static AssemblyResolve field, cleared it, and re-registered the
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

        // The correct API for "resolve a plugin's own .deps.json-declared dependencies".
        try
        {
            s_resolver = new AssemblyDependencyResolver(location);
        }
        catch (InvalidOperationException)
        {
            // No .deps.json next to the assembly -- nothing this class can do; RoslynAssemblyIsolation
            // (a same-directory probe, not deps.json-based) still covers Roslyn independently.
            return;
        }

        // Hook the ALC this add-in was actually loaded into (Revit's per-add-in ALC), not Default:
        // anything MCPBridge.AddIn/MCPBridge.Core reference is resolved by THAT context, and loading
        // into it keeps type identity consistent (Assembly.LoadFrom would land in Default and can
        // produce two distinct types for the same assembly name).
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
        if (name.Name is null || name.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
        {
            // RoslynAssemblyIsolation's own Resolving handler owns these -- let this request fall
            // through to it (or to whatever else is registered) rather than racing it.
            return null;
        }

        var path = s_resolver?.ResolveAssemblyToPath(name);
        return path is null ? null : context.LoadFromAssemblyPath(path);
    }
}
