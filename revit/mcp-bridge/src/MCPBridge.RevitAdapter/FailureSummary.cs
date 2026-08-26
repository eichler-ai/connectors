using System.Collections.Generic;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Core-visible summary of one Revit Failures API message (PRD §07) -- never a raw Autodesk.Revit.DB
/// type, since MCPBridge.Core never references RevitAPI.dll directly.
/// </summary>
public sealed class FailureSummary
{
    public bool IsError { get; }
    public string Message { get; }
    public string FailureDefinitionId { get; }
    public IReadOnlyList<string> FailingElementIds { get; }

    public FailureSummary(bool isError, string message, string failureDefinitionId, IReadOnlyList<string> failingElementIds)
    {
        IsError = isError;
        Message = message;
        FailureDefinitionId = failureDefinitionId;
        FailingElementIds = failingElementIds;
    }
}
