using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Real IFailuresPreprocessor (PRD §07): dismisses every warning, forces rollback on any error. Not
/// unit-tested -- see RevitTransactionAdapter's own doc comment for why.
///
/// Review finding, fixed: this used to delete warnings as it iterated, THEN check whether any error was
/// present -- but Revit's documented contract is that no failure resolution may be performed on a pass
/// that returns ProceedWithRollBack (the earlier version violated that whenever a commit had both a
/// warning and an error, deleting the warning before discovering the error later in the same list).
/// Fixed by scanning for an error first, deleting nothing at all on that pass if one exists.
///
/// Review finding, fixed: this used to invoke a caller-supplied observer callback (building
/// DiagnosticRecord instances, which can throw on an empty message) synchronously from inside
/// PreprocessFailures, i.e. from Revit's own failure-handling dispatch -- an uncaught exception there
/// left the dialog/commit in an undefined state. Now this class only accumulates plain FailureSummary
/// data (never throws building it); the caller reads <see cref="Summaries"/> once, after Commit()
/// returns, entirely outside Revit's callback.
/// </summary>
internal sealed class AdapterFailuresPreprocessor : IFailuresPreprocessor
{
    private readonly List<FailureSummary> _summaries = new();

    public IReadOnlyList<FailureSummary> Summaries => _summaries;

    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        var messages = accessor.GetFailureMessages();
        if (messages.Count == 0)
        {
            return FailureProcessingResult.Continue;
        }

        var hasError = messages.Any(msg => msg.GetSeverity() is FailureSeverity.Error or FailureSeverity.DocumentCorruption);

        foreach (var msg in messages)
        {
            var isError = msg.GetSeverity() is FailureSeverity.Error or FailureSeverity.DocumentCorruption;
            var description = msg.GetDescriptionText();
            _summaries.Add(new FailureSummary(
                isError,
                string.IsNullOrWhiteSpace(description) ? "(no description provided by Revit)" : description,
                msg.GetFailureDefinitionId().Guid.ToString(),
                msg.GetFailingElementIds().Select(id => id.ToString() ?? "").ToArray()));

            // Only ever dismiss warnings on a pass with no error present at all -- see this class's own
            // doc comment. hasError forces ProceedWithRollBack below, so nothing here needs deleting.
            if (!isError && !hasError)
            {
                accessor.DeleteWarning(msg);
            }
        }

        return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
