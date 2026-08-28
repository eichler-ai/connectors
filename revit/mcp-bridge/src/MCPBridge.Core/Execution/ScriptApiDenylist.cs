using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Compile-time guard over what an agent script may call, now that ScriptGlobals exposes the real
/// Autodesk.Revit.DB.Document rather than a narrow interface (PRD §14, Phase 3).
///
/// TWO CHECKS, and they are not equally important:
///
/// 1. TRANSACTION OWNERSHIP -- the load-bearing one. TransactionScriptExecutor opens an ambient
///    TransactionGroup + Transaction around every script run, before compilation even happens, and
///    Revit permits only one open Transaction per Document. A script constructing its own
///    Transaction/TransactionGroup/SubTransaction against that same document therefore always fails,
///    and worse, could leave the executor's own transaction state ambiguous. This check is what makes
///    exposing the real Document safe at all; everything else a script does with the real API (reads,
///    writes, element queries, geometry) rides the ambient transaction correctly with no new
///    transaction-ownership scheme -- confirmed live before Phase 3 shipped.
///
///    Note this is a DIFFERENT thing from what the deleted IScriptDocument/IScriptUiDocument/
///    IScriptUiApplication interfaces used to guard. Those blocked IDocumentAdapter.CreateTransaction/
///    CreateTransactionGroup -- our own adapter methods, not Revit API, and unreachable from a script
///    once Document became the real type anyway.
///
/// 2. DOCUMENT-LIFECYCLE / WORKSHARING MEMBERS -- a fixed starting denylist: operations a script has no
///    business performing on a document a human has open in a live session (closing or saving it out
///    from under them, syncing or relinquishing shared ownership, driving the printer).
///
/// SCOPE, deliberately (PRD §14's own explicit caveat): this list is a STARTING POINT expected to grow
/// from real use. It is not, and is not trying to be, an exhaustive policy over the ~1,700-type Revit
/// API surface -- that cannot be enumerated up front, and a general policy engine here would be
/// over-engineering for a list this size. Add entries as real usage justifies them.
///
/// It is also a GUARD, not a sandbox: a determined script can still reach a denied member through
/// reflection, exactly as scripts previously reached the real Document that way. That is accepted --
/// the purpose is to stop an agent from doing something destructive by accident or by plausible-looking
/// mistake, which is the realistic failure mode, not to contain hostile code.
/// </summary>
internal static class ScriptApiDenylist
{
    private const string TransactionType = "Autodesk.Revit.DB.Transaction";
    private const string TransactionGroupType = "Autodesk.Revit.DB.TransactionGroup";
    private const string SubTransactionType = "Autodesk.Revit.DB.SubTransaction";

    /// <summary>
    /// Types a script must never construct -- see check 1 in the class doc comment.
    /// </summary>
    private static readonly HashSet<string> DeniedConstructedTypes = new()
    {
        TransactionType,
        TransactionGroupType,
        SubTransactionType,
    };

    /// <summary>
    /// Denied (containing type, member name) pairs -- see check 2. Keyed on the CONTAINING TYPE as well
    /// as the name, deliberately: a bare name check would reject ordinary, harmless .NET such as
    /// System.IO.Stream.Close. Member names verified against the live Revit 2027 API via
    /// describe_function (note PRD §14 originally wrote "SynchronizeWithCentralDocument"; the actual
    /// member is Document.SynchronizeWithCentral).
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> DeniedMembersByType = new()
    {
        ["Autodesk.Revit.DB.Document"] = new HashSet<string>
        {
            "Close",
            "Save",
            "SaveAs",
            "SynchronizeWithCentral",
            "Print",
            "PrintToFile",
        },
        ["Autodesk.Revit.DB.WorksharingUtils"] = new HashSet<string>
        {
            "RelinquishOwnership",
        },
    };

    /// <summary>
    /// Walks the already-bound compilation and throws <see cref="ScriptApiDenylistViolationException"/>
    /// on the first violation found. Called from RoslynScriptRunner.GetOrCompile after script.Compile()
    /// reports no errors -- binding must have succeeded for the semantic model to resolve symbols at all,
    /// and running before that would just re-report ordinary compile errors as denylist violations.
    ///
    /// This is a SEMANTIC check, not a text search: it asks the semantic model what each expression
    /// actually binds to, so a using-alias, a fully-qualified name, and a `var`-typed intermediate are
    /// all caught identically, while the same words inside a string literal or a comment are not.
    /// </summary>
    public static void Enforce(Compilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);

            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case ObjectCreationExpressionSyntax creation:
                        CheckConstruction(semanticModel, creation);
                        break;
                    case InvocationExpressionSyntax invocation:
                        CheckInvocation(semanticModel, invocation);
                        break;
                }
            }
        }
    }

    private static void CheckConstruction(SemanticModel semanticModel, ObjectCreationExpressionSyntax creation)
    {
        var type = semanticModel.GetTypeInfo(creation).Type;
        var typeName = FullName(type);
        if (typeName is null || !DeniedConstructedTypes.Contains(typeName))
        {
            return;
        }

        throw new ScriptApiDenylistViolationException(
            typeName,
            "Every script already runs inside a Transaction and TransactionGroup this connector opens " +
            "for you, and Revit allows only one open Transaction per document at a time, so opening " +
            "your own always fails.",
            "Just make your changes directly -- they are committed automatically if the script succeeds " +
            "and rolled back if it throws.");
    }

    private static void CheckInvocation(SemanticModel semanticModel, InvocationExpressionSyntax invocation)
    {
        if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
        {
            return;
        }

        var containingType = FullName(method.ContainingType);
        if (containingType is null
            || !DeniedMembersByType.TryGetValue(containingType, out var deniedMembers)
            || !deniedMembers.Contains(method.Name))
        {
            return;
        }

        throw new ScriptApiDenylistViolationException(
            $"{containingType}.{method.Name}",
            "This changes the document's lifecycle or its worksharing state rather than its content, " +
            "which is not something a script may do to a document a person has open in a live Revit " +
            "session.",
            "Ask the person driving Revit to do this themselves if it is genuinely needed.");
    }

    /// <summary>
    /// Namespace-qualified name with no assembly/global:: prefix and no generic type arguments -- the
    /// shape the denylist tables above are written in. Returns null for an unresolved symbol (which
    /// simply means nothing to check: a script that doesn't bind never gets this far anyway).
    /// </summary>
    private static string? FullName(ITypeSymbol? type)
    {
        if (type is null || type.TypeKind == TypeKind.Error)
        {
            return null;
        }

        var containingNamespace = type.ContainingNamespace;
        if (containingNamespace is null || containingNamespace.IsGlobalNamespace)
        {
            return type.Name;
        }

        var namespaceParts = new List<string>();
        for (var ns = containingNamespace; ns is not null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
        {
            namespaceParts.Add(ns.Name);
        }

        namespaceParts.Reverse();
        return string.Join(".", namespaceParts.Append(type.Name));
    }
}
