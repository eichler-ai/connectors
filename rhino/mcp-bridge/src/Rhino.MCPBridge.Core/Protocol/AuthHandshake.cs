using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rhino.MCPBridge.Core.Diagnostics;

namespace Rhino.MCPBridge.Core.Protocol;

/// <summary>
/// The plug-in's side of PRD §13's auth rule, inverted from Revit's because the server dials in: the
/// FIRST line a connecting server sends must be a JSON-RPC request with method "auth" carrying this
/// instance's token (from instances/&lt;pid&gt;.json) and a role. Anything else — a different method, a
/// notification, a bad token, an unknown role, malformed JSON — is answered with one error record
/// and the connection is closed. Pure: takes the line, returns the verdict and the response to write.
/// </summary>
public static class AuthHandshake
{
    public const string RoleAgentClient = "agent-client";

    public sealed class Result
    {
        public bool Accepted { get; }
        /// <summary>The JSON line to write back (an ok result or an error), never null.</summary>
        public string ResponseJson { get; }
        /// <summary>Set when accepted: the server's self-declared id, used for `last_run` attribution (PRD §05).</summary>
        public string? ServerId { get; }
        public string? ServerVersion { get; }
        /// <summary>Set when rejected: the kebab-case code, for the connection log.</summary>
        public string? RejectionCode { get; }

        internal Result(bool accepted, string responseJson, string? serverId, string? serverVersion, string? rejectionCode)
        {
            Accepted = accepted;
            ResponseJson = responseJson;
            ServerId = serverId;
            ServerVersion = serverVersion;
            RejectionCode = rejectionCode;
        }
    }

    public static Result Evaluate(string firstLine, string expectedToken)
    {
        JsonRpcRequest request;
        try
        {
            request = JsonRpcRequest.Parse(firstLine);
        }
        catch (Exception ex)
        {
            return Reject(default, "auth-required", $"the first message on a new connection must be a JSON-RPC request with method \"auth\"; it could not be parsed as one: {ex.Message}");
        }

        if (request.Method != "auth")
        {
            return Reject(request.Id, "auth-required", $"the first message on a new connection must be a JSON-RPC request with method \"auth\" and a valid token; got method \"{request.Method}\"");
        }

        string token, role;
        try
        {
            token = request.GetRequiredString("token");
            role = request.GetRequiredString("role");
        }
        catch (JsonRpcParamException ex)
        {
            return Reject(request.Id, "auth-malformed", $"auth params could not be decoded: {ex.Message}");
        }

        if (!TokensMatch(expectedToken, token))
        {
            return Reject(request.Id, "auth-invalid-token", "the presented token does not match this instance's token");
        }

        if (role != RoleAgentClient)
        {
            return Reject(request.Id, "auth-invalid-role", $"unknown role \"{role}\"; expected \"{RoleAgentClient}\"");
        }

        var ok = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request.Id, result = new { ok = true } }, WireJson.Compact);
        return new Result(true, ok, request.GetOptionalString("server_id"), request.GetOptionalString("server_version"), null);
    }

    private static Result Reject(JsonElement id, string code, string message)
    {
        var record = DiagnosticRecord.Create(DiagnosticSeverity.Error, code, DiagnosticSource.Connection, message,
            detail: null,
            remedy: new[] { "reconnect and send a valid auth request as the very first message; the token is in this instance's instances/<pid>.json" });
        var idToUse = id.ValueKind is JsonValueKind.Undefined ? JsonSerializer.SerializeToElement((object?)null) : id;
        return new Result(false, JsonRpcErrorMessage.ToJson(idToUse, JsonRpcErrorCode.InvalidRequest, message, record), null, null, code);
    }

    /// <summary>Constant-time comparison; an empty expected token never matches (a plug-in that failed to mint one must not be open).</summary>
    internal static bool TokensMatch(string expected, string presented)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
