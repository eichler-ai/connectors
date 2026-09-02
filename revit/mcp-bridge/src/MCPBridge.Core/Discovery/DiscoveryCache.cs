using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MCPBridge.Core.Discovery;

/// <summary>One reflected member, joined back with its declaring type's identity -- the shape both list_functions' type-scoped tier and search_functions' results are built from.</summary>
public sealed class DiscoveryMemberRow
{
    public required string MemberId { get; init; }
    public required string Kind { get; init; }
    public required string Namespace { get; init; }
    public required string DeclaringType { get; init; }
    public required string Name { get; init; }
    public required string Signature { get; init; }
    public string? Summary { get; init; }
    public string? Returns { get; init; }
    public required IReadOnlyList<ReflectedParameter> Parameters { get; init; }

    /// <summary>
    /// True when this member's declaring type came from RevitAPI.dll/RevitAPIUI.dll (assemblies.kind =
    /// 'core'), false for any add-in assembly. PRD §08's discovery design deliberately indexes add-ins
    /// too -- an agent scripting against a live session can validly call another add-in's public API,
    /// same as Revit's own -- so this is used to BOOST core results in ranking (<see cref="DiscoveryCache.Search"/>),
    /// never to exclude add-ins outright. Confirmed live (issue filed from the coverage-plan Phase A
    /// session): on a real dev VM with ~690 loaded namespaces, an unscoped search_functions query for
    /// "EditGroup postable command" returned zero Group-related results, buried entirely under
    /// unrelated third-party add-in Command classes -- the existing "core wins ties" tie-break already
    /// used by <see cref="DiscoveryCache.FindTypeRow"/>/<see cref="DiscoveryCache.FindTypeRowByFullName"/>
    /// was never applied to the ranked query paths at all.
    /// </summary>
    public required bool IsCoreAssembly { get; init; }
}

/// <summary>Counts from one <see cref="DiscoveryCache.Sync"/> call -- surfaced for logging (PRD §01: an automatic-resolution pass like this deserves a trace, not just silent success).</summary>
public sealed record DiscoverySyncResult(int Added, int Updated, int Removed, int Unchanged);

/// <summary>
/// SQLite-backed persistent cache of the reflected discovery surface (PRD §08 addendum: live reflection
/// alone cost ~1.5s to enumerate types plus ~700ms per full-corpus search_functions scan, paid on every
/// Revit process launch with nothing carried over -- this cache survives across restarts and turns repeat
/// scans into an indexed query). <see cref="Sync"/> is the only thing that mutates it; every other member is
/// a read-only query DiscoveryService composes into list_functions/search_functions/describe_function.
///
/// <para>
/// Deliberately takes a plain file path (":memory:" included) rather than computing
/// %LOCALAPPDATA%\Connectors\Revit\ itself -- same convention as
/// <see cref="MCPBridge.Core.Connection.BrokerDiscoveryOptions.Local"/>: the caller (BridgeHost in
/// production, a test elsewhere) owns path resolution so this class stays fully testable without touching
/// the real filesystem.
/// </para>
/// </summary>
public sealed class DiscoveryCache : IDisposable
{
    private readonly SqliteConnection _connection;

    // Independent PR review finding: SqliteConnection/SqliteCommand aren't thread-safe, and this cache is
    // genuinely accessed from two threads -- the connection thread serving list_functions/search_functions,
    // and the threadpool timer driving the deferred re-sync (BridgeHost). Every public member below takes
    // this lock; Dispose() takes it too, which also closes the "Stop() tears down mid-sync" gap the same
    // review pass flagged, since disposal now waits for any in-flight Sync/query on another thread to finish
    // rather than pulling the connection out from under it.
    private readonly object _lock = new();
    private readonly int _rankedDepth;
    private bool _disposed;

    public DiscoveryCache(string databasePath)
        : this(databasePath, TierCandidateLimit)
    {
    }

    /// <summary>
    /// Overrides <see cref="TierCandidateLimit"/> so the cap-after-scoring behaviour is testable at all:
    /// proving it needs MORE candidates than the cap, and no hand-written fixture assembly is going to
    /// produce 500 members.
    ///
    /// <para><b>Internal, not a public optional parameter.</b> Independent PR review finding: this shipped
    /// as `public DiscoveryCache(string, int?)` on the first draft, which is exactly what this assembly's
    /// own InternalsVisibleTo comment warns against -- "it stays internal under this assembly's
    /// default-to-internal rule rather than being made public just to be testable". The grant that comment
    /// sits on was already configured for this test assembly, so the internal seam cost nothing. No new
    /// capability was ever exposed (DiscoveryCache is public either way and holds no adapter), but the rule
    /// is there so that judgment call does not have to be re-made per type.</para>
    ///
    /// <para>Caps BOTH tiers -- tier 2's post-scoring window and tier 3's FTS <c>LIMIT</c> -- since they
    /// share the one constant. A test that sets it small and then asserts on tier-3 rows will be truncated
    /// there too.</para>
    /// </summary>
    internal DiscoveryCache(string databasePath, int rankedDepth)
    {
        // A zero or negative depth is not a degenerate-but-harmless setting: Take(0) empties the window and
        // MaterializeMembers would emit "IN ()", a SQLite parse error, while LIMIT -1 means UNLIMITED in
        // SQLite and would silently uncap tier 3 at the same time.
        if (rankedDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rankedDepth), rankedDepth, "rankedDepth must be positive.");
        }

        _rankedDepth = rankedDepth;

        // Independent PR review finding (2nd round, M1): the caller's self-heal (delete-and-recreate a
        // corrupted database) only works if a failure here doesn't leave a live, locked connection handle
        // behind -- the dominant real corruption failure ("database disk image is malformed") is thrown by
        // PRAGMA/CreateSchema below, AFTER _connection.Open() already succeeded, and if the constructor
        // throws without disposing it first, the caller's very next File.Delete() hits a Windows sharing
        // violation against a handle nothing will ever release. This try/catch is the fix: any failure past
        // Open() disposes the connection before rethrowing, so the caller's delete actually succeeds.
        _connection = new SqliteConnection($"Data Source={databasePath}");
        try
        {
            _connection.Open();

            using (var pragma = _connection.CreateCommand())
            {
                // Required for the types/members ON DELETE CASCADE below to actually cascade -- SQLite
                // ignores foreign key actions entirely unless this pragma is set on the connection, every
                // time it opens.
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }

            CreateSchema();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Dispose();
        }
    }

    /// <summary>
    /// Independent PR review finding (2nd round, L1): the deferred re-sync Timer's non-waiting
    /// <c>Dispose()</c> means a callback already past this check (waiting on <see cref="_lock"/>) can still
    /// run after <see cref="Dispose"/> above has released it -- this guard is what actually makes that a
    /// clean no-op instead of an <see cref="ObjectDisposedException"/> against a torn-down connection.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DiscoveryCache));
        }
    }

    private void CreateSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS assemblies (
                id INTEGER PRIMARY KEY,
                kind TEXT NOT NULL CHECK (kind IN ('core','addin')),
                name TEXT NOT NULL,
                file_path TEXT NOT NULL UNIQUE,
                file_hash TEXT NOT NULL,
                file_version TEXT,
                last_synced_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS types (
                id INTEGER PRIMARY KEY,
                assembly_id INTEGER NOT NULL REFERENCES assemblies(id) ON DELETE CASCADE,
                namespace TEXT NOT NULL,
                name TEXT NOT NULL,
                full_name TEXT NOT NULL,
                member_id TEXT NOT NULL,
                base_full_name TEXT,
                documented INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_types_namespace ON types(namespace);
            CREATE INDEX IF NOT EXISTS ix_types_full_name ON types(full_name);
            CREATE INDEX IF NOT EXISTS ix_types_ns_name ON types(namespace, name);

            CREATE TABLE IF NOT EXISTS members (
                id INTEGER PRIMARY KEY,
                type_id INTEGER NOT NULL REFERENCES types(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                signature TEXT NOT NULL,
                summary TEXT,
                member_id TEXT NOT NULL,
                returns TEXT,
                params_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_members_type_id ON members(type_id);

            CREATE VIRTUAL TABLE IF NOT EXISTS members_fts USING fts5(
                name, summary, type_name,
                tokenize = 'unicode61'
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // -------------------------------------------------------------------------------------------------
    // Sync
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Diffs <paramref name="currentAssemblies"/> (the assemblies actually loaded into this Revit process
    /// right now) against the `assemblies` table by file hash, and reconciles: new assemblies are reflected
    /// and inserted, changed ones (hash mismatch -- a rebuilt add-in DLL, typically) are purged and
    /// re-reflected, gone ones (an add-in that was loaded before but isn't now) are purged, and unchanged
    /// ones are skipped entirely -- no re-reflection, no re-write.
    ///
    /// <para>
    /// An assembly with no <see cref="Assembly.Location"/> (dynamic/in-memory, e.g. Roslyn's own
    /// per-script collectible ALCs -- PRD §06) can't be hashed or matched back to a stable identity across
    /// syncs, so it's silently excluded from this call entirely -- not an error, just not a candidate for
    /// persistent tracking. Same for a Location that no longer exists/isn't readable (skipped, not thrown).
    /// </para>
    /// </summary>
    public DiscoverySyncResult Sync(IReadOnlyList<(string Kind, Assembly Assembly)> currentAssemblies)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return SyncLocked(currentAssemblies);
        }
    }

    private DiscoverySyncResult SyncLocked(IReadOnlyList<(string Kind, Assembly Assembly)> currentAssemblies)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");

        var current = new Dictionary<string, (string Kind, Assembly Assembly, string Hash, string? Version, string Name)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, assembly) in currentAssemblies)
        {
            if (string.IsNullOrEmpty(assembly.Location) || current.ContainsKey(assembly.Location))
            {
                continue;
            }

            string hash;
            try
            {
                hash = ComputeFileHash(assembly.Location);
            }
            catch
            {
                continue; // unreadable file -- can't sync this one, skip rather than fail the whole call.
            }

            current[assembly.Location] = (kind, assembly, hash, assembly.GetName().Version?.ToString(), assembly.GetName().Name ?? assembly.Location);
        }

        var existing = new Dictionary<string, (long Id, string Hash)>(StringComparer.OrdinalIgnoreCase);
        using (var select = _connection.CreateCommand())
        {
            select.CommandText = "SELECT id, file_path, file_hash FROM assemblies";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                existing[reader.GetString(1)] = (reader.GetInt64(0), reader.GetString(2));
            }
        }

        int added = 0, updated = 0, removed = 0, unchanged = 0;

        using var transaction = _connection.BeginTransaction();

        foreach (var path in existing.Keys.Where(p => !current.ContainsKey(p)).ToList())
        {
            DeleteAssembly(existing[path].Id, transaction);
            removed++;
        }

        foreach (var (path, info) in current)
        {
            if (existing.TryGetValue(path, out var row))
            {
                if (string.Equals(row.Hash, info.Hash, StringComparison.Ordinal))
                {
                    unchanged++;
                    continue;
                }

                DeleteAssembly(row.Id, transaction);
                updated++;
            }
            else
            {
                added++;
            }

            InsertAssembly(info.Kind, info.Name, path, info.Hash, info.Version, now, info.Assembly, transaction);
        }

        transaction.Commit();

        return new DiscoverySyncResult(added, updated, removed, unchanged);
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private void DeleteAssembly(long assemblyId, SqliteTransaction transaction)
    {
        // members_fts has no foreign key of its own (FTS5 virtual tables can't declare one) -- its rows must
        // be deleted explicitly, and BEFORE the cascading delete below removes the `members` rows this
        // subquery joins through (once those are gone there's nothing left to join against).
        using (var deleteFts = _connection.CreateCommand())
        {
            deleteFts.Transaction = transaction;
            deleteFts.CommandText = """
                DELETE FROM members_fts WHERE rowid IN (
                    SELECT m.id FROM members m JOIN types t ON m.type_id = t.id WHERE t.assembly_id = @id
                )
                """;
            deleteFts.Parameters.AddWithValue("@id", assemblyId);
            deleteFts.ExecuteNonQuery();
        }

        using var deleteAssembly = _connection.CreateCommand();
        deleteAssembly.Transaction = transaction;
        deleteAssembly.CommandText = "DELETE FROM assemblies WHERE id = @id"; // cascades to types, then members
        deleteAssembly.Parameters.AddWithValue("@id", assemblyId);
        deleteAssembly.ExecuteNonQuery();
    }

    private void InsertAssembly(string kind, string name, string path, string hash, string? version, string syncedAt, Assembly assembly, SqliteTransaction transaction)
    {
        long assemblyId;
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO assemblies (kind, name, file_path, file_hash, file_version, last_synced_at)
                VALUES (@kind, @name, @path, @hash, @version, @syncedAt);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("@kind", kind);
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@path", path);
            insert.Parameters.AddWithValue("@hash", hash);
            insert.Parameters.AddWithValue("@version", (object?)version ?? DBNull.Value);
            insert.Parameters.AddWithValue("@syncedAt", syncedAt);
            assemblyId = (long)insert.ExecuteScalar()!;
        }

        IReadOnlyList<ReflectedType> types;
        try
        {
            types = DiscoveryReflector.Reflect(assembly);
        }
        catch
        {
            // Review posture carried over from the original DiscoveryService: a pathological assembly must
            // not take down the whole sync. The assembly row itself still gets recorded (so it's tracked as
            // "synced, zero types" rather than retried every single call), just with no types/members.
            return;
        }

        foreach (var type in types)
        {
            long typeId;
            using (var insertType = _connection.CreateCommand())
            {
                insertType.Transaction = transaction;
                insertType.CommandText = """
                    INSERT INTO types (assembly_id, namespace, name, full_name, member_id, base_full_name, documented)
                    VALUES (@assemblyId, @ns, @name, @fullName, @memberId, @baseFullName, @documented);
                    SELECT last_insert_rowid();
                    """;
                insertType.Parameters.AddWithValue("@assemblyId", assemblyId);
                insertType.Parameters.AddWithValue("@ns", type.Namespace);
                insertType.Parameters.AddWithValue("@name", type.Name);
                insertType.Parameters.AddWithValue("@fullName", type.FullName);
                insertType.Parameters.AddWithValue("@memberId", type.MemberId);
                insertType.Parameters.AddWithValue("@baseFullName", (object?)type.BaseFullName ?? DBNull.Value);
                insertType.Parameters.AddWithValue("@documented", type.Documented ? 1 : 0);
                typeId = (long)insertType.ExecuteScalar()!;
            }

            foreach (var member in type.Members)
            {
                long memberRowId;
                using (var insertMember = _connection.CreateCommand())
                {
                    insertMember.Transaction = transaction;
                    insertMember.CommandText = """
                        INSERT INTO members (type_id, kind, name, signature, summary, member_id, returns, params_json)
                        VALUES (@typeId, @kind, @name, @signature, @summary, @memberId, @returns, @paramsJson);
                        SELECT last_insert_rowid();
                        """;
                    insertMember.Parameters.AddWithValue("@typeId", typeId);
                    insertMember.Parameters.AddWithValue("@kind", member.Kind);
                    insertMember.Parameters.AddWithValue("@name", member.Name);
                    insertMember.Parameters.AddWithValue("@signature", member.Signature);
                    insertMember.Parameters.AddWithValue("@summary", (object?)member.Summary ?? DBNull.Value);
                    insertMember.Parameters.AddWithValue("@memberId", member.MemberId);
                    insertMember.Parameters.AddWithValue("@returns", (object?)member.Returns ?? DBNull.Value);
                    insertMember.Parameters.AddWithValue("@paramsJson", JsonSerializer.Serialize(member.Parameters));
                    memberRowId = (long)insertMember.ExecuteScalar()!;
                }

                using var insertFts = _connection.CreateCommand();
                insertFts.Transaction = transaction;
                insertFts.CommandText = "INSERT INTO members_fts (rowid, name, summary, type_name) VALUES (@rowid, @name, @summary, @typeName)";
                insertFts.Parameters.AddWithValue("@rowid", memberRowId);
                insertFts.Parameters.AddWithValue("@name", member.Name);
                insertFts.Parameters.AddWithValue("@summary", (object?)member.Summary ?? "");
                insertFts.Parameters.AddWithValue("@typeName", type.Name);
                insertFts.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Test-only seam: forces one already-synced assembly's stored file_hash to a bogus value, so a
    /// following <see cref="Sync"/> call deterministically exercises the "changed" (purge + re-reflect)
    /// path without needing two genuinely different on-disk builds of the same test assembly. Never called
    /// by production code (BridgeHost only ever calls <see cref="Sync"/> itself).
    /// </summary>
    public void SetStoredHashForTesting(string assemblyLocation, string bogusHash)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE assemblies SET file_hash = @hash WHERE file_path = @path";
            cmd.Parameters.AddWithValue("@hash", bogusHash);
            cmd.Parameters.AddWithValue("@path", assemblyLocation);
            cmd.ExecuteNonQuery();
        }
    }

    // -------------------------------------------------------------------------------------------------
    // list_functions queries
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every namespace with at least one documented type, core-namespaces-first then alphabetical (see the
    /// ordering paragraph below), with a per-namespace documented-type count. Independent PR review
    /// finding: the global/no-namespace bucket (types whose <c>Type.Namespace</c>
    /// is null -- e.g. some C++/CLI interop artifacts) is excluded here, not just left in as one more row:
    /// list_functions' tree has no way to *scope into* an empty-string namespace (namespaceFilter treats ""
    /// and null identically, per the mutual-exclusivity check above it), so leaving it in the namespaces tier
    /// created an unreachable dead end an agent could select but never drill into.
    ///
    /// <para>
    /// Independent PR review finding (2nd round, L2): <c>COUNT(DISTINCT name)</c>, not <c>COUNT(*)</c> --
    /// now that add-ins are included, two loaded add-ins vendoring the same library (or two versions of the
    /// same helper DLL) can genuinely produce two <c>types</c> rows with the identical namespace+name, which
    /// a plain row count would double-count.
    /// </para>
    ///
    /// <para>
    /// Ordered core-namespaces-first, alphabetical within each group -- confirmed live (coverage-plan
    /// Phase A session) that a straight alphabetical order buries every <c>Autodesk.Revit.*</c> namespace
    /// behind dozens of pages of third-party add-ins (a real dev VM had 690 total namespaces; two full
    /// pages of 50 in, still zero core-Revit namespaces reached). A namespace counts as "core" here if it
    /// has AT LEAST ONE type from a core assembly -- <c>MIN(a.kind != 'core')</c> is 0 (sorts first) as
    /// soon as any row in the group is core, 1 (sorts after) only when every row in the group is an
    /// add-in's. This still lists every add-in namespace, same as before -- PRD §08's discovery design
    /// deliberately covers add-ins too -- it only changes the ORDER, never what's included.
    /// </para>
    /// </summary>
    public IReadOnlyList<(string Namespace, int TypeCount)> ListNamespaces()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT t.namespace, COUNT(DISTINCT t.name) FROM types t JOIN assemblies a ON t.assembly_id = a.id
                WHERE t.documented = 1 AND t.namespace != ''
                GROUP BY t.namespace
                ORDER BY MIN(a.kind != 'core'), t.namespace COLLATE NOCASE
                """;
            using var reader = cmd.ExecuteReader();
            var results = new List<(string, int)>();
            while (reader.Read())
            {
                results.Add((reader.GetString(0), (int)reader.GetInt64(1)));
            }

            return results;
        }
    }

    /// <summary>
    /// Short (unqualified) names of every documented type in one namespace, alphabetical. Independent PR
    /// review finding (2nd round, L2): <c>DISTINCT</c> for the same reason <see cref="ListNamespaces"/>
    /// uses <c>COUNT(DISTINCT name)</c> -- two duplicate <c>types</c> rows for the same name would otherwise
    /// show up as a visibly duplicated entry in this list.
    /// </summary>
    public IReadOnlyList<string> ListTypeNames(string namespaceName)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT name FROM types WHERE namespace = @ns AND documented = 1 ORDER BY name COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@ns", namespaceName);
            using var reader = cmd.ExecuteReader();
            var results = new List<string>();
            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }

            return results;
        }
    }

    public bool TypeExists(string namespaceName, string typeName)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return FindTypeRow(namespaceName, typeName) is not null;
        }
    }

    public bool TypeExistsByFullName(string fullTypeName)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return FindTypeRowByFullName(fullTypeName) is not null;
        }
    }

    /// <summary>
    /// Distinct member names (constructors excluded from name-dedup collapse the same way overload text
    /// would be -- every overload of the same name collapses to one entry) declared on the type OR any base
    /// type still within the reflected surface, alphabetical. This is list_functions' tier-3 shape (PRD §08
    /// addendum): a member name list to browse, not full signatures -- describe_function is the only way to
    /// get overload detail, by design (see the task brief this cache was built from).
    /// </summary>
    public IReadOnlyList<string> ListMemberNames(string namespaceName, string typeName)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            var typeRow = FindTypeRow(namespaceName, typeName);
            if (typeRow is null)
            {
                return Array.Empty<string>();
            }

            return WalkInheritance(typeRow.Value)
                .Select(m => m.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>All members (every overload) declared on the type OR any base type still within the reflected surface -- used by describe_function to resolve a member name to its candidate overload(s). Scoped by namespace+short type name (list_functions' own scoping shape).</summary>
    public IReadOnlyList<DiscoveryMemberRow> GetMembersIncludingInherited(string namespaceName, string typeName)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            var typeRow = FindTypeRow(namespaceName, typeName);
            return typeRow is null ? Array.Empty<DiscoveryMemberRow>() : WalkInheritance(typeRow.Value);
        }
    }

    /// <summary>Same as <see cref="GetMembersIncludingInherited(string,string)"/>, scoped by a fully-qualified dotted type name instead -- describe_function's own scoping shape ("Namespace.Type.Member" -- see its own doc comment).</summary>
    public IReadOnlyList<DiscoveryMemberRow> GetMembersIncludingInheritedByFullName(string fullTypeName)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            var typeRow = FindTypeRowByFullName(fullTypeName);
            return typeRow is null ? Array.Empty<DiscoveryMemberRow>() : WalkInheritance(typeRow.Value);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Whole-corpus dump (issue #107) -- the broker builds its own search index from these pages.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Number of members on documented types -- the same population <see cref="Search"/> ranks over, so the
    /// broker's index and this cache agree on what "the corpus" is.
    /// </summary>
    public int CountMembers()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM members m JOIN types t ON m.type_id = t.id WHERE t.documented = 1";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>
    /// One page of the corpus in stable <c>members.id</c> order, so consecutive (offset, limit) pages
    /// partition it exactly once. Ordering by rowid rather than name is what makes paging safe against a
    /// concurrent <see cref="Sync"/> only in the trivial sense (a sync mid-dump changes ids; the broker
    /// detects that by re-reading <see cref="CorpusFingerprint"/> at the end of the dump).
    /// </summary>
    public IReadOnlyList<DiscoveryMemberRow> EnumerateMembers(int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT m.kind, m.name, m.signature, m.summary, m.member_id, m.returns, m.params_json, t.namespace, t.full_name, a.kind
                FROM members m JOIN types t ON m.type_id = t.id JOIN assemblies a ON t.assembly_id = a.id
                WHERE t.documented = 1
                ORDER BY m.id
                LIMIT @limit OFFSET @offset
                """;
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);
            using var reader = cmd.ExecuteReader();
            return ReadMemberRows(reader).ToList();
        }
    }

    /// <summary>
    /// SHA-256 over the reflected assembly set (kind, name, content hash; ordered by path). Changes exactly
    /// when the set of loaded assemblies or any assembly's bytes change -- the same signal <see cref="Sync"/>
    /// keys its own reconcile on -- so it is the identity of the corpus, independent of Revit version.
    /// </summary>
    public string CorpusFingerprint()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT kind, name, file_hash FROM assemblies ORDER BY file_path";
            var sb = new System.Text.StringBuilder();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                sb.Append(reader.GetString(0)).Append('|').Append(reader.GetString(1)).Append('|').Append(reader.GetString(2)).Append('\n');
            }
            var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
    }

    private readonly record struct TypeRow(long Id, string Namespace, string Name, string FullName, string? BaseFullName);

    /// <summary>
    /// Independent PR review finding: <c>full_name</c> (and namespace+name) is only unique for the core
    /// RevitAPI/RevitAPIUI surface -- once add-in assemblies are included too, two loaded add-ins vendoring
    /// the same library (or two versions of the same helper DLL) can genuinely produce two <c>types</c> rows
    /// with an identical namespace+name/full_name. <c>LIMIT 1</c> with no ordering made which row won
    /// arbitrary (SQLite row order isn't a stable contract) and could silently resolve a lookup against the
    /// wrong assembly's version of a type. Ties now break deterministically: core wins over any add-in, then
    /// lowest assembly_id (insertion order) -- not a full fix for the underlying ambiguity, but at least a
    /// stable, predictable answer instead of a coin flip that can change between runs.
    /// </summary>
    private TypeRow? FindTypeRow(string namespaceName, string typeName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.namespace, t.name, t.full_name, t.base_full_name
            FROM types t JOIN assemblies a ON t.assembly_id = a.id
            WHERE t.namespace = @ns AND t.name = @name
            ORDER BY (a.kind != 'core'), t.assembly_id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@ns", namespaceName);
        cmd.Parameters.AddWithValue("@name", typeName);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadTypeRow(reader) : null;
    }

    private TypeRow? FindTypeRowByFullName(string fullTypeName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.namespace, t.name, t.full_name, t.base_full_name
            FROM types t JOIN assemblies a ON t.assembly_id = a.id
            WHERE t.full_name = @fullName
            ORDER BY (a.kind != 'core'), t.assembly_id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@fullName", fullTypeName);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadTypeRow(reader) : null;
    }

    private static TypeRow ReadTypeRow(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4));

    /// <summary>
    /// Review finding (H1) from the original in-memory DiscoveryService, carried over unchanged: a
    /// type-scoped member list must include members declared on base types (Revit's API is deeply inherited
    /// -- e.g. Wall.Id is declared on Element), not just what the exact type itself declares. Walks the base
    /// chain via `base_full_name`, stopping the moment a base type isn't itself in the `types` table (i.e.
    /// isn't part of any reflected assembly's surface) -- this naturally stops at the BCL boundary
    /// (System.Object etc. are never reflected/stored) without hardcoding it. Most-derived first; a
    /// (kind, name, signature) duplicate from a base type (an override) is dropped in favor of the
    /// most-derived declaration already seen.
    /// </summary>
    private List<DiscoveryMemberRow> WalkInheritance(TypeRow startType)
    {
        var results = new List<DiscoveryMemberRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        TypeRow? current = startType;
        var guard = 0;
        while (current is { } type && guard++ < 64) // 64: generous depth guard against a malformed base-chain cycle; Revit's real inheritance depth is nowhere close.
        {
            foreach (var member in GetOwnMembers(type.Id, type.Namespace, type.FullName))
            {
                var key = member.Kind + "|" + member.Name + "|" + member.Signature;
                if (seen.Add(key))
                {
                    results.Add(member);
                }
            }

            current = type.BaseFullName is null ? null : FindTypeRowByFullName(type.BaseFullName);
        }

        return results;
    }

    private List<DiscoveryMemberRow> GetOwnMembers(long typeId, string namespaceName, string declaringTypeFullName)
    {
        using var cmd = _connection.CreateCommand();
        // Independent PR review finding: IsCoreAssembly used to be hardcoded false here with a comment
        // claiming "no consumer on this path" -- true today (describe_function/WalkInheritance never rank
        // these rows), but GetMembersIncludingInherited is a PUBLIC method with no such guarantee about
        // its future callers, and a hardcoded false is a silently-wrong answer the moment one shows up,
        // not a loud one. Joining assemblies here costs nothing (same pattern ReadMemberRow's other query
        // sites already use) and makes this row genuinely correct regardless of who reads it.
        cmd.CommandText = """
            SELECT m.kind, m.name, m.signature, m.summary, m.member_id, m.returns, m.params_json, a.kind
            FROM members m JOIN types t ON m.type_id = t.id JOIN assemblies a ON t.assembly_id = a.id
            WHERE m.type_id = @typeId
            """;
        cmd.Parameters.AddWithValue("@typeId", typeId);
        using var reader = cmd.ExecuteReader();

        var results = new List<DiscoveryMemberRow>();
        while (reader.Read())
        {
            results.Add(new DiscoveryMemberRow
            {
                Kind = reader.GetString(0),
                Name = reader.GetString(1),
                Signature = reader.GetString(2),
                Summary = reader.IsDBNull(3) ? null : reader.GetString(3),
                MemberId = reader.GetString(4),
                Returns = reader.IsDBNull(5) ? null : reader.GetString(5),
                Parameters = JsonSerializer.Deserialize<List<ReflectedParameter>>(reader.GetString(6)) ?? new List<ReflectedParameter>(),
                Namespace = namespaceName,
                DeclaringType = declaringTypeFullName,
                IsCoreAssembly = reader.GetString(7) == "core",
            });
        }

        return results;
    }

    // -------------------------------------------------------------------------------------------------
    // search_functions
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// How deep each of the token-match/FTS5 tiers below keeps results -- a cap on how far a caller can
    /// page, well above any realistic topN+cursor walk, NOT a cost control. Both tiers now apply it to a
    /// set already ordered by that tier's own ranking (relevance for tier 2, bm25 for tier 3), so what it
    /// discards is genuinely the tail.
    ///
    /// <para>Issue #76 -- and the reason this comment now states measurements instead of asserting a cost
    /// model. The claim it used to make, that the limit kept a broad query "a cheap indexed query, not a
    /// full scan", was never true of tier 2: its predicate is <c>LIKE '%token%'</c> against two name
    /// columns, which cannot use an index at all, so the full scan of the join is paid whether or not rows
    /// are then discarded. Measured against the real RevitAPI 2027 corpus (25,933 documented members),
    /// reading every matching row rather than 500 costs about 10ms on the widest query found ("id", 4,768
    /// candidates) and is indistinguishable on every natural-language query tried. End-to-end
    /// <c>search_functions</c> stayed between 37 and 137ms across 71 queries, against the ~700ms
    /// full-corpus scan that motivated this cache in the first place -- so the cost is real but the
    /// headroom absorbs it. (An earlier draft of this comment called 46ms vs 36ms "noise". It is a 28%
    /// increase, and the end-to-end figure moved 45ms to 65ms. Affordable is the claim that holds.)</para>
    ///
    /// <para>The corpus size is what decides whether that headroom holds, and it is bounded by Revit's own
    /// API surface plus whatever add-ins are loaded, not by anything a caller controls.</para>
    /// </summary>
    private const int TierCandidateLimit = 500;

    /// <summary>
    /// A small, deterministic tie-breaking boost for core (RevitAPI/RevitAPIUI) results within whatever
    /// tier a row already landed in -- confirmed live (coverage-plan Phase A session) that an unscoped
    /// query can return zero core-Revit hits at all, buried under third-party add-in noise, because
    /// nothing in <see cref="Search"/> previously used the <c>kind</c> column any query path already
    /// carried. Deliberately small (0.5) relative to the 500-point gaps between tiers 1/2/3, so this can
    /// only ever move a result within a tier, never across one -- tier boundaries stay untouched,
    /// matching PRD §08's explicit design intent that add-in APIs remain fully searchable, not
    /// suppressed. Independent PR review finding: within tier 3 specifically, "never across a tier" is a
    /// softer guarantee than "a genuinely stronger add-in match always outranks a weaker core one" might
    /// suggest -- the bm25-derived <c>normalized</c> score below is asymptotically bounded by 499 and
    /// saturates quickly for a strong match, so two close-but-unequal tier-3 matches can legitimately
    /// land within 0.5 of each other, which this boost can then flip. Since issue #65 the same caveat
    /// applies to tier 2, which is now graded across a 249-point band rather than flat. Tier 1 alone still
    /// gives every row the same base score (1000), so there this boost remains the only ranking signal
    /// within the tier. Mirrors the same "core wins ties" policy <see cref="FindTypeRow"/>/
    /// <see cref="FindTypeRowByFullName"/> already apply to type lookups.
    /// </summary>
    /// <summary>
    /// Tier 2's floor: the score of a row admitted to tier 2 with zero relevance, before
    /// <see cref="CoreBoost"/>. INTERNAL rather than private so a test can assert the tier boundary
    /// against the value production actually uses -- an independent review found TierBoundaryTests
    /// hard-coding 500.5, which meant changing this constant would leave the invariant fully broken and
    /// the test permanently green, since its filter would simply match nothing.
    /// </summary>
    internal const double TierTwoFloor = 500.0;

    /// <summary>The bonus a core-assembly row earns, applied AFTER candidate selection. See <see cref="CoreBoost"/>.</summary>
    internal const double CoreAssemblyBoost = 0.5;

    private static double CoreBoost(bool isCoreAssembly) => isCoreAssembly ? CoreAssemblyBoost : 0.0;

    /// <summary>
    /// Width of the score band tier 2 spreads its graded relevance across, above its floor of 500 and well
    /// clear of tier 1's flat 1000 (issue #65). Tier 3 stays bounded below 500, so the tier ordering the
    /// rest of this class documents is unchanged -- only the ordering WITHIN tier 2, which previously did
    /// not exist at all.
    /// </summary>
    private const double TierTwoBand = 249.0;

    /// <summary>
    /// <see cref="DiscoveryMemberRow.DeclaringType"/> holds the namespace-qualified name; relevance scoring
    /// wants the short name, since the namespace is not something a caller is phrasing a natural-language
    /// query against. Generic arity ("ViewSheet`1") is stripped for the same reason.
    /// </summary>
    private static string ShortTypeName(string declaringTypeFullName)
    {
        var lastDot = declaringTypeFullName.LastIndexOf('.');
        var shortName = lastDot < 0 ? declaringTypeFullName : declaringTypeFullName[(lastDot + 1)..];
        return TypeNameFormatting.StripArity(shortName);
    }

    /// <summary>
    /// FTS5-backed ranked search, per the design decision in this feature's task brief: three tiers,
    /// highest first, built on FTS5's own indexing rather than a hand-rolled full-corpus scan (the previous
    /// design -- see search_functions' original ScoreMember doc comment -- cost ~700ms per call precisely
    /// because it scanned every documented member on every query).
    ///
    /// <list type="number">
    /// <item><b>Exact Type.Member.</b> The query cleanly parses as "TypeToken.MemberToken" (or
    /// "TypeToken MemberToken", the last whitespace-separated pair) and both halves resolve to a real,
    /// case-insensitive type-name + member-name pair in the corpus.</item>
    /// <item><b>Query tokens matched across {type name, member name}</b> (not the summary) -- what makes
    /// "wall create" reliably surface Wall.Create even when neither word alone is a great match against a
    /// huge summary corpus. Admits rows leaving at most <see cref="UnmatchedTokenAllowance"/> tokens
    /// unmatched, and grades them by <see cref="IdentifierRelevance"/> across a band above 500; see issue
    /// #65 for why this tier being all-or-nothing, and flat for everything that cleared the bar, produced
    /// both a wrong top result and an alphabetical page 1.</item>
    /// <item><b>FTS5 BM25 fallback</b> against name+summary+type_name combined -- the loose/exploratory
    /// case, ranked by SQLite's own <c>rank</c> column.</item>
    /// </list>
    ///
    /// Deduplicated across tiers (a member found in tier 1 is never also emitted at a lower tier).
    /// </summary>
    public IReadOnlyList<(DiscoveryMemberRow Member, double Score)> Search(string query, string? namespaceFilter)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            var queryLower = query.Trim().ToLowerInvariant();
            var tokens = TokenizeQuery(queryLower);

            var results = new List<(DiscoveryMemberRow Member, double Score)>();
            var seenMemberIds = new HashSet<string>(StringComparer.Ordinal);

            // Tier 1: exact Type.Member. A query copied verbatim from a describe_function result or from
            // Revit's own docs is naturally fully-qualified ("Autodesk.Revit.DB.Wall.Create"), but
            // types.name only ever stores the bare type name -- so the qualified form is tried against
            // full_name FIRST, and only falls back to a bare-name match if that finds nothing.
            //
            // Independent PR review finding (2nd round, M3): an earlier version of this fix stripped
            // straight to the bare name unconditionally, which meant a query that WAS already
            // unambiguous ("Autodesk.Revit.DB.Document.Delete") silently discarded the one piece of
            // information that made it so -- it tied at score 1000 against every OTHER Document.Delete in
            // any other namespace or add-in, which is a worse outcome than the loose tier-3 fallback the
            // fix replaced. Trying the qualified form against full_name first, and using ONLY those
            // results when it matches anything, keeps a genuinely qualified query unambiguous while still
            // letting a bare "Type.Member" query (nothing before the type name to be a namespace) work
            // exactly as it always did.
            var lastDot = queryLower.LastIndexOf('.');
            var lastSpace = queryLower.LastIndexOf(' ');
            var splitAt = Math.Max(lastDot, lastSpace);
            if (splitAt > 0 && splitAt < queryLower.Length - 1)
            {
                var typeToken = queryLower[..splitAt].Trim();
                var memberToken = queryLower[(splitAt + 1)..].Trim();

                // A bare type token (no dot at all, e.g. "Wall" in "Wall.Create") can never match
                // full_name (which is always namespace-qualified, except for a global-namespace type where
                // it equals the bare name anyway -- matchFullName still finds that case correctly) -- go
                // straight to the bare-name match rather than wasting a query on a full_name attempt that
                // can only ever miss.
                List<DiscoveryMemberRow> exactRows;
                if (typeToken.Contains('.'))
                {
                    exactRows = QueryExactTypeMember(typeToken, memberToken, namespaceFilter, matchFullName: true).ToList();
                    if (exactRows.Count == 0)
                    {
                        var typeTokenBare = typeToken[(typeToken.LastIndexOf('.') + 1)..];
                        exactRows = QueryExactTypeMember(typeTokenBare, memberToken, namespaceFilter, matchFullName: false).ToList();
                    }
                }
                else
                {
                    exactRows = QueryExactTypeMember(typeToken, memberToken, namespaceFilter, matchFullName: false).ToList();
                }

                foreach (var row in exactRows)
                {
                    if (seenMemberIds.Add(row.MemberId))
                    {
                        results.Add((row, 1000 + CoreBoost(row.IsCoreAssembly)));
                    }
                }
            }

            // Tier 2: query tokens matched across {type name, member name}, graded by IdentifierRelevance.
            //
            // Issue #65: this tier used to be binary -- all tokens or nothing, every survivor scoring a flat
            // 500. Both halves of that were defects. Rows are now admitted with a small unmatched-token
            // allowance (UnmatchedTokenAllowance) and scored across the band below, so a strong match that
            // a stray natural-language word would previously have dropped into tier 3 stays in contention,
            // and rows within the tier are ordered by relevance instead of falling through to
            // DiscoveryService's alphabetical-by-name tie-break.
            if (tokens.Length > 0)
            {
                foreach (var (row, score) in QueryTokenMatch(tokens, namespaceFilter))
                {
                    if (seenMemberIds.Add(row.MemberId))
                    {
                        results.Add((row, score));
                    }
                }
            }

            // Tier 3: FTS5 BM25 fallback.
            foreach (var (row, rank) in QueryFts(queryLower, namespaceFilter))
            {
                if (seenMemberIds.Add(row.MemberId))
                {
                    // Independent PR review finding: bm25() returns a negative value, more negative = better
                    // match. The original fold (499/(1+betterness)) had this backwards -- it mapped a
                    // STRONGER match to a LOWER score, so OrderByDescending(Score) in DiscoveryService
                    // actually surfaced the WEAKEST tier-3 hits first. betterness increasing -> normalized
                    // increasing (asymptotic toward, never reaching, tier 2's floor of 500) fixes the
                    // direction while preserving the same bounded-below-500 contract.
                    var betterness = Math.Max(0, -rank);
                    var normalized = 499.0 * betterness / (1.0 + betterness);
                    results.Add((row, normalized + CoreBoost(row.IsCoreAssembly)));
                }
            }

            return results;
        }
    }

    /// <summary>
    /// <paramref name="matchFullName"/> selects which column <paramref name="typeToken"/> is compared
    /// against -- <c>t.full_name</c> for a query that arrived already dotted/qualified (unambiguous: at
    /// most one type anywhere has a given full name), <c>t.name</c> for a bare type name (can legitimately
    /// match more than one type across namespaces/add-ins, which is why the qualified form is always tried
    /// first -- see <see cref="Search"/>'s own comment).
    /// </summary>
    private IEnumerable<DiscoveryMemberRow> QueryExactTypeMember(string typeToken, string memberToken, string? namespaceFilter, bool matchFullName)
    {
        using var cmd = _connection.CreateCommand();
        var typeColumn = matchFullName ? "t.full_name" : "t.name";
        cmd.CommandText = $"""
            SELECT m.kind, m.name, m.signature, m.summary, m.member_id, m.returns, m.params_json, t.namespace, t.full_name, a.kind
            FROM members m JOIN types t ON m.type_id = t.id JOIN assemblies a ON t.assembly_id = a.id
            WHERE t.documented = 1 AND LOWER({typeColumn}) = @typeToken AND LOWER(m.name) = @memberToken
              AND (@ns IS NULL OR t.namespace = @ns)
            """;
        cmd.Parameters.AddWithValue("@typeToken", typeToken);
        cmd.Parameters.AddWithValue("@memberToken", memberToken);
        cmd.Parameters.AddWithValue("@ns", (object?)namespaceFilter ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        return ReadMemberRows(reader);
    }

    /// <summary>
    /// How many query tokens a row may leave unmatched and still be admitted to tier 2 (issue #65). Zero
    /// for a one- or two-token query, where every token is load-bearing and dropping one would admit
    /// almost anything; one for a longer query, where natural-language phrasing routinely carries a word
    /// no API name contains -- "create sheet place view" is the reported case, in which "place" appears in
    /// no part of <c>ViewSheet.Create</c> and cost it the whole tier.
    /// </summary>
    private static int UnmatchedTokenAllowance(int tokenCount) => tokenCount >= 3 ? 1 : 0;

    /// <summary>
    /// Splits a lowercased query into the tokens the name-match and FTS tiers rank against, dropping
    /// <see cref="IdentifierRelevance.StopWords"/>, bare single characters, and duplicates.
    ///
    /// <para>Deduplication matters for correctness, not just cost: the admission test below counts matched
    /// tokens, and recall averages over them, so without it "get the element type of an element" lets a row
    /// matching only "element" count that hit twice toward both. Independent PR review finding -- the old
    /// all-AND predicate was immune to this, since an AND of identical clauses is idempotent.</para>
    ///
    /// <para>Falls back to the unfiltered tokens when filtering would leave nothing, so a query that is
    /// ALL stopwords still searches for what the caller literally typed rather than silently matching
    /// everything.</para>
    ///
    /// <para><b>De-duplicates by synonym CLASS, not by literal spelling</b> (issue #75 follow-up,
    /// independent review finding). "create" and "new" are the same <see cref="IdentifierRelevance.Synonyms"/>
    /// class; keeping them as two separate token slots meant each slot's own
    /// <see cref="IdentifierRelevance.Expand"/> included the other, so a single name word-part like "Create"
    /// satisfied BOTH slots at once and silently bought the row a free <c>UnmatchedTokenAllowance</c> seat
    /// no query actually earned. Measured live against the real corpus: "create a new transaction" admitted
    /// <c>Arc.Create</c> (sharing only "create"/"new", nothing else) into tier 2, while
    /// <c>Transaction.Transaction</c> fell out of the top 12 entirely. Collapsing to one slot per class
    /// keeps whichever literal spelling appeared FIRST -- which spelling wins does not change ranking,
    /// since <see cref="IdentifierRelevance.Credit"/> discounts synonym-derived credit either way.</para>
    /// </summary>
    private static string[] TokenizeQuery(string queryLower)
    {
        var raw = queryLower.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var filtered = raw.Where(t => t.Length > 1 && !IdentifierRelevance.StopWords.Contains(t)).ToArray();
        return DedupeBySynonymClass(filtered.Length > 0 ? filtered : raw);
    }

    private static string[] DedupeBySynonymClass(string[] tokens)
    {
        var seenClasses = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            if (seenClasses.Add(IdentifierRelevance.SynonymClassKey(token)))
            {
                result.Add(token);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Tier-2 candidates: rows whose member or declaring-type name matches enough of the query's tokens.
    ///
    /// <para>Note the deliberate asymmetry with <see cref="IdentifierRelevance"/>, which does the actual
    /// scoring: this predicate is a cheap SQL-side SUPERSET of it. A raw <c>LIKE '%token%'</c> is exactly
    /// the condition under which <c>IdentifierRelevance</c> can award a token any credit above zero, so no
    /// row that would have scored is filtered out here -- SQL decides membership, C# decides rank.</para>
    ///
    /// <para><b>The unmatched-token allowance requires a hit on the MEMBER name.</b> Independent PR review
    /// finding: without that condition, any query whose DECLARING TYPE name alone supplies enough tokens
    /// admits every member of that type at the tier-2 floor, which pushed
    /// <c>Document.NewFamilyInstance</c> to rank 27 behind properties and collection methods, and made
    /// page 1 of "set the parameter of an element" read <c>ParameterSet.Insert/Erase/Contains</c>. Since
    /// tier 3 is bounded below 500, that buries the right answer HARDER than the bug the gate was added
    /// alongside. A row still matching every token is admitted regardless, so an exact all-token match
    /// never needs to justify itself.</para>
    ///
    /// <para>Re-measured through this code path against RevitAPI 2027 (issue #76): the gate takes "create
    /// family instance" from 151 candidates to 71, and "set the parameter of an element" from 522 to 455.
    /// An earlier version of this comment said 162 and 855. Those came from a corpus reconstructed out of
    /// RevitAPI.xml during review rather than from the reflected corpus this class actually indexes -- it
    /// carries ~30,449 entries against this one's 25,933, and the discrepancy is worth naming because the
    /// same borrowed figures were what made issue #76 look like a problem with natural-language queries
    /// when it is really a problem with short ones.</para>
    /// </summary>
    private IReadOnlyList<(DiscoveryMemberRow Row, double Score)> QueryTokenMatch(string[] tokens, string? namespaceFilter)
    {
        var scored = ScoreTokenMatchCandidates(tokens, namespaceFilter);
        if (scored.Count == 0)
        {
            return Array.Empty<(DiscoveryMemberRow, double)>();
        }

        // Cap AFTER scoring, so what survives is the genuine top of the ranking rather than whatever the
        // join happened to walk first (issue #76). Sorted by score here only to pick the window; the
        // caller's own ordering (DiscoveryService, which owns the tie-breaks) still decides final rank.
        // ThenBy(Id) makes the CUT a total order, not just the display order. Independent PR review
        // finding: OrderByDescending is a stable sort over a result set pass 1 deliberately leaves
        // unordered, so among rows tied at the boundary score the survivors would be whichever SQLite
        // scanned first -- and ties at the boundary are not hypothetical, six rows share 637.5 for "id".
        // Measured to be stable in practice for a fixed database file, but it is unspecified by contract,
        // and search_functions cursors are stateless offsets that re-run the query for every page.
        var window = scored
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Id)
            .Take(_rankedDepth)
            .ToList();

        // Keyed on members.id, the primary key, NOT on member_id: nothing constrains member_id to be
        // unique (two assemblies can each declare the same fully-qualified type -- an add-in bundling a
        // copy of a core type is the realistic case), and a duplicate key here would throw out of an
        // ordinary search query. Search's own seenMemberIds dedup still collapses such a pair afterwards;
        // that is a pre-existing policy and deliberately left alone.
        var scoreById = window.ToDictionary(c => c.Id, c => c.Score);
        return MaterializeMembers(scoreById.Keys)
            .Select(m => (Row: m.Row, Score: scoreById[m.Id]))
            .ToList();
    }

    /// <summary>
    /// Pass 1 of <see cref="QueryTokenMatch"/>: every matching row, scored, reading only the five columns
    /// <see cref="IdentifierRelevance"/> and <see cref="CoreBoost"/> actually need. Deliberately unbounded
    /// -- see <see cref="TierCandidateLimit"/> for the measurements that say this is affordable.
    /// </summary>
    private List<(long Id, double Score)> ScoreTokenMatchCandidates(string[] tokens, string? namespaceFilter)
    {
        using var cmd = _connection.CreateCommand();
        var hitTerms = new List<string>();
        var memberHitTerms = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            // Issue #75: a query token is admitted through any of its IdentifierRelevance.Expand synonyms
            // ("create" through "new"), not just its literal spelling -- otherwise a short query relying
            // entirely on the synonym word (e.g. "create wall" against a fixture named "NewWall") never
            // reaches IdentifierRelevance.Score in the first place, since this predicate decides candidate
            // MEMBERSHIP and Score only decides rank among admitted rows. Each variant gets its own LIKE
            // parameter (SQLite has no array binding), OR'd together per token so the token still counts as
            // exactly one hit -- required/hits/member_hits below stay in terms of the ORIGINAL token count.
            var variants = IdentifierRelevance.Expand(tokens[i]);
            var typeOrMemberClauses = new List<string>();
            var memberOnlyClauses = new List<string>();
            for (var v = 0; v < variants.Count; v++)
            {
                var pname = $"@tok{i}_{v}";
                cmd.Parameters.AddWithValue(pname, "%" + EscapeLike(variants[v]) + "%");
                typeOrMemberClauses.Add($"LOWER(t.name) LIKE {pname} ESCAPE '\\' OR LOWER(m.name) LIKE {pname} ESCAPE '\\'");
                memberOnlyClauses.Add($"LOWER(m.name) LIKE {pname} ESCAPE '\\'");
            }

            hitTerms.Add($"(CASE WHEN ({string.Join(" OR ", typeOrMemberClauses)}) THEN 1 ELSE 0 END)");
            // A constructor's m.name IS its declaring type's name (DiscoveryReflector), so counting it here
            // would hand every constructor of a name-matching type the very type-name-only admission this
            // gate exists to refuse -- and, through ORDER BY member_hits, a guaranteed seat inside the LIMIT
            // ahead of genuine member matches. 2nd review round: 14 constructors were admitted for "set the
            // parameter of an element" that way, displacing Element.LookupParameter. Excluding them here is
            // the same premise the scoring path already applies (see the Constructor branch in Search).
            memberHitTerms.Add($"(CASE WHEN m.kind <> 'Constructor' AND ({string.Join(" OR ", memberOnlyClauses)}) THEN 1 ELSE 0 END)");
        }

        var hits = string.Join(" + ", hitTerms);
        var memberHits = string.Join(" + ", memberHitTerms);
        var required = tokens.Length - UnmatchedTokenAllowance(tokens.Length);
        cmd.Parameters.AddWithValue("@ns", (object?)namespaceFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@required", required);
        cmd.Parameters.AddWithValue("@all", tokens.Length);
        // No ORDER BY and no LIMIT, deliberately. Every previous attempt to pick survivors in SQL was
        // picking them by a proxy for the score rather than the score, and each proxy was wrong in its own
        // way: no ordering at all (SQLite's scan order, so CoreBoost was decided before Search ever ran);
        // then total hits, which is precisely the signal this tier exists to OVERRULE -- CreatePlaceholder
        // out-hits Create and must still lose, so truncating by it preserves the rows the scorer will rank
        // lowest; then member hits, which at least correlates with the final score but still cut ties
        // arbitrarily once the tied band alone overflowed the limit. Scoring first and cutting afterwards
        // (see QueryTokenMatch) removes the proxy entirely.
        cmd.CommandText = $"""
            SELECT m.id, m.kind, m.name, t.full_name, a.kind
            FROM members m JOIN types t ON m.type_id = t.id JOIN assemblies a ON t.assembly_id = a.id
            WHERE t.documented = 1 AND (@ns IS NULL OR t.namespace = @ns)
              AND ({hits}) >= @required
              AND (({hits}) >= @all OR ({memberHits}) >= 1)
            """;

        var scored = new List<(long, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var kind = reader.GetString(1);
            var name = reader.GetString(2);
            var declaringType = reader.GetString(3);
            var isCore = reader.GetString(4) == "core";

            // A constructor's Name IS its declaring type's name (DiscoveryReflector sets it that way), so
            // scoring both would count the same words twice -- once at full member weight, once at type
            // weight -- systematically inflating every constructor whose type the query mentions. Measured
            // on the real corpus (2nd review round): "set the parameter of an element" returned
            // ParameterSet's and ElementSet's CONSTRUCTORS at ranks 1-2, above Parameter.Set. Scoring the
            // type name alone is the honest reading -- a constructor contributes no name material of its
            // own.
            var isConstructor = string.Equals(kind, "Constructor", StringComparison.Ordinal);
            var relevance = IdentifierRelevance.Score(
                tokens,
                isConstructor ? string.Empty : name,
                ShortTypeName(declaringType));

            // A row the query's words do not explain AT ALL is not a tier-2 row (issue #80). Admission is
            // `LOWER(name) LIKE '%token%'` against the raw stored name, which matches across word
            // boundaries; IdentifierRelevance scores against SplitWords, which never does. So a token can
            // admit a row and then earn it nothing: SplitWords("LineWeight") is ["line","weight"], and the
            // token "lineweight" is a contiguous substring of the raw name but is in no word-part, giving
            // relevance 0. At the old floor that row scored 500.5 and therefore outranked EVERY tier-3
            // match in the corpus, however strong.
            //
            // Measured on the real corpus: "create lineweight" put 17 of its 548 rows at the tier-2 floor
            // -- Category.GetLineWeight, Category.SetLineWeight, FilledRegionType.IsValidLineWeight,
            // OverrideGraphicSettings.SetProjectionLineWeight and a dozen more -- every one of them above
            // the best tier-3 row. (16 against RevitAPI alone; the 17th is in RevitAPIUI, which the test
            // corpus only started syncing once it was made to match production. They begin around rank 35
            // rather than rank 1, because issue #79 lifted genuinely-relevant rows above them -- the
            // defect was intact, only its visibility had changed.) Dropping them lets tier 3 rank them on
            // their merits, and makes the tier boundary mean what its own comments already claim: tier 2
            // is "the query's words explain this name", and a zero score says they do not.
            //
            // Note this leaves WEAK-but-nonzero rows alone (UnitTypeId.Kilonewtons earns a prefix credit
            // for "create kilonewton"). That is arguably correct and deliberately unchanged.
            if (relevance <= 0.0)
            {
                continue;
            }

            scored.Add((id, TierTwoFloor + (TierTwoBand * relevance) + CoreBoost(isCore)));
        }

        return scored;
    }

    /// <summary>
    /// Pass 2 of <see cref="QueryTokenMatch"/>: the full row for each id that survived scoring. Split from
    /// pass 1 so the expensive columns -- <c>signature</c>, <c>summary</c>, and <c>params_json</c>, which
    /// <see cref="ReadMemberRows"/> deserializes per row -- are read only for rows a caller can actually
    /// reach, rather than for every candidate the predicate admits.
    /// </summary>
    private List<(long Id, DiscoveryMemberRow Row)> MaterializeMembers(IEnumerable<long> ids)
    {
        using var cmd = _connection.CreateCommand();
        var placeholders = new List<string>();
        var index = 0;
        foreach (var id in ids)
        {
            placeholders.Add($"@id{index}");
            cmd.Parameters.AddWithValue($"@id{index}", id);
            index++;
        }

        // m.id trails the ten columns ReadMemberRow reads by ordinal, so it can stay unaware of it.
        cmd.CommandText = $"""
            SELECT m.kind, m.name, m.signature, m.summary, m.member_id, m.returns, m.params_json, t.namespace, t.full_name, a.kind, m.id
            FROM members m JOIN types t ON m.type_id = t.id JOIN assemblies a ON t.assembly_id = a.id
            WHERE m.id IN ({string.Join(", ", placeholders)})
            """;

        var rows = new List<(long, DiscoveryMemberRow)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(10), ReadMemberRow(reader)));
        }

        return rows;
    }

    private IEnumerable<(DiscoveryMemberRow Row, double Rank)> QueryFts(string query, string? namespaceFilter)
    {
        // FTS5's MATCH operand syntax treats bare punctuation specially; the query is already reduced to a
        // token stream everywhere else in this class, so build a simple OR-of-tokens match expression rather
        // than passing the raw query straight through. Stopwords are dropped here for the same reason as in
        // the name-match tier: OR-ing in "a" or "the" matches most of the corpus on no real signal.
        var tokens = TokenizeQuery(query);
        if (tokens.Length == 0)
        {
            return Array.Empty<(DiscoveryMemberRow, double)>();
        }

        var matchExpression = string.Join(" OR ", tokens.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.kind, m.name, m.signature, m.summary, m.member_id, m.returns, m.params_json, t.namespace, t.full_name, a.kind, bm25(members_fts)
            FROM members_fts
            JOIN members m ON m.id = members_fts.rowid
            JOIN types t ON m.type_id = t.id
            JOIN assemblies a ON t.assembly_id = a.id
            WHERE members_fts MATCH @match AND t.documented = 1 AND (@ns IS NULL OR t.namespace = @ns)
            -- ORDER BY relevance, then a tie-break, and NOT by assembly kind (issue #81).
            --
            -- This clause used to lead with (a.kind != 'core'), which made kind the primary sort of a
            -- LIMITed query -- that is a filter on candidate SELECTION, not a preference applied to
            -- ranking. Once core alone supplied @limit candidates, no add-in row was considered at all,
            -- however much better its bm25. Measured on the real corpus at the production limit, with
            -- RevitAPIUI synced as the add-in and the top 50 inspected: "let the user pick an element"
            -- returned 0 add-in rows before this change and 21 after, while "prompt the user" (12),
            -- "show a dialog to the user" (23) and "user interface" (14) were identical either way. It
            -- bites only when core alone fills the budget, which is why it went unnoticed for so long.
            --
            -- PRD §08 says add-in APIs rank below core and stay fully searchable; the ranking half of
            -- that lives in CoreBoost, which adds +0.5 to a core row's SCORE after selection and
            -- expresses the preference without excluding anything. Since issue #91 the connector's own
            -- script API is indexed as an add-in, so an agent searching it by description takes this path.
            --
            -- m.id is a TIE-BREAK, not a ranking signal: bm25 ties at the boundary are common (the
            -- committed ranking snapshots show groups of 3 and 7 sharing a score), and a single-key
            -- ORDER BY under a LIMIT leaves which of them survive to SQLite's scan order. QueryTokenMatch
            -- states this invariant for tier 2 in its own comment and enforces it with ThenBy(Id); tier 3
            -- needs it for the same reason, since search_functions' cursors are stateless offsets that
            -- re-run the query for every page.
            ORDER BY bm25(members_fts), m.id
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@match", matchExpression);
        cmd.Parameters.AddWithValue("@ns", (object?)namespaceFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@limit", _rankedDepth);

        var results = new List<(DiscoveryMemberRow, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((ReadMemberRow(reader), reader.GetDouble(10)));
        }

        return results;
    }

    private static string EscapeLike(string token) => token.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static IEnumerable<DiscoveryMemberRow> ReadMemberRows(SqliteDataReader reader)
    {
        var results = new List<DiscoveryMemberRow>();
        while (reader.Read())
        {
            results.Add(ReadMemberRow(reader));
        }

        return results;
    }

    private static DiscoveryMemberRow ReadMemberRow(SqliteDataReader reader) => new()
    {
        Kind = reader.GetString(0),
        Name = reader.GetString(1),
        Signature = reader.GetString(2),
        Summary = reader.IsDBNull(3) ? null : reader.GetString(3),
        MemberId = reader.GetString(4),
        Returns = reader.IsDBNull(5) ? null : reader.GetString(5),
        Parameters = JsonSerializer.Deserialize<List<ReflectedParameter>>(reader.GetString(6)) ?? new List<ReflectedParameter>(),
        Namespace = reader.GetString(7),
        DeclaringType = reader.GetString(8),
        IsCoreAssembly = reader.GetString(9) == "core",
    };
}
