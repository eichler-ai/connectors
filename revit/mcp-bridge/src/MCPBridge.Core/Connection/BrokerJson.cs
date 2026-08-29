using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPBridge.Core.Connection;

/// <summary>
/// Thrown when broker.json exists but cannot be parsed or is missing a required field.
/// Distinct from "file not found" (BrokerDiscovery.TryDiscover reports that as
/// not-found, not an exception) -- this is specifically a malformed-file condition.
/// </summary>
public sealed class BrokerJsonParseException : Exception
{
    public BrokerJsonParseException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// The contents of broker.json (PRD §05/§10): host/port/PID/start-time for discovery, and
/// the auth token the add-in must present on the TCP handshake (PRD §10). Field names here
/// match the Go broker's actual JSON writer (singleton.go's BrokerInfo) verbatim: "host",
/// "port", "pid", "started_at", "token".
/// </summary>
public sealed class BrokerJson
{
    public string Host { get; }
    public int Port { get; }
    public int Pid { get; }
    public DateTimeOffset StartedAt { get; }
    public string Token { get; }

    private BrokerJson(string host, int port, int pid, DateTimeOffset startedAt, string token)
    {
        Host = host;
        Port = port;
        Pid = pid;
        StartedAt = startedAt;
        Token = token;
    }

    public static BrokerJson Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BrokerJsonParseException("broker.json is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            var host = RequireNonEmptyString(root, "host");
            var port = RequireInt(root, "port");
            var pid = RequireInt(root, "pid");
            var startedAt = RequireDateTimeOffset(root, "started_at");
            var token = RequireNonEmptyString(root, "token");

            return new BrokerJson(host, port, pid, startedAt, token);
        }
    }

    private static int RequireInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new BrokerJsonParseException($"broker.json is missing required numeric field '{name}'.");
        }

        // TryGetInt32, not GetInt32: a numeric value that isn't a 32-bit integer (a float, or a
        // number out of range) made GetInt32 throw FormatException -- which is not
        // BrokerJsonParseException, so it escaped BrokerDiscovery.TryDiscover's catch entirely and,
        // with nothing above it on the connection thread catching either, could take down the whole
        // Revit process on a malformed broker.json (v1 integrated review).
        if (!value.TryGetInt32(out var parsed))
        {
            throw new BrokerJsonParseException($"broker.json field '{name}' is not a valid 32-bit integer.");
        }

        return parsed;
    }

    private static DateTimeOffset RequireDateTimeOffset(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new BrokerJsonParseException($"broker.json is missing required field '{name}'.");
        }

        if (!value.TryGetDateTimeOffset(out var parsed))
        {
            throw new BrokerJsonParseException($"broker.json field '{name}' is not a valid ISO-8601 timestamp.");
        }

        return parsed;
    }

    private static string RequireNonEmptyString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new BrokerJsonParseException($"broker.json is missing required non-empty field '{name}'.");
        }

        return value.GetString()!;
    }
}
