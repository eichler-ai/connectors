using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCPBridge.Core.Diagnostics;

namespace MCPBridge.Core.Connection;

/// <summary>
/// The add-in's own persisted settings: <c>%LOCALAPPDATA%\Connectors\Revit\bridge-config.json</c>
/// (issue #185). Today it holds exactly one decision -- which broker topology to dial (PRD §05's
/// local/remote table) and, for remote, the shared drive's UNC root -- written by the ribbon's
/// broker-mode switch and read at OnStartup ahead of the <c>MCPBRIDGE_BROKER_MODE</c> environment
/// variables (see <see cref="BrokerModeResolver"/> for the precedence and why it is that way round).
///
/// <para>Deliberately the LOCAL per-machine directory in every mode, the same place
/// <c>startup-errors.log</c>/<c>connection.log</c> live: this file is what decides whether the shared
/// drive is consulted at all, so it cannot itself live on the shared drive.</para>
///
/// <para>Internal because in this assembly <c>public</c> means script-reachable; the AddIn reaches it
/// via InternalsVisibleTo. A script gains no capability from it (it can already write any file), but
/// there is equally no reason to hand it a typed handle on the add-in's own configuration.</para>
/// </summary>
internal sealed class BridgeConfig
{
    /// <summary>Property values for <see cref="BrokerMode"/>; compared case-insensitively on read.</summary>
    public const string LocalMode = "local";
    public const string RemoteMode = "remote";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A hand-edited file with a trailing comma or a // note should still load: this is a
        // settings file a developer may well touch by hand, not a wire format.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>"local" or "remote" (case-insensitive); null/absent means "not decided here" and lets
    /// the environment-variable fallback and the Local default apply.</summary>
    public string? BrokerMode { get; set; }

    /// <summary>UNC root of the shared drive for remote mode (\\host\share, per PRD §09 -- never a
    /// mapped drive letter). Kept when switching back to local so the next switch to remote can offer
    /// it again as the default.</summary>
    public string? SharedRoot { get; set; }

    /// <summary>Convenience for callers that only need the yes/no: does this file say "remote"?</summary>
    public bool IsRemote => string.Equals(BrokerMode, RemoteMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>The one production location, beside broker.json's LOCAL-mode home.</summary>
    public static string DefaultPath()
        => Path.Combine(BrokerDiscoveryOptions.Local().ConnectorRoot, "bridge-config.json");

    /// <summary>
    /// Reads the file at <paramref name="path"/>. A missing file is the normal first-run case and
    /// yields a null <see cref="LoadResult.Config"/> with no diagnostic. A present-but-unusable file
    /// (malformed JSON, unreadable) ALSO yields null -- so the resolver falls back exactly as if the
    /// file were absent, per the same never-fail-OnStartup-over-a-topology-setting rule
    /// <c>BuildDiscoveryOptions</c> has always followed -- but carries a §01 record so the fallback is
    /// visible in startup-errors.log rather than silent (a corrupt config that quietly reverted the
    /// add-in to local mode would reproduce the very "connected to the wrong broker with no evidence
    /// why" symptom #185 was filed over).
    /// </summary>
    public static LoadResult Load(string path)
    {
        if (!File.Exists(path))
        {
            return new LoadResult(null, null);
        }

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new LoadResult(null, null); // an empty file is "nothing decided", not corruption.
            }

            var config = JsonSerializer.Deserialize<BridgeConfig>(json, SerializerOptions);
            return new LoadResult(config, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var diagnostic = DiagnosticRecord.Create(
                DiagnosticSeverity.Warning,
                "bridge-config-unreadable",
                DiagnosticSource.Connection,
                $"bridge-config.json at {path} could not be read or parsed ({ex.GetType().Name}: {ex.Message}); ignoring it and resolving broker mode from the environment/default instead.",
                detail: new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["exception_type"] = ex.GetType().FullName,
                },
                remedy: new[]
                {
                    "Fix or delete the file, then use the ribbon's broker-mode switch to rewrite it.",
                });
            return new LoadResult(null, diagnostic);
        }
    }

    /// <summary>
    /// Writes this config to <paramref name="path"/>, creating the directory if needed. Written to a
    /// sibling temp file and moved into place so a crash mid-write can never leave a half-written
    /// file for the next OnStartup to trip over (which <see cref="Load"/> would tolerate, but as a
    /// silent revert to local -- the outcome this whole file exists to make explicit).
    /// </summary>
    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, SerializerOptions);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>Outcome of <see cref="Load"/>: the parsed config (null when absent or unusable) and,
    /// for the unusable case only, the record saying why.</summary>
    public sealed record LoadResult(BridgeConfig? Config, DiagnosticRecord? Diagnostic);
}
