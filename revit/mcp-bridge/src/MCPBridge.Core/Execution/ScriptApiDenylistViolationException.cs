using System;
using System.Collections.Generic;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Thrown when a script's use of the Revit API is refused by <see cref="ScriptApiDenylist"/> (PRD §14).
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
/// Both surface through the identical path, and neither needed new failure handling: TransactionScript-
/// Executor rolls back the ambient Transaction/TransactionGroup it had already opened, and Request-
/// Dispatcher builds the usual PRD §01 diagnostic record. Both are raised before anything is emitted or
/// executed, so a refused script -- of either kind -- changes nothing.
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

    /// <summary>The fully-qualified member the script used, e.g. <c>Autodesk.Revit.DB.Document.Close</c>. For a confirmation-required refusal naming several members, they are joined with ", ".</summary>
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
            $"(code: {ConfirmationRequiredCode}). Everything else a script changes is covered by the " +
            "Transaction this connector opens for you, so it is undone automatically if the script throws. " +
            "These members are not: they act outside this document's own content -- on a person's open " +
            "session, on the filesystem, on the shared central model, on a printer, or on another user's " +
            "ability to edit -- and no exception undoes that. Resend the same execute_script call with " +
            "confirm_lifecycle_actions: true if this is genuinely intended; otherwise remove the call.");
    }
}
