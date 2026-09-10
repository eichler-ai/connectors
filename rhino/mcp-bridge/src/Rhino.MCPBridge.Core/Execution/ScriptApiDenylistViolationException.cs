using System;
using System.Collections.Generic;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// Thrown when a script's use of the Rhino API is refused by <see cref="ScriptApiDenylist"/> (PRD §14).
/// One type, two codes, because there are two genuinely different refusals and an agent must be able to
/// tell them apart from the message alone:
///
/// - <see cref="DeniedCode"/> (<c>script-api-denied</c>) -- unconditional. Raised at COMPILE time, from
///   the same point in RoslynScriptRunner.GetOrCompile where <see cref="ScriptAwaitNotAllowedException"/>
///   is raised. There is no flag that permits it; the script has to change.
/// - <see cref="ConfirmationRequiredCode"/> (<c>script-lifecycle-confirmation-required</c>) -- raised per
///   RUN, from RoslynScriptRunner.RunAsync, when the script uses a lifecycle member that escapes the
///   ambient transaction's rollback boundary and the request did not pass
///   <c>confirm_lifecycle_actions: true</c>. The same script text succeeds if resent with it, which is
///   why this one cannot be decided at compile time (compilation is cached; confirmation is per request).
///
/// Both surface through the identical path, and neither needed new failure handling: the outcome
/// is a failed run before anything executed, so UndoRunExecutor has nothing to revert and
/// RequestDispatcher builds the usual PRD §01 diagnostic record. A refused script -- of either kind
/// -- changes nothing.
///
/// The message always names the concrete member(s) rejected, the code, why, and the next step (PRD §01:
/// no generic "an error occurred" wrappers, and a remedy wherever there's a real next step).
/// </summary>
public sealed class ScriptApiDenylistViolationException : Exception
{
    public const string DeniedCode = "script-api-denied";
    public const string ConfirmationRequiredCode = "script-lifecycle-confirmation-required";

    /// <summary>Which of the two refusals this is -- <see cref="DeniedCode"/> or <see cref="ConfirmationRequiredCode"/>.</summary>
    public string Code { get; }

    /// <summary>The fully-qualified member the script used, e.g. <c>Rhino.RhinoDoc.Close</c>. For a confirmation-required refusal naming several members, they are joined with ", ".</summary>
    public string DeniedMember { get; }

    private ScriptApiDenylistViolationException(string code, string deniedMember, string message)
        : base(message)
    {
        Code = code;
        DeniedMember = deniedMember;
    }

    /// <summary>An unconditional refusal -- nothing the caller can pass makes this script run.</summary>
    public static ScriptApiDenylistViolationException Denied(string deniedMember, string reason, string remedy) =>
        new(DeniedCode,
            deniedMember,
            $"script uses `{deniedMember}`, which is not permitted from an agent script (code: {DeniedCode}). " +
            $"{reason} {remedy}");

    /// <summary>Hard-blocked because the member acts on the undo record this connector owns, or exits Rhino.</summary>
    public static ScriptApiDenylistViolationException UndoOrExitMember(string deniedMember) =>
        Denied(deniedMember,
            "The connector runs every script inside one command whose undo entry it owns and reverts on failure; a script that touches the undo stack itself destroys that entry (verified live), and exiting Rhino ends the session the person is in.",
            "Remove the call. To undo the connector's own runs use the undo/redo tools; to end a run that went wrong, throw -- the connector reverts it.");

    /// <summary>Hard-blocked because the member waits for a person to click or type (an interactive getter).</summary>
    public static ScriptApiDenylistViolationException InteractiveGetter(string deniedMember) =>
        Denied(deniedMember,
            "It prompts on Rhino's command line and blocks the main thread until a person answers -- and no person is at the keyboard for an agent's script.",
            "Select or supply the input programmatically instead: find objects with Document.Objects (FindByLayer, GetObjectList, FindId), build points and geometry directly, and pass values as literals.");

    /// <summary>
    /// A refusal the caller can lift by resending the same script with <c>confirm_lifecycle_actions: true</c>.
    /// The message explains WHY these members are gated in the terms that actually decide it -- they escape
    /// the rollback boundary every other script change enjoys -- so an agent can judge whether confirming is
    /// appropriate rather than reflexively retrying with the flag set.
    /// </summary>
    public static ScriptApiDenylistViolationException LifecycleConfirmationRequired(IReadOnlyList<string> lifecycleMembers)
    {
        var members = string.Join(", ", lifecycleMembers);
        return new ScriptApiDenylistViolationException(
            ConfirmationRequiredCode,
            members,
            $"script uses `{members}`, which needs explicit confirmation before it may run " +
            $"(code: {ConfirmationRequiredCode}). Everything else a script changes is one undo entry the " +
            "connector reverts automatically if the script throws. These members are not: they act outside " +
            "this document's own content -- on the filesystem, on which documents are open, on content " +
            "imported from outside -- and no undo reverts that. Resend the same execute_script call with " +
            "confirm_lifecycle_actions: true if this is genuinely intended; otherwise remove the call.");
    }
}
