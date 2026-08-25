using MCPBridge.Core.Diagnostics;

namespace MCPBridge.Core.Execution;

/// <summary>Result of ExecutionManager.Start (PRD §06): a fresh Pending execution, a pointer at the one already in flight, or a hard "this instance is dead" refusal.</summary>
public sealed class ExecuteOutcome
{
    public ExecuteOutcomeKind Kind { get; }
    public ExecutionRecord? Record { get; }
    public DiagnosticRecord? Diagnostic { get; }

    private ExecuteOutcome(ExecuteOutcomeKind kind, ExecutionRecord? record, DiagnosticRecord? diagnostic)
    {
        Kind = kind;
        Record = record;
        Diagnostic = diagnostic;
    }

    public static ExecuteOutcome Started(ExecutionRecord record) => new(ExecuteOutcomeKind.Started, record, null);

    public static ExecuteOutcome Busy(ExecutionRecord existing) => new(ExecuteOutcomeKind.Busy, existing, null);

    public static ExecuteOutcome InstanceUnrecoverable(DiagnosticRecord diagnostic) =>
        new(ExecuteOutcomeKind.InstanceUnrecoverable, null, diagnostic);
}
