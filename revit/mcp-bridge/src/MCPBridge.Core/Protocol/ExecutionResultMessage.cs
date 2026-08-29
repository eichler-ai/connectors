using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Execution;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// Serializes an execution outcome (an <see cref="ExecutionRecord"/>, a Busy pointer, or an
/// instance-unrecoverable refusal) into the wire response shape the Go broker's execution.Result struct
/// expects (mcp-server/internal/execution/execution.go): {"jsonrpc":"2.0","id":&lt;echoed&gt;,"result":
/// {"status","execution_id","output","notices","error"}}.
///
/// <para>
/// <b>Output field mapping</b> (flagged as an open question in the cross-PR review; resolved here, revisit
/// if wrong): the Go side's Result.Output is documented (PRD §06 step 4) as "stdout captured into the
/// result" -- a single string field, with no separate slot for a script's own *return value*
/// (<see cref="ScriptExecutionOutcome.ReturnValue"/>). Roslyn C# scripting scripts are frequently a bare
/// trailing expression whose value IS the answer an agent wants (e.g. a script that's just
/// `doc.Title`, with no Console.Write at all) -- silently dropping ReturnValue would make the single most
/// common trivial-script shape look like it produced nothing. This class's mapping: Output = StdOut, with
/// the return value's display string (formatted at completion time on the UI thread -- see
/// RequestDispatcher.SafeFormatReturnValue; this class only ever sees the string) appended after it,
/// separated by a blank line for readability when both are present. This is a judgment call the wire
/// contract itself doesn't specify;
/// if the broker/agent side later wants the return value surfaced as a separate structured field instead
/// of folded into Output textually, that's a wire-shape change on both sides, not just this method.
/// </para>
///
/// <para>
/// The "busy" and "unrecoverable" status values need special handling: "busy" is deliberately not an
/// <see cref="ExecutionStatus"/> member (see that enum's own doc comment -- it's a response shape, not a
/// state stored on an execution), so <see cref="Busy"/> builds the wire status string directly rather than
/// going through an <see cref="ExecutionRecord"/> at all (there is no record for the execution a Busy
/// response points at that wasn't already the one already in flight). <see cref="FromInstanceUnrecoverable"/>
/// covers the case where a *new* execute_script is rejected outright because the whole instance already
/// latched unrecoverable (<see cref="ExecuteOutcomeKind.InstanceUnrecoverable"/>) -- no
/// <see cref="ExecutionRecord"/> was ever created for that call, so it can't go through
/// <see cref="FromRecord"/> either.
/// </para>
/// </summary>
public static class ExecutionResultMessage
{
    private sealed class ResultDto
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("execution_id")]
        public string ExecutionId { get; set; } = "";

        [JsonPropertyName("output")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Output { get; set; }

        [JsonPropertyName("notices")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<DiagnosticRecord>? Notices { get; set; }

        [JsonPropertyName("files")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PublishedFileRecord>? Files { get; set; }

        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DiagnosticRecord? Error { get; set; }
    }

    private sealed class Envelope
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public JsonElement Id { get; set; }

        [JsonPropertyName("result")]
        public ResultDto Result { get; set; } = new();
    }

    /// <summary>
    /// Builds the wire result for a pending/running/success/error/cancelled/unrecoverable execution's
    /// current state. extraNotices (PRD §07 v1 window-inventory fallback) are merged into this one
    /// response's notices[] only -- deliberately never written back onto <paramref name="record"/>,
    /// which stays the persistent, ExecutionManager-owned source of truth.
    /// </summary>
    public static string FromRecord(JsonElement id, ExecutionRecord record, IReadOnlyList<DiagnosticRecord>? extraNotices = null)
    {
        List<DiagnosticRecord>? notices = null;
        if (record.Notices.Count > 0 || (extraNotices?.Count ?? 0) > 0)
        {
            notices = new List<DiagnosticRecord>(record.Notices);
            if (extraNotices is not null)
            {
                notices.AddRange(extraNotices);
            }
        }

        var dto = new ResultDto
        {
            Status = ToWireStatus(record.Status),
            ExecutionId = record.ExecutionId,
            Output = ComposeOutput(record),
            Notices = notices,
            Files = record.Files.Count > 0 ? new List<PublishedFileRecord>(record.Files) : null,
            Error = record.Error,
        };

        return Serialize(id, dto);
    }

    /// <summary>Builds the wire result for ExecuteOutcomeKind.Busy: points at the execution already in flight, no output/error.</summary>
    public static string Busy(JsonElement id, string existingExecutionId) =>
        Serialize(id, new ResultDto { Status = "busy", ExecutionId = existingExecutionId });

    /// <summary>Builds the wire result for ExecuteOutcomeKind.InstanceUnrecoverable: no execution was ever created for this call.</summary>
    public static string FromInstanceUnrecoverable(JsonElement id, DiagnosticRecord diagnostic) =>
        Serialize(id, new ResultDto { Status = "unrecoverable", ExecutionId = "", Error = diagnostic });

    private static string Serialize(JsonElement id, ResultDto dto)
    {
        var envelope = new Envelope { Id = id, Result = dto };
        return JsonSerializer.Serialize(envelope, WireJson.Compact);
    }

    private static string? ComposeOutput(ExecutionRecord record)
    {
        if (record.Status != ExecutionStatus.Completed)
        {
            // Error/Cancelled/Pending/Running: whatever stdout was captured before the execution stopped
            // (if any), no return value to fold in (Completed is the only status with one).
            return string.IsNullOrEmpty(record.StdOut) ? null : record.StdOut;
        }

        var stdOut = record.StdOut ?? "";
        if (record.Result is null)
        {
            return stdOut.Length == 0 ? null : stdOut;
        }

        // record.Result is already the formatted display string -- formatting happens at completion
        // time on the UI thread (RequestDispatcher.SafeFormatReturnValue), never here on the TCP
        // thread where a Revit-object ToString() would run off the API context (v1 integrated review).
        var formatted = record.Result;
        if (stdOut.Length == 0)
        {
            return formatted;
        }

        // Trim any trailing newline stdOut already ends with (the common case -- Console.WriteLine)
        // before inserting the blank-line separator, so the two don't compound into three-plus newlines.
        return stdOut.TrimEnd('\n', '\r') + "\n\n" + formatted;
    }

    // Deliberately a separate small mapping rather than reusing ExecutionStatus's own
    // [WireEnumName] wire values via JsonSerializer -- "busy" needs to share this same wire
    // vocabulary (see the class doc comment) but is deliberately not a member of ExecutionStatus, so a
    // single ResultDto.Status : string field (rather than ExecutionStatus) is what lets one type cover
    // both. Keep in sync with ExecutionStatus's [WireEnumName] attributes if either changes.
    private static string ToWireStatus(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Pending => "pending",
        ExecutionStatus.Running => "running",
        ExecutionStatus.Completed => "success",
        ExecutionStatus.Error => "error",
        ExecutionStatus.Cancelled => "cancelled",
        ExecutionStatus.Unrecoverable => "unrecoverable",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
