using System;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Thrown when a script contains a top-level `await` expression (PR #2 review, Fix 1's confirmed
/// architecture decision). IExternalEventHandler.Execute(UIApplication) has no async/Task-based variant, so
/// RoslynScriptRunner blocks on the compiled script's Task via <c>.GetAwaiter().GetResult()</c> from inside
/// a fully synchronous Execute(). That is deadlock-safe only because a Roslyn script with no internal
/// `await` runs synchronously to completion before its Task is even returned (verified against
/// dotnet/roslyn#6928) -- so agent-supplied scripts must never contain their own `await`. Rejected before
/// compilation (RoslynScriptRunner walks the parsed syntax tree for AwaitExpressionSyntax), never silently
/// hung or silently dropped (PRD §01 observability-over-silence): this always surfaces as a script-execution
/// failure with a clear, actionable message.
/// </summary>
public sealed class ScriptAwaitNotAllowedException : Exception
{
    public const string Code = "script-await-not-allowed";

    public ScriptAwaitNotAllowedException()
        : base(
            $"script contains an `await` expression; agent scripts must not use async/await (code: {Code}). " +
            "Revit's IExternalEventHandler.Execute(UIApplication) callback is synchronous and UI-thread-only " +
            "with no async variant, so scripts must run to completion synchronously -- remove the `await` " +
            "and use synchronous calls instead.")
    {
    }
}
