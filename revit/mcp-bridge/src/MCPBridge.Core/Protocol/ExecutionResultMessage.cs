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
/// {"status","execution_id","output","return_value","notices","error"}}.
///
/// <para>
/// <b>Output field mapping.</b> The Go side's Result.Output is documented (PRD §06 step 4) as "stdout
/// captured into the result", and a script's own *return value*
/// (<see cref="ScriptExecutionOutcome.ReturnValue"/>) is a different thing: Roslyn C# scripting scripts
/// are frequently a bare trailing expression whose value IS the answer an agent wants (e.g. a script
/// that's just `doc.Title`, with no Console.Write at all), so dropping it is not an option either.
/// This class originally folded both into <c>output</c>, separated by a blank line, and flagged that as a
/// judgment call to revisit if the two ever needed telling apart. Issue #117 is that case, reported live:
/// Revit writes to the process console during a script (`PlayerServer:Warning:No subscriber registered.`),
/// ScriptConsoleCapture correctly captures it as stdout, and an agent reading <c>output</c> saw two lines
/// of Revit's internal chatter ahead of the value its script returned with nothing marking the boundary.
/// So the fold is gone: <c>output</c> is stdout and only stdout -- its documented meaning -- and the
/// return value's display string (formatted at completion time on the UI thread by
/// ReturnValueFormatter, via RequestDispatcher.SafeFormatReturnValue; this class only ever sees the
/// string) has its own <c>return_value</c> field. Both ends changed together: execution.Result and
/// mcpserver.ExecutionOut on the Go side, and revit/docs/tools.md.
/// </para>
///
/// <para>
/// <b>Version skew</b> is worth stating rather than discovering: an OLD broker against a NEW add-in drops
/// return_value on the floor (its Result struct has no such field), so a script's returned value goes
/// missing entirely rather than appearing in the wrong place. A new broker against an old add-in sees no
/// return_value and an <c>output</c> that still has the value folded in -- degraded, not broken. The
/// installer ships both halves together, which is what keeps this a note and not a compatibility scheme.
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

        [JsonPropertyName("return_value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReturnValue { get; set; }

        [JsonPropertyName("notices")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<DiagnosticRecord>? Notices { get; set; }

        [JsonPropertyName("files")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PublishedFileRecord>? Files { get; set; }

        /// <summary>#146 Phase 2. Absent, not zeroed, when the run changed nothing -- the Go side's Result.Mutations is a pointer for the same reason.</summary>
        [JsonPropertyName("mutations")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MutationReport? Mutations { get; set; }

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
            Output = string.IsNullOrEmpty(record.StdOut) ? null : record.StdOut,
            // Non-null only on a Completed record: MarkCompleted is the sole writer of
            // ExecutionRecord.Result, so no status check belongs here -- one would be unfalsifiable
            // defensive code duplicating an invariant that record already owns. It is already the
            // formatted display string: formatting happens at completion time on the UI thread
            // (RequestDispatcher.SafeFormatReturnValue), never here on the TCP thread, where a Revit
            // object's ToString() would run off the API context (v1 integrated review).
            ReturnValue = record.Result,
            Notices = notices,
            Files = record.Files.Count > 0 ? new List<PublishedFileRecord>(record.Files) : null,
            Mutations = record.Mutations,
            Error = record.Error,
        };

        return Serialize(id, dto);
    }

    /// <summary>
    /// #146 Phase 2c: the result of an operation that is not an execution (undo/redo) but shares the wire
    /// shape -- status, an optional mutation report describing what changed, notices, and an error. No
    /// execution_id: nothing to poll.
    /// </summary>
    public static string Adhoc(JsonElement id, string status, MutationReport? mutations, IReadOnlyList<DiagnosticRecord>? notices, DiagnosticRecord? error) =>
        Serialize(id, new ResultDto
        {
            Status = status,
            ExecutionId = "",
            Mutations = mutations,
            Notices = notices is { Count: > 0 } ? new List<DiagnosticRecord>(notices) : null,
            Error = error,
        });

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
