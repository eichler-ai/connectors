using System;
using System.IO;
using System.Runtime.Loader;
using System.Threading;

namespace MCPBridge.Core.Discovery;

/// <summary>
/// Same fix as <see cref="MCPBridge.Core.Execution.RoslynAssemblyIsolation"/>, for the same underlying
/// reason: Revit's AddInLoader loads this add-in via <c>Assembly.LoadFrom</c>, not a deps.json-driven
/// host, so MCPBridge.Core.dll's own PackageReference dependencies (Microsoft.Data.Sqlite,
/// SQLitePCLRaw.*) are NOT automatically found via default assembly-resolution probing even though they
/// sit physically right next to MCPBridge.Core.dll in the Addins folder -- confirmed live: a bare
/// <see cref="DiscoveryCache"/> reference threw <c>TypeLoadException</c> under Revit's AddInLoader despite
/// every required DLL (including the flattened native e_sqlite3.dll) being present on disk.
///
/// Registers an <see cref="AssemblyLoadContext.Default"/>.Resolving handler that, for any
/// Microsoft.Data.Sqlite/SQLitePCLRaw* request the default context's normal probing can't already
/// satisfy, loads it explicitly from MCPBridge's own directory into a dedicated, non-collectible
/// AssemblyLoadContext -- rather than failing, or (if another add-in happens to bundle a different SQLite
/// version and loads first) silently binding to a foreign, version-mismatched copy.
/// </summary>
public static class SqliteAssemblyIsolation
{
    private static int _initialized;
    private static AssemblyLoadContext? _sqliteContext;

    public static bool IsInitialized => Volatile.Read(ref _initialized) == 1;

    public static void EnsureInitialized()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
        {
            return;
        }

        _sqliteContext = new AssemblyLoadContext("MCPBridge.SqliteIsolation", isCollectible: false);
        var baseDirectory = Path.GetDirectoryName(typeof(SqliteAssemblyIsolation).Assembly.Location) ?? AppContext.BaseDirectory;

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            if (name.Name is null || !IsSqliteAssembly(name.Name))
            {
                return null;
            }

            var candidatePath = Path.Combine(baseDirectory, name.Name + ".dll");
            return File.Exists(candidatePath) ? _sqliteContext.LoadFromAssemblyPath(candidatePath) : null;
        };
    }

    private static bool IsSqliteAssembly(string assemblyName) =>
        assemblyName.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) ||
        assemblyName.StartsWith("SQLitePCLRaw", StringComparison.Ordinal);
}
