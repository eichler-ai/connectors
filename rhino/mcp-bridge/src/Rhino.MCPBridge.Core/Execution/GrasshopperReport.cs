using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// The Grasshopper solve report a run carries when at least one solution ended during it (PRD §10). Built
/// by the adapter from Grasshopper's own solution events and per-object runtime messages; Core only shapes
/// it for the wire. Errors are NOT auto-resolved — a component in a failed phase is reported, and whether
/// that fails the run is the script's call (a definition with one red component is often the intended state).
/// </summary>
public sealed class GrasshopperReport
{
    /// <summary>One entry per solution that ended during the run, in order.</summary>
    [JsonPropertyName("solutions")] public required IReadOnlyList<GrasshopperSolution> Solutions { get; init; }

    /// <summary>Every object that reported a runtime message or ended in a phase other than <c>Computed</c>
    /// (PRD §10) — never the whole canvas, which would blow the response budget.</summary>
    [JsonPropertyName("components")] public required IReadOnlyList<GrasshopperComponentReport> Components { get; init; }
}

/// <summary>One ended Grasshopper solution.</summary>
public sealed class GrasshopperSolution
{
    [JsonPropertyName("started_at")] public required string StartedAt { get; init; }
    [JsonPropertyName("duration_ms")] public required double DurationMs { get; init; }
    /// <summary>Grasshopper's <c>GH_ProcessStep</c>/solution state at end (e.g. "Process", "Aborted").</summary>
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("depth")] public required int Depth { get; init; }
}

/// <summary>One object's outcome in the solve (PRD §10).</summary>
public sealed class GrasshopperComponentReport
{
    [JsonPropertyName("guid")] public required Guid Guid { get; init; }
    [JsonPropertyName("nickname")] public required string Nickname { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    /// <summary>The object's phase at solution end (e.g. "Computed", "Failed", "Blank").</summary>
    [JsonPropertyName("phase")] public required string Phase { get; init; }
    [JsonPropertyName("processor_ms")] public required double ProcessorMs { get; init; }
    [JsonPropertyName("messages")] public required IReadOnlyList<GrasshopperMessage> Messages { get; init; }
}

/// <summary>One runtime message on an object, its severity mapped from Grasshopper's error/warning/remark.</summary>
public sealed class GrasshopperMessage
{
    /// <summary>"error" | "warning" | "remark".</summary>
    [JsonPropertyName("severity")] public required string Severity { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
}

/// <summary>
/// Collects Grasshopper solution events for a run's duration (PRD §10). The adapter subscribes on
/// construction and unsubscribes on <see cref="IDisposable.Dispose"/>; <see cref="BuildReport"/> is called
/// after the run (before dispose) and returns null when no solution ended, so the run carries no
/// <c>grasshopper</c> field. A no-op scope is returned when Grasshopper is not loaded.
/// </summary>
public interface IGrasshopperSolveScope : IDisposable
{
    GrasshopperReport? BuildReport();
}

/// <summary>The scope used when Grasshopper is not loaded (or in tier 1): nothing to report.</summary>
public sealed class NullGrasshopperSolveScope : IGrasshopperSolveScope
{
    public static readonly NullGrasshopperSolveScope Instance = new();
    public GrasshopperReport? BuildReport() => null;
    public void Dispose() { }
}
