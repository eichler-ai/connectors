using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// The compile-time guard over a script's bound compilation (rhino/docs/PRD.md §07, §08; Revit §14's
/// one membership test: <em>would the post-run undo actually undo this?</em>). Two tiers, plus a
/// prevention rule Revit never needed:
///
/// <list type="bullet">
/// <item><b>Hard-blocked, no opt-in</b> (<c>script-api-denied</c>): the undo-record API itself (a
/// mid-run <c>Undo()</c> destroys the run's own entry -- verified, spikes §3), <c>RhinoApp.Exit</c>, an
/// exit/quit command token passed to <c>RunScript</c>, and <b>every interactive getter</b>
/// (<c>RhinoGet.*</c>, anything derived from <c>Rhino.Input.Custom.GetBaseClass</c>): each blocks
/// Rhino's main thread on a click nobody will make. This is prevention, not suppression.</item>
/// <item><b>Confirmation-gated</b> (<c>script-lifecycle-confirmation-required</c> unless the request
/// set <c>confirm_lifecycle_actions</c>): saving, exporting, closing, opening and creating documents,
/// and the command tokens that do the same through <c>RunScript</c> -- effects outside the document's
/// content, which no undo takes back.</item>
/// </list>
///
/// Keyed on <em>(containing type, member)</em>, never a bare name, so <c>Stream.Close</c> is untouched.
/// <c>RunScript</c>'s argument is a string: the walk gates literal command tokens here, and the
/// runtime wrapper (phase 1 PR 2 leaves that to a follow-up) gates computed ones with the same codes.
/// Like Revit's, this is a guard against plausible mistakes, not a sandbox: reflection routes around
/// it and that is accepted (PRD §02). So does a <c>dynamic</c> receiver (<c>dynamic d = Document;
/// d.Undo();</c>): nothing binds, so the walk sees nothing -- and unlike Revit, where the hard tier
/// was also enforced by the host's own one-transaction rule, here it is the ONLY line for the undo
/// members. Accepted for the same reason: reaching for dynamic is deliberate, and the consequence
/// (a run whose own undo entry is gone) lands on the agent's run, not on a person's work, because
/// the post-run rollback checks the last command first.
/// </summary>
internal static class ScriptApiDenylist
{
    private const string RhinoDoc = "Rhino.RhinoDoc";
    private const string RhinoApp = "Rhino.RhinoApp";
    private const string RhinoGet = "Rhino.Input.RhinoGet";
    private const string GetBaseClass = "Rhino.Input.Custom.GetBaseClass";

    /// <summary>Hard-blocked members by containing type.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> DeniedMembers = new Dictionary<string, IReadOnlySet<string>>
    {
        [RhinoDoc] = new HashSet<string> { "BeginUndoRecord", "EndUndoRecord", "ClearUndoRecords", "ClearRedoRecords", "Undo", "Redo", "AddCustomUndoEvent" },
        [RhinoApp] = new HashSet<string> { "Exit" },
    };

    /// <summary>Types whose every method is hard-blocked: the static interactive getters, and Rhino's
    /// modal dialog helpers (message boxes, pickers) -- the same class, a main thread waiting for a
    /// click nobody will make (review of #282).</summary>
    internal static readonly IReadOnlySet<string> DeniedTypes = new HashSet<string> { RhinoGet, "Rhino.UI.Dialogs" };

    /// <summary>Base types whose derived types may not be constructed (the interactive getter objects).</summary>
    internal static readonly IReadOnlySet<string> DeniedConstructedBaseTypes = new HashSet<string> { GetBaseClass };

    /// <summary>Confirmation-gated members by containing type.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> LifecycleMembersByType = new Dictionary<string, IReadOnlySet<string>>
    {
        // Signatures verified against RhinoCommon 8.35's XML: Save/SaveAs/SaveAsTemplate/Export/ExportSelected/
        // Write3dmFile/WriteFile (filesystem); Close (the person's session); Open/OpenFile/OpenHeadless/Create/
        // CreateHeadless (which documents are open); ReadFile/Import (content from outside the document).
        [RhinoDoc] = new HashSet<string> { "Save", "SaveAs", "SaveAsTemplate", "Export", "ExportSelected", "Write3dmFile", "WriteFile", "Close", "Open", "OpenFile", "OpenHeadless", "Create", "CreateHeadless", "ReadFile", "Import" },
    };

    /// <summary>Command tokens (lower-cased, leading `_`/`-` stripped) that make a RunScript/ExecuteCommand
    /// call gated or denied. Undo/Redo are DENIED: a mid-command undo discards the run's own entry
    /// (spikes §3), after which the post-run _Undo would revert a person's (review of #282).</summary>
    internal static readonly IReadOnlySet<string> DeniedCommandTokens = new HashSet<string> { "exit", "quit", "undo", "redo", "undomultiple", "redomultiple" };
    internal static readonly IReadOnlySet<string> LifecycleCommandTokens = new HashSet<string>
    {
        "save", "saveas", "savesmall", "saveastemplate", "incrementalsave", "autosave", "export", "exportselected",
        "open", "new", "close", "print", "import", "insert", "revert", "packagemanager", "worksession",
    };

    public static ScriptApiAnalysis Analyze(Compilation compilation)
    {
        var lifecycleMembers = new List<string>();
        var seen = new HashSet<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var symbol = semanticModel.GetSymbolInfo(node).Symbol;
                if (symbol is IMethodSymbol method)
                {
                    if (method.MethodKind == MethodKind.Constructor)
                    {
                        CheckConstruction(method.ContainingType);
                    }
                    else
                    {
                        CheckMember(method.ContainingType, method.Name);
                        if (node is InvocationExpressionSyntax invocation)
                        {
                            CheckRunScript(method, invocation, semanticModel, seen, lifecycleMembers);
                        }

                        var gated = LifecycleMemberOrNull(method.ContainingType, method.Name);
                        if (gated is not null && seen.Add(gated))
                        {
                            lifecycleMembers.Add(gated);
                        }
                    }

                    continue;
                }

                if (symbol is IPropertySymbol property)
                {
                    CheckMember(property.ContainingType, property.Name);
                    continue;
                }

                // A constructed type Roslyn could not bind a constructor for (target-typed `new`, dynamic
                // arguments) still reports the type: check that too, so the getter block does not rest on
                // the constructor symbol alone (Revit §14's lesson).
                if (node is BaseObjectCreationExpressionSyntax)
                {
                    CheckConstruction(semanticModel.GetTypeInfo(node).Type as INamedTypeSymbol);
                }
            }
        }

        return lifecycleMembers.Count == 0 ? ScriptApiAnalysis.Clean : new ScriptApiAnalysis(lifecycleMembers);
    }

    private static void CheckConstruction(INamedTypeSymbol? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var name = FullName(t);
            if (name is not null && DeniedConstructedBaseTypes.Contains(name))
            {
                throw ScriptApiDenylistViolationException.InteractiveGetter(FullName(type)!);
            }
        }
    }

    private static void CheckMember(INamedTypeSymbol? containing, string member)
    {
        var typeName = FullName(containing);
        if (typeName is null)
        {
            return;
        }

        if (DeniedTypes.Contains(typeName))
        {
            throw ScriptApiDenylistViolationException.InteractiveGetter(typeName + "." + member);
        }

        if (DeniedMembers.TryGetValue(typeName, out var members) && members.Contains(member))
        {
            throw ScriptApiDenylistViolationException.UndoOrExitMember(typeName + "." + member);
        }
    }

    private static string? LifecycleMemberOrNull(INamedTypeSymbol? containing, string member)
    {
        var typeName = FullName(containing);
        return typeName is not null && LifecycleMembersByType.TryGetValue(typeName, out var members) && members.Contains(member)
            ? typeName + "." + member
            : null;
    }

    /// <summary>RhinoApp.RunScript / RhinoApp.ExecuteCommand: every constant string argument is scanned
    /// for command tokens (ExecuteCommand takes a command name; RunScript a macro). A computed string
    /// is not seen here; the plug-in's runtime wrapper for those is tracked as a follow-up.</summary>
    private static void CheckRunScript(IMethodSymbol method, InvocationExpressionSyntax invocation, SemanticModel semanticModel, HashSet<string> seen, List<string> lifecycleMembers)
    {
        if (FullName(method.ContainingType) != RhinoApp || (method.Name != "RunScript" && method.Name != "ExecuteCommand"))
        {
            return;
        }

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var constant = semanticModel.GetConstantValue(arg.Expression);
            if (!constant.HasValue || constant.Value is not string script)
            {
                continue;
            }

            foreach (var token in CommandTokens(script))
            {
                if (DeniedCommandTokens.Contains(token))
                {
                    throw ScriptApiDenylistViolationException.UndoOrExitMember(RhinoApp + "." + method.Name + "(\"" + token + "\")");
                }

                if (LifecycleCommandTokens.Contains(token))
                {
                    var key = RhinoApp + "." + method.Name + "(\"" + token + "\")";
                    if (seen.Add(key))
                    {
                        lifecycleMembers.Add(key);
                    }
                }
            }
        }
    }

    /// <summary>Every whitespace-separated word of a command string, lower-cased, with Rhino's `_`
    /// (English) and `-` (no dialog) prefixes stripped; the first word and any word after an `Enter`
    /// are the tokens that name commands, but scanning all of them errs on the side of gating.</summary>
    internal static IEnumerable<string> CommandTokens(string script)
    {
        foreach (var raw in script.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.TrimStart('_', '-', '!').ToLowerInvariant();
            if (t.Length > 0)
            {
                yield return t;
            }
        }
    }

    private static string? FullName(ITypeSymbol? type) =>
        type is null ? null : type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
}
