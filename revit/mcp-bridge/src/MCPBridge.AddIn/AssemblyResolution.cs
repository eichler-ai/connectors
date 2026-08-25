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
/// Deliberately excludes Microsoft.CodeAnalysis* (Roslyn) names ON AssemblyLoadContext.Default ONLY:
/// RoslynAssemblyIsolation already owns resolving those there, into its own dedicated, non-Default
/// AssemblyLoadContext (see its own doc comment) -- but RoslynAssemblyIsolation never hooks the add-in's
/// own ALC, so this class still handles Roslyn names there via its own deps.json-scoped resolver (second
/// independent PR review finding: an unconditional exclusion silently dropped the safety net entirely on
/// Revit 2025+, where MCPBridge.Core loads into the add-in's own ALC rather than Default). An earlier
/// version of this class had no exclusion at all and, since it registers before
/// RoslynAssemblyIsolation.EnsureInitialized() runs (this is called from OnStartup; that from
/// BridgeHost.Start()), always won the race for Roslyn names on Default via its own
/// AssemblyDependencyResolver -- defeating the isolation it exists for. Found by independent PR review,
/// not hypothetical.
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
        // RoslynAssemblyIsolation only ever hooks AssemblyLoadContext.Default.Resolving (see its own
        // doc comment/source) -- it has no handler at all on the add-in's own (non-Default) ALC. Second
        // independent PR review finding: the first version of this exclusion skipped Roslyn names on
        // BOTH contexts unconditionally, so on Revit 2025+ (where MCPBridge.Core loads into the add-in's
        // own ALC, not Default) a Roslyn resolution request on THAT context would hit this skip, find no
        // other handler waiting to pick it up, and fall through to AppDomain-wide resolution -- silently
        // losing the safety net entirely instead of deferring to anything. Only defer when we're actually
        // racing RoslynAssemblyIsolation, i.e. on Default.
        var deferToRoslynIsolation = ReferenceEquals(context, AssemblyLoadContext.Default)
            && name.Name is not null
            && name.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal);
        if (deferToRoslynIsolation)
        {
            return null;
        }

        var path = s_resolver?.ResolveAssemblyToPath(name);
        return path is null ? null : context.LoadFromAssemblyPath(path);
    }
}
