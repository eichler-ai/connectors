using System;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Thrown when a script uses a Revit API member <see cref="ScriptApiDenylist"/> forbids (PRD §14).
/// Raised at COMPILE time, from the same point in RoslynScriptRunner.GetOrCompile where
/// <see cref="ScriptAwaitNotAllowedException"/> is raised, and surfaced through the identical path:
/// TransactionScriptExecutor rolls back the ambient Transaction/TransactionGroup it had already opened,
/// and RequestDispatcher builds the usual PRD §01 diagnostic record. No new failure handling exists for
/// this, deliberately.
///
/// The message always names the concrete member that was rejected and why, and states the alternative
/// (PRD §01: no generic "an error occurred" wrappers, and a remedy wherever there's a real next step).
/// </summary>
public sealed class ScriptApiDenylistViolationException : Exception
{
    public const string Code = "script-api-denied";

    /// <summary>The fully-qualified member the script used, e.g. <c>Autodesk.Revit.DB.Document.Close</c>.</summary>
    public string DeniedMember { get; }

    public ScriptApiDenylistViolationException(string deniedMember, string reason, string remedy)
        : base($"script uses `{deniedMember}`, which is not permitted from an agent script (code: {Code}). " +
               $"{reason} {remedy}")
    {
        DeniedMember = deniedMember;
    }
}
