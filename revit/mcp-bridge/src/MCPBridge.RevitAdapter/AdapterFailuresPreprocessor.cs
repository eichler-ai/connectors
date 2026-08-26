using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Real IFailuresPreprocessor (PRD §07): dismisses every warning, forces rollback on any error. Not
/// unit-tested -- see RevitTransactionAdapter's own doc comment for why.
/// </summary>
internal sealed class AdapterFailuresPreprocessor : IFailuresPreprocessor
{
    private readonly Action<IReadOnlyList<FailureSummary>> _observer;

    public AdapterFailuresPreprocessor(Action<IReadOnlyList<FailureSummary>> observer)
    {
        _observer = observer;
    }

    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        var messages = accessor.GetFailureMessages();
        if (messages.Count == 0)
        {
            return FailureProcessingResult.Continue;
        }

        var summaries = new List<FailureSummary>(messages.Count);
        var hasError = false;
        foreach (var msg in messages)
        {
            var isError = msg.GetSeverity() is FailureSeverity.Error or FailureSeverity.DocumentCorruption;
            hasError |= isError;
            summaries.Add(new FailureSummary(
                isError,
                msg.GetDescriptionText(),
                msg.GetFailureDefinitionId().Guid.ToString(),
                msg.GetFailingElementIds().Select(id => id.ToString() ?? "").ToArray()));

            if (!isError)
            {
                accessor.DeleteWarning(msg);
            }
        }

        _observer(summaries);
        return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
