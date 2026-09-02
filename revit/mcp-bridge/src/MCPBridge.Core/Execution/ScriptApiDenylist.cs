using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Compile-time guard over what an agent script may call, now that ScriptGlobals exposes the real
/// Autodesk.Revit.DB.Document rather than a narrow interface (PRD §14, Phase 3).
///
/// TWO CHECKS, and they differ in KIND, not just in importance -- one is an unconditional rejection,
/// the other is a confirmation gate:
///
/// 1. TRANSACTION OWNERSHIP -- the load-bearing one. TransactionScriptExecutor opens a TransactionGroup
///    around every script run, before compilation even happens, and every write goes through a
///    transaction the connector opens inside that group (Connector.WithTransaction) with the §07 failure
///    preprocessor on its commit. A script constructing its own Transaction or TransactionGroup would
///    bypass that capture and collide with the run's own group state. This check is what makes exposing
///    the real Document safe at all; reads need nothing, and writes ride the connector's transaction.
///
///    THE OVER-BREADTH THIS CHECK ONCE HAD IS RESOLVED, and the resolution left the check itself
///    byte-for-byte unchanged -- worth recording, because it is the reason the fix was chosen. The check
///    cannot look at WHICH document a Transaction targets: it is compile-time, and the receiver is an
///    arbitrary expression. So it also refused the one case Revit itself allows -- a Transaction on a
///    document the script just created, which the ambient transaction does not cover, since
///    one-open-transaction is a per-DOCUMENT rule. A script could create a document and read it, not
///    write to it, which blocked PRD §13's corpus fixture system.
///
///    Issue #24 closed that WITHOUT narrowing anything here. The connector now opens and owns a
///    Transaction/TransactionGroup for each document a script creates, in the same step that creates it
///    (ScriptGlobals.CreateProjectDocument/CreateFamilyDocument -> ManagedDocumentTransactions), so the
///    script has no reason to construct one and the refusal stays unconditional. The rejected
///    alternative was a runtime document-identity comparison: Revit hands back DIFFERENT wrapper objects
///    for "the same" document depending on the API entry point, and DocumentIdentity.ResolveCached is
///    weakest for exactly the unsaved documents this is about, so a naive same-instance test would have
///    reopened the same bypass class two review rounds already closed here. The chosen shape gains no
///    new bypass surface at all: the creation helper is an ordinary method call on ScriptGlobals, not an
///    object creation, so this AST walk has nothing new to bind to -- asserted, not assumed, at both
///    tiers (TransactionScriptExecutorTests.CreateProjectDocument_IsNotADenylistViolation and
///    revit/test-harness's TestCreatedDocumentIsWritable). See PRD §14.
///
///    Note this is a DIFFERENT thing from what the deleted IScriptDocument/IScriptUiDocument/
///    IScriptUiApplication interfaces used to guard. Those blocked IDocumentAdapter.CreateTransaction/
///    CreateTransactionGroup -- our own adapter methods, not Revit API, and unreachable from a script
///    once Document became the real type anyway.
///
/// 2. DOCUMENT-LIFECYCLE / WORKSHARING MEMBERS -- CONFIRMATION-GATED, not blocked. These are allowed,
///    but only when the execute_script request that carries the script explicitly opted in with
///    confirm_lifecycle_actions: true.
///
///    THE GOVERNING PRINCIPLE, which is what decides whether a member belongs on this list: a script's
///    changes roll back automatically if it throws, because the ambient transaction covers document
///    CONTENT. These members are gated precisely because they escape that rollback boundary -- each
///    affects something outside this document's own content, which no exception can undo: a human's
///    local session (Close), the filesystem (Save/SaveAs), a shared central model other teammates see
///    (SynchronizeWithCentral), a physical device (Print/PrintToFile), or another user's ability to edit
///    (WorksharingUtils.RelinquishOwnership). The test for adding an entry is one question -- "does a
///    thrown exception actually undo this?" -- and for everything here the answer is no.
///
///    Confirmation, rather than a block, is the right mechanism for exactly this class: the operations
///    are legitimate and sometimes genuinely wanted, they just must not happen as an incidental side
///    effect of a script an agent wrote for some other purpose. Contrast check 1, which can never be
///    opted into because it is not a policy judgement at all -- a second Transaction simply cannot work.
///
///    Detection is compile-time and cacheable (a property of the script text); the allow/reject decision
///    is per-run, since the confirmation flag is per-request. See <see cref="ScriptApiAnalysis"/> for why
///    the two are split that way, and RoslynScriptRunner.RunAsync for where the decision is made.
///
/// SCOPE, deliberately (PRD §14's own explicit caveat): this list is a STARTING POINT expected to grow
/// from real use. It is not, and is not trying to be, an exhaustive policy over the ~1,700-type Revit
/// API surface -- that cannot be enumerated up front, and a general policy engine here would be
/// over-engineering for a list this size. Add entries as real usage justifies them.
///
/// It is also a GUARD, not a sandbox: a determined script can still reach a denied member through
/// reflection, exactly as scripts previously reached the real Document that way. That is accepted --
/// the purpose is to stop an agent from doing something destructive by accident or by plausible-looking
/// mistake, which is the realistic failure mode, not to contain hostile code. Reflection reaches the
/// CONNECTOR'S OWN internals too, not just Revit's API, and that is the bigger half of what is being
/// accepted here: <see cref="ManagedDocumentTransactions"/> is internal, but reflection over it grants
/// commit/rollback authority on every document this run manages INCLUDING the ambient one -- so a
/// script willing to use reflection can commit the ambient document's transaction mid-run and defeat
/// the roll-back-on-throw guarantee on a document a human may have open. Same accepted position, same
/// reason (deliberate, not accidental); see ManagedDocumentTransactions' own class comment and PRD §14,
/// which record it as well. What is NOT accepted, and is fixed structurally rather than by accepting
/// it, is any route that reaches those internals WITHOUT reflection -- a public type that is, hands
/// out, or is itself such machinery (three live instances found and closed by review, all now pinned
/// in revit/test-harness/denylist_bypass_test.go).
///
/// ONE SUCH ROUTE IS NOW DELIBERATE, AND IT IS GATED RATHER THAN ACCEPTED (issue #132).
/// <c>Connector.Settle</c> is exactly the commit/rollback authority described above, reachable with no
/// reflection at all -- that is its purpose, since Revit refuses Close/Save/SaveAs while the connector
/// holds anything open on the document. It is therefore an entry in check 2's table, the first one that
/// is our own member rather than Revit's: the capability is first-class, and the REQUEST must opt into
/// it with confirm_lifecycle_actions like every other member that escapes the rollback boundary. The
/// paragraph above still describes every route that is NOT gated.
/// </summary>
internal static class ScriptApiDenylist
{
    private const string TransactionType = "Autodesk.Revit.DB.Transaction";
    private const string TransactionGroupType = "Autodesk.Revit.DB.TransactionGroup";
    private const string SubTransactionType = "Autodesk.Revit.DB.SubTransaction";

    /// <summary>
    /// Types a script must never construct -- see check 1 in the class doc comment.
    ///
    /// <c>Autodesk.Revit.DB.SubTransaction</c> is DELIBERATELY ABSENT (#146 Phase 1, #143). It sat here
    /// from the first version by association -- "no transaction of any kind" -- but it fails check 1's
    /// own test: it is not a competing transaction. A SubTransaction can only be started INSIDE an open
    /// Transaction, commits into that transaction (so the §07 failure preprocessor installed on the
    /// connector's commit still sees every write), and cannot escape the run's rollback boundary. What
    /// it adds is the one thing a connector-owned transaction otherwise lacks: an intra-run SAVEPOINT --
    /// try a slice, roll just that slice back, carry on. Verified live (Revit 2025): create-in-sub,
    /// RollBack, element gone, ambient writes intact. Its "no open transaction" failure is mapped by
    /// RequestDispatcher to name Connector.WithTransaction.
    ///
    /// PERMITTED WITH ONE SYNTACTIC CONDITION, enforced by <see cref="RequireSubTransactionHeldInUsing"/>:
    /// the construction must be the resource of a `using`. Live-verified hazard (Revit 2025): a
    /// SubTransaction still active when the enclosing transaction closes -- the WithTransaction block
    /// ending, Connector.Settle, or an exception unwinding
    /// through RunBody's rollback -- is accepted silently (slice kept, no exception) and Revit then
    /// crashed on the next Document.Close. Nothing at runtime can guard it: the connector never sees the
    /// script's SubTransaction object, and Document exposes no open-sub-transaction observable. Dispose
    /// ends an active sub-transaction, so `using` is the one shape that makes the crash unreachable by
    /// construction, and this walk already judges construction sites. Fails open on the deliberate
    /// spellings the denylist accepts everywhere (reflection, a factory method) -- the accidental,
    /// plausible-looking `var st = new SubTransaction(doc)` is exactly what it catches. A connector-owned
    /// savepoint wrapper would make even that unnecessary (#146, Phase 4 candidate).
    /// </summary>
    private static readonly HashSet<string> DeniedConstructedTypes = new()
    {
        TransactionType,
        TransactionGroupType,
    };

    /// <summary>
    /// Confirmation-gated (containing type, member name) pairs -- see check 2. Keyed on the CONTAINING
    /// TYPE as well as the name, deliberately: a bare name check would gate ordinary, harmless .NET such
    /// as System.IO.Stream.Close. Member names verified against the live Revit 2027 API via
    /// describe_function (note PRD §14 originally wrote "SynchronizeWithCentralDocument"; the actual
    /// member is Document.SynchronizeWithCentral).
    ///
    /// A DELIBERATE NON-ENTRY: a member reached through a `dynamic` receiver (`dynamic d = Document;
    /// d.Close();`) is NOT detected here, and that is accepted rather than overlooked. The receiver's
    /// type is erased at compile time, so nothing binds and there is no containing type to key on --
    /// gating it would mean matching the bare NAME "Close" on any dynamic receiver, which is exactly the
    /// check this table's shape exists to avoid (it would gate System.IO.Stream.Close and every other
    /// unrelated Close in .NET). It also fails the carve-out test in the same way reflection does: writing
    /// `dynamic` to reach a gated member is deliberate, not the accidental or plausible-looking mistake
    /// this guard is for. Confirmed live, so it is a known accepted gap and not a guess. Constructing a
    /// denied TYPE via `dynamic` is a different matter and stays refused -- see Analyze.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> LifecycleMembersByType = new()
    {
        ["Autodesk.Revit.DB.Document"] = new HashSet<string>
        {
            "Close",
            "Save",
            "SaveAs",
            // Writes the model into an Autodesk cloud project -- SaveAs's escape from this document's
            // own content, aimed at a shared destination other people see.
            "SaveAsCloudModel",
            "SynchronizeWithCentral",
            "Print",
            "PrintToFile",
            // Document is IDisposable and disposing one closes it, so this is Close under another
            // spelling; the same "no exception undoes it" answer applies.
            "Dispose",
        },
        ["Eichler.Connectors.Revit.Connector"] = new HashSet<string>
        {
            // OUR OWN member, not Revit's, and the first entry here that is (issue #132). It belongs by
            // this list's own governing test -- "does a thrown exception actually undo this?" -- and the
            // answer is no by construction: Settle(keep: true) assimilates the document's transaction
            // group, making every write to it so far permanent immediately, and Settle(keep: false)
            // discards them just as irreversibly. Ungated, a script could permanently commit to the
            // AMBIENT document -- a real model a person has open -- with no request-level opt-in, which
            // is exactly the authority this file's class comment says is accepted only via reflection
            // and must be closed structurally wherever a non-reflection route appears.
            //
            // Gating costs the feature nothing: Settle exists to make Close/Save/SaveAs/
            // SynchronizeWithCentral legal in the same run, and every one of those already requires the
            // flag, so any script with a real use for Settle is already sending it.
            "Settle",
        },
        ["Autodesk.Revit.DB.PrintManager"] = new HashSet<string>
        {
            // Sends the job to a physical device (or a file), same category as Document.Print.
            "SubmitPrint",
        },
        ["Autodesk.Revit.UI.UIDocument"] = new HashSet<string>
        {
            // Saves AND ends the human's open session in one call -- both halves already gated on Document.
            "SaveAndClose",
        },
        ["Autodesk.Revit.UI.UIApplication"] = new HashSet<string>
        {
            // Queues a Revit UI command to run AFTER control leaves the API context -- i.e. after the
            // ambient transaction has already been committed or rolled back. Whatever it does is
            // structurally outside the rollback boundary, whichever command is posted.
            "PostCommand",
        },
        ["Autodesk.Revit.DB.WorksharingUtils"] = new HashSet<string>
        {
            "RelinquishOwnership",
        },
    };

    /// <summary>
    /// Walks the already-bound compilation. Throws <see cref="ScriptApiDenylistViolationException"/> on
    /// the first transaction-construction violation (check 1 -- unconditional, no opt-in exists), and
    /// otherwise RETURNS what lifecycle members the script uses (check 2) for the caller to judge against
    /// this run's confirmation flag. Called from RoslynScriptRunner.GetOrCompile after script.Compile()
    /// reports no errors -- binding must have succeeded for the semantic model to resolve symbols at all,
    /// and running before that would just re-report ordinary compile errors as denylist violations.
    ///
    /// This is a SEMANTIC check, not a text search: it asks the semantic model what each expression
    /// actually binds to, so a using-alias, a fully-qualified name, and a `var`-typed intermediate are
    /// all caught identically, while the same words inside a string literal or a comment are not. That
    /// property matters as much for the gated members as for the constructed types -- moving lifecycle
    /// members onto a different enforcement path changed WHEN they are judged, not HOW they are found.
    /// </summary>
    public static ScriptApiAnalysis Analyze(Compilation compilation)
    {
        // Ordered + deduplicated: the rejection message names every member the script uses, in the order
        // they appear, so an agent that has to remove or confirm them sees all of them at once rather
        // than discovering them one failed run at a time.
        var lifecycleMembers = new List<string>();
        var seen = new HashSet<string>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);

            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                // Resolve what this node BINDS TO and judge that, rather than matching syntax shapes.
                // Both checks below key off the bound symbol for the same reason: syntax-shape matching
                // is what an adversary (or an ordinary script written in a slightly different style)
                // walks straight past. Two such gaps were found live in this very file's first version,
                // and both actually worked against a real document:
                //
                //   Autodesk.Revit.DB.Transaction t = new(Document, "x");   // target-typed `new`:
                //       ImplicitObjectCreationExpressionSyntax, NOT ObjectCreationExpressionSyntax
                //   System.Func<bool> f = Document.Close;                   // method group:
                //       never an InvocationExpressionSyntax at all
                //
                // A constructor is just an IMethodSymbol whose MethodKind is Constructor, so binding to
                // symbols catches every spelling of `new` (explicit, target-typed, via a using-alias)
                // through one code path, and catches a denied method whether it is called or merely
                // referenced.
                var symbol = semanticModel.GetSymbolInfo(node).Symbol;
                if (symbol is IMethodSymbol method)
                {
                    if (method.MethodKind == MethodKind.Constructor)
                    {
                        var constructedType = FullName(method.ContainingType);
                        CheckConstruction(constructedType);
                        // The BOUND-constructor path is the one every ordinary spelling takes (the
                        // fallback below runs only when the symbol did not resolve), so the `using`
                        // requirement has to be applied here too -- found by the tier-1 rows: with the
                        // check only in the fallback, every bare construction sailed through.
                        if (constructedType == SubTransactionType && node is BaseObjectCreationExpressionSyntax boundCreation)
                        {
                            RequireSubTransactionHeldInUsing(boundCreation);
                        }
                    }

                    var lifecycleMember = LifecycleMemberOrNull(method);
                    if (lifecycleMember is not null && seen.Add(lifecycleMember))
                    {
                        lifecycleMembers.Add(lifecycleMember);
                    }

                    continue;
                }

                // BELT AND BRACES for `new`, because check 1 is the one refusal that can never be opted
                // into: judge the CONSTRUCTED TYPE whenever the constructor symbol itself did not
                // resolve. GetSymbolInfo(...).Symbol is null for any late-bound call -- and a `dynamic`
                // argument makes constructor overload resolution late-bound in principle
                // (CandidateReason.LateBound) -- whereas GetTypeInfo still names the type being
                // constructed, since `new T(...)` is statically typed T however its overload is picked.
                //
                // LIVE FINDING, worth recording rather than implying: this is defense in depth, NOT a
                // hole that was open. Every spelling we could construct was ALREADY rejected against a
                // real Revit 2027 document -- `dynamic d = Document; new Transaction(d, "x")`, both
                // arguments dynamic, the target-typed `Transaction t = new(d, "x")`, and assigning the
                // result to a `dynamic` -- because Roslyn still reports the bound constructor symbol for
                // an object creation with dynamic arguments. (`dynamic` itself is genuinely usable from a
                // script here: `dynamic d = 1; d.ToString()` runs, so Microsoft.CSharp is present and the
                // premise was testable, not vacuous.) The fallback exists so this does not silently
                // depend on that Roslyn detail continuing to hold.
                if (node is BaseObjectCreationExpressionSyntax creation)
                {
                    var constructedType = FullName(semanticModel.GetTypeInfo(node).Type);
                    CheckConstruction(constructedType);
                    if (constructedType == SubTransactionType)
                    {
                        RequireSubTransactionHeldInUsing(creation);
                    }
                }

                // COMPILER-SYNTHESIZED DISPOSE (v1 integrated review; live-exploitable before this):
                // `using (Document) { }` and `using var d = Document;` close the ambient document when
                // the scope ends -- Dispose is Close under another spelling, gated above -- but the
                // Dispose call they imply never appears as a bindable node: the compiler synthesizes
                // it, so the IMethodSymbol path above sees nothing. And `using` on an IDisposable is
                // precisely the idiomatic, plausible-looking mistake this gate exists for -- unlike
                // `dynamic` (the documented accepted gap), no deliberate intent is needed to write it.
                // Judged by the RESOURCE EXPRESSION's bound type -- still the semantic model, never
                // syntax text -- so aliases and `var` flow through identically.
                switch (node)
                {
                    case UsingStatementSyntax usingStatement:
                        if (usingStatement.Expression is not null)
                        {
                            RegisterSynthesizedDispose(semanticModel, usingStatement.Expression, seen, lifecycleMembers);
                        }

                        if (usingStatement.Declaration is not null)
                        {
                            foreach (var variable in usingStatement.Declaration.Variables)
                            {
                                if (variable.Initializer is not null)
                                {
                                    RegisterSynthesizedDispose(semanticModel, variable.Initializer.Value, seen, lifecycleMembers);
                                }
                            }
                        }

                        break;

                    case LocalDeclarationStatementSyntax localDeclaration when localDeclaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword):
                        foreach (var variable in localDeclaration.Declaration.Variables)
                        {
                            if (variable.Initializer is not null)
                            {
                                RegisterSynthesizedDispose(semanticModel, variable.Initializer.Value, seen, lifecycleMembers);
                            }
                        }

                        break;

                    // INTERFACE-DISPATCH LAUNDERING of the same member:
                    // `((System.IDisposable)Document).Dispose()` binds to System.IDisposable.Dispose,
                    // a containing type the gated table deliberately doesn't list (it would gate every
                    // Dispose in .NET). The cast/`as`/pattern node is where the gated type is still
                    // visible, so that is what's judged -- converting a gated-Dispose type to
                    // IDisposable has essentially one use from script scope. The operand is judged
                    // through any wrapping parentheses and intermediate casts (PR review finding:
                    // `((System.IDisposable)(object)Document)` otherwise laundered via `object` in one
                    // extra step). An implicit conversion with no conversion syntax at all
                    // (`System.IDisposable x = Document;`) is NOT caught: that is the same
                    // deliberate-laundering bucket as `dynamic`, accepted and documented at
                    // LifecycleMembersByType.
                    case CastExpressionSyntax cast
                        when FullName(semanticModel.GetTypeInfo(cast.Type).Type) == "System.IDisposable":
                        RegisterSynthesizedDispose(semanticModel, PeelConversions(cast.Expression), seen, lifecycleMembers);
                        break;

                    case BinaryExpressionSyntax asExpression
                        when asExpression.IsKind(SyntaxKind.AsExpression) &&
                             FullName(semanticModel.GetTypeInfo(asExpression.Right).Type) == "System.IDisposable":
                        RegisterSynthesizedDispose(semanticModel, PeelConversions(asExpression.Left), seen, lifecycleMembers);
                        break;

                    // Same laundering through PATTERN forms (PR review finding): `Document is
                    // System.IDisposable d` and `case System.IDisposable d:` / `System.IDisposable _ =>`
                    // hand out an IDisposable-typed binding to the same effect as the cast above. The
                    // judged type is the pattern's INPUT EXPRESSION's -- found by walking up to the
                    // is-expression / switch statement / switch expression the pattern belongs to --
                    // since that is where the gated type is visible.
                    case DeclarationPatternSyntax declarationPattern
                        when FullName(semanticModel.GetTypeInfo(declarationPattern.Type).Type) == "System.IDisposable"
                             && PatternInputExpression(declarationPattern) is { } declarationInput:
                        RegisterSynthesizedDispose(semanticModel, PeelConversions(declarationInput), seen, lifecycleMembers);
                        break;

                    case TypePatternSyntax typePattern
                        when FullName(semanticModel.GetTypeInfo(typePattern.Type).Type) == "System.IDisposable"
                             && PatternInputExpression(typePattern) is { } typePatternInput:
                        RegisterSynthesizedDispose(semanticModel, PeelConversions(typePatternInput), seen, lifecycleMembers);
                        break;
                }
            }
        }

        return lifecycleMembers.Count == 0 ? ScriptApiAnalysis.Clean : new ScriptApiAnalysis(lifecycleMembers);
    }

    /// <summary>
    /// Refuses a SubTransaction construction that is not the resource of a `using` statement or `using`
    /// declaration -- see <see cref="DeniedConstructedTypes"/>' comment for the live crash this closes.
    /// Judged on SYNTAX (the creation's parent chain), which is the right tool here: whether the object is
    /// disposed at scope end is a property of where it sits in the tree, not of what it binds to. Accepted
    /// shapes: <c>using (var st = new SubTransaction(doc))</c>, <c>using var st = new SubTransaction(doc);</c>,
    /// <c>using (new SubTransaction(doc))</c>, and the target-typed/aliased spellings of each -- the type
    /// was already resolved semantically by the caller, so any spelling reaches here identically.
    /// </summary>
    private static void RequireSubTransactionHeldInUsing(BaseObjectCreationExpressionSyntax creation)
    {
        SyntaxNode? node = creation;
        // Walk up through the wrappers a `using` resource can legitimately carry: parentheses, a cast
        // (`using (var d = (IDisposable)new SubTransaction(doc))`), and the declarator/declaration
        // pair of a declared resource.
        while (node.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
        {
            node = node.Parent;
        }

        var parent = node.Parent;
        switch (parent)
        {
            case UsingStatementSyntax usingStatement when usingStatement.Expression == node:
                return;
            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }:
                switch (declaration.Parent)
                {
                    case UsingStatementSyntax:
                        return;
                    case LocalDeclarationStatementSyntax local when local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword):
                        return;
                }

                break;
        }

        throw ScriptApiDenylistViolationException.Denied(
            SubTransactionType,
            "A SubTransaction is allowed as a savepoint inside the connector's transaction, but only when " +
            "it is held in a `using`: one still active when the enclosing transaction closes is accepted " +
            "silently by Revit and has crashed it afterwards, and disposing it is what prevents that.",
            "Write `using (var st = new Autodesk.Revit.DB.SubTransaction(doc)) { st.Start(); ... st.Commit(); }` " +
            "(or RollBack()) so it is disposed when the block ends.");
    }

    /// <summary>
    /// Registers <c>&lt;Type&gt;.Dispose</c> as a used lifecycle member when <paramref name="resource"/>'s
    /// bound type has a gated Dispose -- the shared tail of every compiler-synthesized/interface-laundered
    /// Dispose shape above. A type with no gated Dispose (every ordinary IDisposable a script legitimately
    /// uses -- StreamWriter, FileStream, ...) registers nothing.
    /// </summary>
    private static void RegisterSynthesizedDispose(SemanticModel semanticModel, ExpressionSyntax resource, HashSet<string> seen, List<string> lifecycleMembers)
    {
        RegisterSynthesizedDisposeByTypeName(FullName(semanticModel.GetTypeInfo(resource).Type), seen, lifecycleMembers);
    }

    private static void RegisterSynthesizedDisposeByTypeName(string? typeName, HashSet<string> seen, List<string> lifecycleMembers)
    {
        if (typeName is null
            || !LifecycleMembersByType.TryGetValue(typeName, out var gatedMembers)
            || !gatedMembers.Contains("Dispose"))
        {
            return;
        }

        var member = $"{typeName}.Dispose";
        if (seen.Add(member))
        {
            lifecycleMembers.Add(member);
        }
    }

    /// <summary>
    /// The expression a pattern is matched AGAINST -- its input -- found by walking up to the nearest
    /// is-expression, switch statement, or switch expression. Returns null for a pattern whose input
    /// isn't one of those carriers (e.g. a nested property subpattern, where the input is a member of
    /// the governing expression rather than the expression itself -- judging the governing expression
    /// there would over-gate, so those are left to the documented implicit-conversion accepted bucket).
    /// </summary>
    private static ExpressionSyntax? PatternInputExpression(SyntaxNode pattern)
    {
        for (var node = pattern.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case IsPatternExpressionSyntax isPattern:
                    return isPattern.Expression;
                case SwitchStatementSyntax switchStatement:
                    return switchStatement.Expression;
                case SwitchExpressionSyntax switchExpression:
                    return switchExpression.GoverningExpression;
                case SubpatternSyntax:
                    return null; // nested property subpattern -- input is not the governing expression
                case PatternSyntax:
                case CasePatternSwitchLabelSyntax:
                case SwitchSectionSyntax:
                case SwitchExpressionArmSyntax:
                    continue; // still inside the pattern's own carrier chain -- keep walking up
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Unwraps parentheses and intermediate casts around a conversion's operand so the judged type is
    /// the innermost expression's -- what a chained-cast laundering shape is actually converting.
    /// </summary>
    private static ExpressionSyntax PeelConversions(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case CastExpressionSyntax innerCast:
                    expression = innerCast.Expression;
                    continue;
                case BinaryExpressionSyntax innerAs when innerAs.IsKind(SyntaxKind.AsExpression):
                    expression = innerAs.Left;
                    continue;
                default:
                    return expression;
            }
        }
    }

    /// <summary>
    /// Throws if <paramref name="typeName"/> is a type no script may construct (check 1). Takes the
    /// resolved type NAME rather than a symbol so both callers in <see cref="Analyze"/> -- the bound
    /// constructor's containing type, and the constructed type of an object creation whose constructor
    /// did not bind -- reach the identical refusal with the identical message.
    /// </summary>
    private static void CheckConstruction(string? typeName)
    {
        if (typeName is null || !DeniedConstructedTypes.Contains(typeName))
        {
            return;
        }

        throw ScriptApiDenylistViolationException.Denied(
            typeName,
            "The connector owns every transaction: it opens one for you, with warning/error capture, and " +
            "commits it when your block ends -- a transaction you construct yourself would bypass that capture " +
            "and cannot coexist with the run's own group.",
            "Write inside Connector.WithTransaction(doc, () => { ... }) instead; reads need no block. For a " +
            "savepoint inside the block, a native Autodesk.Revit.DB.SubTransaction held in a `using` is " +
            "permitted: Start it, then Commit or RollBack it before the block ends.");
    }

    /// <summary>
    /// The fully-qualified name of <paramref name="method"/> if it is one of the confirmation-gated
    /// lifecycle members, otherwise null. Returning rather than throwing is the whole point of check 2's
    /// shape: whether this is allowed cannot be decided here, because it depends on the request.
    /// </summary>
    private static string? LifecycleMemberOrNull(IMethodSymbol method)
    {
        var containingType = FullName(method.ContainingType);
        if (containingType is null
            || !LifecycleMembersByType.TryGetValue(containingType, out var gatedMembers)
            || !gatedMembers.Contains(method.Name))
        {
            return null;
        }

        return $"{containingType}.{method.Name}";
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
