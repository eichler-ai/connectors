using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MCPBridge.Core.Discovery;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// The committed ranking regression corpus (issue #82).
///
/// <para>Why it exists: <c>search_functions</c>' ranking has had three rounds of changes, and two of the
/// three shipped a locally-correct change that broke sibling queries. Both times the breakage was found
/// by an independent reviewer building a query set from scratch, never by the suite -- which was green
/// throughout. Three separate throwaway corpora (20, 23 and 71 queries) were built and discarded across
/// one PR pair, so every round started from nothing and their numbers survive only as prose in commit
/// messages.</para>
///
/// <para>The gap was never rigour. It was coverage of queries nobody thought to try. So the corpus is
/// committed, it runs against the REAL reflected corpus (~26k members, via
/// <see cref="RealRevitApiLoader"/> -- not a reconstruction from RevitAPI.xml, which has ~30k entries and
/// produced counts wrong by up to 60% when someone last tried that shortcut), and its output is
/// snapshotted so a ranking change arrives as a reviewable git diff instead of a number in a commit
/// message.</para>
///
/// <para>Two different jobs, deliberately separated:</para>
/// <list type="bullet">
/// <item><description><see cref="ExpectedAnswersAreFound"/> ASSERTS. Only for queries with a defensible
/// right answer -- a past defect and the answer its fix established.</description></item>
/// <item><description><see cref="RankingSnapshotIsUnchanged"/> OBSERVES. It fails on any movement, which
/// is the point: someone has to look at what moved and re-bless it. Regenerate with
/// <c>MCPBRIDGE_UPDATE_RANKING_SNAPSHOT=1</c>.</description></item>
/// </list>
///
/// <para>Snapshots are per Revit version, because the corpora genuinely differ (2027 has 32,977
/// documented summaries against 2025's 31,216) and this project multi-targets precisely so both are
/// exercised. A single shared snapshot would be wrong for one of them.</para>
/// </summary>
public class RankingCorpusTests
{
    private const int SnapshotDepth = 10;

    private sealed record CorpusRow(string Query, string? Expected, int MaxRank, string Note);

    [Fact]
    public void CorpusFileIsPresentAndNonTrivial()
    {
        var rows = LoadCorpus();

        // A parse that silently yields nothing would make both tests below pass while checking nothing --
        // the exact shape caveats.md warns about. The bound is deliberately well under the real count so
        // it does not become an obstacle to trimming a query, but far enough above zero to catch a broken
        // parse or a fixture that failed to copy to the output directory.
        Assert.True(rows.Count >= 50, $"only {rows.Count} corpus rows parsed; issue #82 asks for 50-80");
        Assert.True(rows.Any(r => r.Expected is not null), "no row carries an expectation");
    }

    /// <summary>
    /// The rows with a defensible right answer. Each one is a defect that was reported, diagnosed and
    /// fixed; the assertion is that it stays fixed.
    /// </summary>
    [Fact]
    public void ExpectedAnswersAreFound()
    {
        var loaded = RealRevitApiLoader.TryLoad();
        if (loaded is null)
        {
            return; // No Revit for this TFM; RealRevitApiLoaderTests fails if that is wrong.
        }

        using var context = loaded.Value.Context;
        using var cache = BuildCache(loaded.Value.Assembly, TryLoadUi(loaded.Value.Context, loaded.Value.Assembly));
        var service = new DiscoveryService(cache);

        var failures = new List<string>();
        foreach (var row in LoadCorpus().Where(r => r.Expected is not null))
        {
            var ranked = Rank(service, row.Query);
            var index = ranked.FindIndex(m => string.Equals(m, row.Expected, StringComparison.Ordinal));

            if (index < 0)
            {
                failures.Add($"'{row.Query}' -> {row.Expected} NOT FOUND in top {SnapshotDepth}. {row.Note}");
            }
            else if (index + 1 > row.MaxRank)
            {
                failures.Add($"'{row.Query}' -> {row.Expected} at rank {index + 1}, wanted <= {row.MaxRank}. {row.Note}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Fails on ANY ranking movement across the whole corpus, and that is the feature rather than a
    /// nuisance: it is the diff instrument issue #82 asks for. A ranking change should be reviewed by
    /// looking at what moved, not by trusting that the queries someone happened to try still work.
    ///
    /// <para>Re-bless with <c>MCPBRIDGE_UPDATE_RANKING_SNAPSHOT=1</c> and commit the resulting diff, which
    /// is then the evidence in the PR.</para>
    /// </summary>
    [Fact]
    public void RankingSnapshotIsUnchanged()
    {
        var loaded = RealRevitApiLoader.TryLoad();
        if (loaded is null)
        {
            return;
        }

        using var context = loaded.Value.Context;
        using var cache = BuildCache(loaded.Value.Assembly, TryLoadUi(loaded.Value.Context, loaded.Value.Assembly));
        var service = new DiscoveryService(cache);

        var actual = new StringBuilder();
        actual.Append("# Generated by RankingCorpusTests. Regenerate: MCPBRIDGE_UPDATE_RANKING_SNAPSHOT=1\n");
        actual.Append($"# Revit {RealRevitApiLoader.RevitVersionForThisTargetFramework}, top {SnapshotDepth} per query.\n");

        foreach (var row in LoadCorpus())
        {
            actual.Append('\n').Append("QUERY ").Append(row.Query).Append('\n');
            var ranked = RankWithScores(service, row.Query);
            if (ranked.Count == 0)
            {
                actual.Append("  (no results)\n");
                continue;
            }

            for (var i = 0; i < ranked.Count; i++)
            {
                // Scores are rounded to ONE decimal on purpose. Full precision turns an inconsequential
                // scoring tweak into hundreds of diff lines and trains everyone to re-bless without
                // reading; one decimal still separates the tiers (1000 / 500-749 / <500), which is the
                // thing worth noticing.
                actual.Append("  ")
                      .Append((i + 1).ToString("00", CultureInfo.InvariantCulture)).Append("  ")
                      .Append(ranked[i].Score.ToString("F1", CultureInfo.InvariantCulture).PadLeft(7)).Append("  ")
                      .Append(ranked[i].Member).Append('\n');
            }
        }

        var snapshotPath = SnapshotPath();
        if (Environment.GetEnvironmentVariable("MCPBRIDGE_UPDATE_RANKING_SNAPSHOT") == "1")
        {
            var sourcePath = SourceSnapshotPath();
            Assert.True(
                Directory.Exists(Path.GetDirectoryName(sourcePath)),
                $"cannot regenerate: source fixture directory not reachable at {sourcePath}. Run the " +
                "update from a checkout, not from a copied output folder.");

            File.WriteAllText(sourcePath, actual.ToString());
            return;
        }

        Assert.True(
            File.Exists(snapshotPath),
            $"No ranking snapshot at {snapshotPath}. Generate one with MCPBRIDGE_UPDATE_RANKING_SNAPSHOT=1 " +
            "and commit it -- see issue #82.");

        var expected = File.ReadAllText(snapshotPath);
        if (string.Equals(Normalize(expected), Normalize(actual.ToString()), StringComparison.Ordinal))
        {
            return;
        }

        Assert.Fail(SummariseDrift(expected, actual.ToString(), snapshotPath));
    }

    /// <summary>
    /// A readable account of what moved. A raw string-equality failure on a 900-line snapshot is unusable,
    /// and an unusable failure message is how a snapshot test turns into something people re-bless blind.
    /// </summary>
    private static string SummariseDrift(string expected, string actual, string snapshotPath)
    {
        var before = ParseSnapshot(expected);
        var after = ParseSnapshot(actual);

        var lines = new List<string>
        {
            $"Ranking moved against {Path.GetFileName(snapshotPath)}.",
            "Review what moved, then re-bless with MCPBRIDGE_UPDATE_RANKING_SNAPSHOT=1 and commit the diff.",
            "",
        };

        var changedTop1 = new List<string>();
        var changedElsewhere = new List<string>();

        foreach (var query in before.Keys.Union(after.Keys).OrderBy(q => q, StringComparer.Ordinal))
        {
            before.TryGetValue(query, out var b);
            after.TryGetValue(query, out var a);
            b ??= new List<string>();
            a ??= new List<string>();

            if (b.SequenceEqual(a, StringComparer.Ordinal))
            {
                continue;
            }

            var bTop = b.FirstOrDefault() ?? "(none)";
            var aTop = a.FirstOrDefault() ?? "(none)";
            if (!string.Equals(bTop, aTop, StringComparison.Ordinal))
            {
                changedTop1.Add($"  '{query}'\n      was: {bTop}\n      now: {aTop}");
            }
            else
            {
                changedElsewhere.Add($"  '{query}'");
            }
        }

        lines.Add($"TOP HIT CHANGED for {changedTop1.Count} quer{(changedTop1.Count == 1 ? "y" : "ies")}:");
        lines.AddRange(changedTop1.Count == 0 ? new[] { "  (none)" } : changedTop1.Take(25));
        if (changedTop1.Count > 25)
        {
            lines.Add($"  ... and {changedTop1.Count - 25} more");
        }

        lines.Add("");
        lines.Add($"Order changed below rank 1 for {changedElsewhere.Count} quer{(changedElsewhere.Count == 1 ? "y" : "ies")}:");
        lines.AddRange(changedElsewhere.Count == 0 ? new[] { "  (none)" } : changedElsewhere.Take(25));
        if (changedElsewhere.Count > 25)
        {
            lines.Add($"  ... and {changedElsewhere.Count - 25} more");
        }

        return string.Join("\n", lines);
    }

    /// <summary>query -> ordered member ids. Scores are dropped: a pure score shift with no reordering is
    /// not what a reader of this message needs to see, and it would drown the reorderings that matter.</summary>
    private static Dictionary<string, List<string>> ParseSnapshot(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("QUERY ", StringComparison.Ordinal))
            {
                current = line["QUERY ".Length..];
                result[current] = new List<string>();
            }
            else if (current is not null && line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("  (", StringComparison.Ordinal))
            {
                var parts = line.Split("  ", StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    result[current].Add(parts[^1].Trim());
                }
            }
        }

        return result;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static List<string> Rank(DiscoveryService service, string query) =>
        RankWithScores(service, query).Select(r => r.Member).ToList();

    private static List<(string Member, double Score)> RankWithScores(DiscoveryService service, string query)
    {
        // Through DiscoveryService, not DiscoveryCache.Search: the service is what applies the final
        // ordering an agent actually sees (score, then callable-first, then name, then member id), and
        // ranking a corpus against a different sort than production uses would measure the wrong thing.
        var result = service.SearchFunctions(query, namespaceFilter: null, cursor: null, topN: SnapshotDepth);
        return result.Results
            .Select(r => ($"{r.Member.DeclaringType}.{r.Member.Name}", r.Score))
            .ToList();
    }

    /// <summary>
    /// Syncs BOTH core assemblies, matching <c>BridgeHost.CollectAssembliesToSync</c>, which registers
    /// RevitAPI and RevitAPIUI together as <c>"core"</c>.
    ///
    /// <para>An earlier version synced RevitAPI alone, which quietly made the corpus a weaker instrument
    /// than it looked: every <c>Autodesk.Revit.UI</c> member was absent, so a query like "prompt the user"
    /// or "let the user pick an element" ranked against a corpus an agent never sees. A regression corpus
    /// whose corpus differs from production measures the wrong thing.</para>
    /// </summary>
    private static DiscoveryCache BuildCache(System.Reflection.Assembly revitApi, System.Reflection.Assembly? revitApiUi)
    {
        var cache = new DiscoveryCache(":memory:");
        cache.Sync(revitApiUi is null
            ? new[] { ("core", revitApi) }
            : new[] { ("core", revitApi), ("core", revitApiUi) });
        return cache;
    }

    /// <summary>RevitAPIUI from the same install, loaded into the same metadata context; null if absent.</summary>
    private static System.Reflection.Assembly? TryLoadUi(
        System.Reflection.MetadataLoadContext context, System.Reflection.Assembly core)
    {
        var uiPath = Path.Combine(Path.GetDirectoryName(core.Location)!, "RevitAPIUI.dll");
        return File.Exists(uiPath) ? context.LoadFromAssemblyPath(uiPath) : null;
    }

    private static string SnapshotFileName =>
        $"ranking-snapshot-{RealRevitApiLoader.RevitVersionForThisTargetFramework}.txt";

    /// <summary>Where the snapshot is READ from: the copy deployed beside the test assembly.</summary>
    private static string SnapshotPath() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", SnapshotFileName);

    /// <summary>
    /// Where a regenerated snapshot is WRITTEN: the source tree, not the build output.
    ///
    /// <para>Without this, <c>MCPBRIDGE_UPDATE_RANKING_SNAPSHOT=1</c> would rewrite the copy under
    /// <c>bin/</c> and report success while the committed file never changed -- the next build would
    /// overwrite the "update" from source and the drift would silently return. The whole value of this
    /// corpus is that a ranking change lands as a reviewable git diff, so writing anywhere but the source
    /// tree defeats it.</para>
    ///
    /// <para><see cref="System.Runtime.CompilerServices.CallerFilePathAttribute"/> resolves at compile
    /// time to this file, which is the only reliable way to find the source directory from a test host
    /// whose working directory is the output folder.</para>
    /// </summary>
    private static string SourceSnapshotPath(
        [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Fixtures", SnapshotFileName);

    private static List<CorpusRow> LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ranking-corpus.tsv");
        Assert.True(File.Exists(path), $"ranking corpus not found at {path}");

        var rows = new List<CorpusRow>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // Tolerant by design: a coverage row is often just a query with no tabs at all, and requiring
            // trailing empty columns would make the file tedious to extend -- which is how a corpus stops
            // being extended.
            var fields = line.Split('\t');
            var expected = fields.Length > 1 && fields[1].Trim().Length > 0 ? fields[1].Trim() : null;
            var maxRank = SnapshotDepth;
            if (expected is not null && fields.Length > 2 && int.TryParse(fields[2].Trim(), out var parsed))
            {
                maxRank = parsed;
            }

            rows.Add(new CorpusRow(fields[0].Trim(), expected, maxRank, fields.Length > 3 ? fields[3].Trim() : ""));
        }

        return rows;
    }
}
