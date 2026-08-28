using System.Collections.Generic;

namespace MCPBridge.Core.Execution;

/// <summary>
/// What <see cref="ScriptApiDenylist"/> found in one script's compiled form, cached alongside the
/// compiled <c>Script&lt;object&gt;</c> it describes (PRD §14).
///
/// WHY THIS TYPE EXISTS AT ALL -- the compile-cache/per-request split. Compilation is cached by
/// verbatim script text, but whether a run is *confirmed* is a property of the REQUEST, not the
/// script: the same text can arrive once without <c>confirm_lifecycle_actions</c> and again with it,
/// and both must be judged correctly. So the two halves are separated by what they actually depend on:
///
/// - DETECTION (this type) depends only on the script text, so it is computed once per compilation and
///   cached with it. Re-deriving it on a cache hit would mean re-walking the syntax tree on every rerun
///   for an answer that cannot have changed.
/// - The DECISION (allow/reject) depends on detection AND the request's confirmation flag, so it is
///   made per run, in <see cref="RoslynScriptRunner.RunAsync"/>, before anything is emitted or executed.
///
/// Transaction construction is not represented here, deliberately: it has no per-request dimension at
/// all (there is no way to opt in), so it stays an unconditional throw during compilation and never
/// reaches a cache entry.
/// </summary>
internal sealed class ScriptApiAnalysis
{
    public static readonly ScriptApiAnalysis Clean = new(new List<string>());

    public ScriptApiAnalysis(IReadOnlyList<string> lifecycleMembers)
    {
        LifecycleMembers = lifecycleMembers;
    }

    /// <summary>
    /// Fully-qualified members the script uses that escape the ambient transaction's rollback boundary
    /// (e.g. <c>Autodesk.Revit.DB.Document.SaveAs</c>), in source order, deduplicated. Empty for the
    /// overwhelmingly common script.
    /// </summary>
    public IReadOnlyList<string> LifecycleMembers { get; }

    public bool RequiresLifecycleConfirmation => LifecycleMembers.Count > 0;
}
