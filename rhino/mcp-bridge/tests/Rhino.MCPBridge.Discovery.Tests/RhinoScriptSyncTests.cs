using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Rhino.MCPBridge.Core.Discovery;
using Xunit;

namespace Rhino.MCPBridge.Discovery.Tests;

/// <summary>
/// DiscoveryCache.SyncSource — the synthetic-source path that puts rhinoscriptsyntax into the same cache
/// as the reflected assemblies (kind=rhinoscript, PRD §09). Uses the bundled fixture modules so it runs on
/// CI with no Rhino. Asserts the rhinoscript functions are searchable, describable and listable, and that
/// the content-hash staleness (no-op when unchanged, replace when changed) works.
/// </summary>
public class RhinoScriptSyncTests
{
    private static string FixtureDir => Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "rhinoscript");

    private static (DiscoveryCache Cache, string Hash) SyncedCache()
    {
        var indexed = RhinoScriptIndexer.Index(FixtureDir)!.Value;
        var cache = new DiscoveryCache(":memory:");
        cache.SyncSource("rhinoscript", RhinoScriptIndexer.SourceId, indexed.ContentHash, indexed.Types);
        return (cache, indexed.ContentHash);
    }

    [Fact]
    public void SyncSource_MakesRhinoScriptSearchable()
    {
        var (cache, _) = SyncedCache();
        var hit = cache.Search("add a circle", namespaceFilter: null)
            .FirstOrDefault(s => s.Member.MemberId == "rhinoscript:AddCircle");
        Assert.NotNull(hit.Member);
        Assert.Equal("rhinoscriptsyntax", hit.Member.Namespace);
        Assert.Equal("AddCircle(plane_or_center, radius)", hit.Member.Signature);
    }

    [Fact]
    public void SyncSource_ListsUnderTheRhinoscriptsyntaxNamespaceAndType()
    {
        var (cache, _) = SyncedCache();
        Assert.Contains(cache.ListNamespaces(), n => n.Namespace == "rhinoscriptsyntax");
        Assert.Contains("rhinoscriptsyntax", cache.ListTypeNames("rhinoscriptsyntax"));
        Assert.Contains("AddCircle", cache.ListMemberNames("rhinoscriptsyntax", "rhinoscriptsyntax"));
    }

    [Fact]
    public void DescribeFunction_ResolvesARhinoScriptMember()
    {
        var (cache, _) = SyncedCache();
        var result = new DiscoveryService(cache).DescribeFunction("rhinoscriptsyntax.AddCircle", null);
        Assert.NotNull(result.Single);
        Assert.Equal("Adds a circle curve to the document", result.Single!.Summary);
    }

    [Fact]
    public void SyncSource_SameHashIsANoOp_ChangedHashReplaces()
    {
        var indexed = RhinoScriptIndexer.Index(FixtureDir)!.Value;
        using var cache = new DiscoveryCache(":memory:");

        var first = cache.SyncSource("rhinoscript", RhinoScriptIndexer.SourceId, indexed.ContentHash, indexed.Types);
        Assert.Equal(1, first.Added);

        var again = cache.SyncSource("rhinoscript", RhinoScriptIndexer.SourceId, indexed.ContentHash, indexed.Types);
        Assert.Equal(1, again.Unchanged); // identical hash -> skipped
        Assert.Equal(0, again.Added + again.Updated);

        var changed = cache.SyncSource("rhinoscript", RhinoScriptIndexer.SourceId, indexed.ContentHash + "x", indexed.Types);
        Assert.Equal(1, changed.Updated); // new hash -> purge + reinsert
        // Still exactly one rhinoscriptsyntax namespace afterward (not duplicated).
        Assert.Single(cache.ListNamespaces(), n => n.Namespace == "rhinoscriptsyntax");
    }

    /// <summary>
    /// Regression: a cache file written before the kind-CHECK gained 'rhinoscript'/'grasshopper' (PRD §09)
    /// must be rebuilt on open, not rejected. SQLite cannot ALTER a CHECK in place, so without the
    /// schema-version migration the rhinoscript sync fails live with "CHECK constraint failed:
    /// kind IN ('core','addin')". This writes that exact old-schema file and asserts the reopened cache
    /// accepts a rhinoscript source.
    /// </summary>
    [Fact]
    public void OpeningAPreRhinoScriptSchemaFile_RebuildsAndAcceptsRhinoScript()
    {
        var path = Path.Combine(Path.GetTempPath(), $"discovery-oldschema-{System.Guid.NewGuid():N}.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                // The v1 schema: kind CHECK without 'rhinoscript'/'grasshopper', and user_version left at 0.
                cmd.CommandText = """
                    CREATE TABLE assemblies (
                        id INTEGER PRIMARY KEY,
                        kind TEXT NOT NULL CHECK (kind IN ('core','addin')),
                        name TEXT NOT NULL,
                        file_path TEXT NOT NULL UNIQUE,
                        file_hash TEXT NOT NULL,
                        file_version TEXT,
                        last_synced_at TEXT NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            var indexed = RhinoScriptIndexer.Index(FixtureDir)!.Value;
            using var cache = new DiscoveryCache(path);
            var result = cache.SyncSource("rhinoscript", RhinoScriptIndexer.SourceId, indexed.ContentHash, indexed.Types);

            Assert.Equal(1, result.Added); // rebuilt table accepts kind=rhinoscript
            Assert.Contains(cache.ListNamespaces(), n => n.Namespace == "rhinoscriptsyntax");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
