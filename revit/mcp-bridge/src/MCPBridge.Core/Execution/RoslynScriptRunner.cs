using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Compiles and runs script text via Microsoft.CodeAnalysis.CSharp.Scripting (PRD §06
/// step 3, and "Roslyn isolation &amp; memory lifecycle"). Two separate mechanisms, per
/// the PRD:
///
/// - A small bounded LRU (<see cref="ScriptCompilationCache"/>) caches the *compiled*
///   Script&lt;object&gt; -- plus everything else derived purely from the script text, see
///   <see cref="CompiledScript"/> -- per unique script text, so a verbatim re-run skips
///   parse/bind, bounded so it doesn't grow unbounded across a long session.
/// - Every *run* -- cached compilation or not -- emits a fresh assembly and loads it into
///   its own short-lived, collectible AssemblyLoadContext, unloaded once the run completes
///   and its result is captured.
///
/// PR #2 review finding (Fix 2), and why this does NOT use Script&lt;object&gt;.RunAsync():
/// the original implementation tried to isolate each run via
/// AssemblyLoadContext.EnterContextualReflection() plus script.WithOptions(script.Options)
/// as a same-options "clone". Neither did anything: (a) Roslyn's own
/// Microsoft.CodeAnalysis.Scripting.Hosting.InteractiveAssemblyLoader manages its own
/// internal load context for RunAsync and ignores ambient contextual-reflection state
/// entirely -- confirmed empirically (see the phase-01 implementation report) by loading a
/// trivial script and inspecting AssemblyLoadContext.GetLoadContext() on the generated
/// submission assembly: it always lands in a load context with IsCollectible == false,
/// regardless of any EnterContextualReflection() scope -- so it can never be unloaded; and
/// (b) Script.WithOptions(options) with the *same* options instance is a documented Roslyn
/// no-op that returns `this`, not a clone, so the "fresh executor slot" the old comment
/// described never existed -- the cached Script kept memoizing (and thus pinning) whatever
/// assembly its most recent run loaded.
///
/// The fix compiles once (cached, as before) but executes each run through a hand-rolled
/// path that genuinely isolates and reclaims memory: get the already-bound Compilation off
/// the cached Script (<see cref="Script{T}.GetCompilation"/>), Emit() it into a MemoryStream
/// (Emit is a pure, repeatable operation against an immutable Compilation -- this does not
/// mutate or pin anything from a prior run), load the resulting assembly into a fresh
/// collectible AssemblyLoadContext we own, and invoke its generated entry point directly via
/// reflection (Roslyn scripting compiles every submission to a `Submission#0.&lt;Factory&gt;
/// (object[]) : Task&lt;object&gt;` static method -- an internal codegen convention, not a
/// public contract, but stable across the 4.11.0 Microsoft.CodeAnalysis.CSharp.Scripting
/// used here and empirically verified the same way). Once the run completes and its result
/// is captured, every local reference to the assembly/type/method is dropped and the ALC is
/// unloaded -- verified end-to-end with a WeakReference GC test in
/// RoslynScriptRunnerTests (RunAsync_MemoryIsActuallyReclaimed_AfterUnload), not just "was
/// Unload() called".
///
/// INTERNAL, AND THAT IS A SECURITY BOUNDARY, NOT A STYLE CHOICE (third-round review finding,
/// live-verified before and after). While this type was public, a script could start a NESTED run
/// and self-grant the confirmation-gated tier, with no reflection and without naming a single
/// internal type:
///
///     var g = (MCPBridge.Core.Execution.ScriptGlobals)((System.Action&lt;string, string&gt;)Publish).Target;
///     new MCPBridge.Core.Execution.RoslynScriptRunner()
///         .RunAsync("...", g, CancellationToken, confirmLifecycleActions: true)
///         .GetAwaiter().GetResult();
///
/// Two public surfaces composed into it. ScriptGlobals has to stay public -- Roslyn binds a
/// script's globals by name against a public type -- and Delegate.Target is public on every
/// delegate, so a method-group delegate over any ScriptGlobals instance member (Publish here)
/// hands the script the LIVE globals object. From there, a public RunAsync taking the
/// confirmation flag as an ordinary parameter let the SCRIPT decide what only the REQUEST is
/// allowed to decide. Confirmed live against Revit 2027 before the fix: an execute_script call
/// that never set confirm_lifecycle_actions ran a nested script binding Document.Save and
/// reported success. Check 1 (transaction construction) was never affected -- the nested compile
/// still runs the full ScriptApiDenylist.Analyze, which refuses it unconditionally -- so what
/// this closes is the gated tier specifically. Pinned by
/// revit/test-harness/denylist_bypass_test.go (TestConfirmationTierCannotBeSelfGranted); the
/// only production caller is TransactionScriptExecutor, itself internal for the same reason.
/// </summary>
internal sealed class RoslynScriptRunner
{
    private const string SubmissionTypeName = "Submission#0";
    private const string FactoryMethodName = "<Factory>";

    private readonly ScriptCompilationCache _cache;
    private readonly Func<AssemblyLoadContext> _alcFactory;
    private readonly Action? _compileCounter;
    private readonly ScriptOptions _options;

    /// <param name="additionalMetadataReferencePaths">
    /// Extra assemblies to make bindable from script scope, referenced by FILE PATH (metadata only)
    /// rather than as loaded assemblies. Unused in production and null by default: inside a live Revit
    /// process RevitAPI/RevitAPIUI are already loaded, so <see cref="LoadableReferences"/> picks them up
    /// on its own. It exists for MCPBridge.Core.Tests, which cannot get them that way -- RevitAPI.dll is
    /// a mixed-mode C++/CLI assembly that only Revit's own native host can load (Assembly.LoadFrom on it
    /// elsewhere throws "An attempt was made to load a program with an incorrect format", confirmed
    /// live). Roslyn, however, only ever reads its managed METADATA, which works fine from the file. That
    /// is what keeps the compile-time ScriptApiDenylist checks fully unit-testable with no live Revit
    /// (PRD §14) -- the scripts in those tests are rejected before anything is ever emitted or executed,
    /// so nothing needs the assembly to actually load.
    /// </param>
    /// <param name="additionalMetadataReferences">
    /// Same purpose as <paramref name="additionalMetadataReferencePaths"/>, taking ALREADY-BUILT
    /// references. Also tests-only, and it exists for test-suite wall clock (test-quality pass):
    /// <c>MetadataReference.CreateFromFile</c> re-parses RevitAPI.dll/RevitAPIUI.dll's metadata on
    /// every call, and the tier-1 suites construct ~100 runners -- a shared, once-per-process pair of
    /// references (see the test project's RevitApiReference.References) removes that repeated parse
    /// without sharing any runner state between tests.
    /// </param>
    public RoslynScriptRunner(
        int cacheCapacity = 32,
        Func<AssemblyLoadContext>? alcFactory = null,
        Action? compileCounter = null,
        IEnumerable<string>? additionalMetadataReferencePaths = null,
        IEnumerable<MetadataReference>? additionalMetadataReferences = null)
    {
        _cache = new ScriptCompilationCache(cacheCapacity);
        _alcFactory = alcFactory ?? (() => new AssemblyLoadContext($"mcpbridge-script-{Guid.NewGuid()}", isCollectible: true));
        _compileCounter = compileCounter;
        _options = ScriptOptions.Default
            .WithReferences(LoadableReferences())
            .WithImports("System");

        if (additionalMetadataReferencePaths is not null)
        {
            _options = _options.AddReferences(
                additionalMetadataReferencePaths.Select(path => MetadataReference.CreateFromFile(path)));
        }

        if (additionalMetadataReferences is not null)
        {
            _options = _options.AddReferences(additionalMetadataReferences);
        }
    }

    private static Assembly[] LoadableReferences() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .ToArray();

    /// <param name="confirmLifecycleActions">
    /// The request's own <c>confirm_lifecycle_actions</c> flag (PRD §14). PER-REQUEST by nature -- the same
    /// script text can arrive once without it and again with it -- which is exactly why it is a parameter of
    /// RUN rather than of compile: compilation is cached by script text, so folding this decision into
    /// GetOrCompile would let one run's answer be reused for a request that asked something different.
    /// Detection of whether the script needs it IS cached (it depends only on the text); only the decision
    /// is made here.
    /// </param>
    public async Task<ScriptExecutionOutcome> RunAsync(
        string scriptText,
        ScriptGlobals globals,
        CancellationToken cancellationToken,
        bool confirmLifecycleActions = false)
    {
        CompiledScript compiled;
        try
        {
            compiled = GetOrCompile(scriptText);
        }
        catch (CompilationErrorException ex)
        {
            return ScriptExecutionOutcome.Failed(ex, stdOut: "");
        }
        catch (ScriptAwaitNotAllowedException ex)
        {
            return ScriptExecutionOutcome.Failed(ex, stdOut: "");
        }
        catch (ScriptApiDenylistViolationException ex)
        {
            // Surfaced through the identical path as the two above (PRD §14): the caller
            // (TransactionScriptExecutor) rolls back its ambient Transaction/TransactionGroup exactly as
            // it does for any other pre-execution failure, so the denylist needed no new failure handling.
            return ScriptExecutionOutcome.Failed(ex, stdOut: "");
        }

        // PRD §14, the per-request half of the denylist. Deliberately placed here -- after compilation
        // (cached or not), before the ALC is created and before anything is emitted or executed -- so a
        // refused run has the same "nothing happened" property as the unconditional compile-time
        // rejections above: TransactionScriptExecutor rolls its ambient transaction back and the document
        // is untouched. Note this is reached identically on a cache HIT and a cache MISS, which is the
        // whole point: an unconfirmed rerun of a script that was confirmed a moment ago is still refused.
        if (compiled.Analysis.RequiresLifecycleConfirmation && !confirmLifecycleActions)
        {
            return ScriptExecutionOutcome.Failed(
                ScriptApiDenylistViolationException.LifecycleConfirmationRequired(compiled.Analysis.LifecycleMembers),
                stdOut: "");
        }

        var script = compiled.Script;
        var alc = _alcFactory();
        var originalOut = Console.Out;
        var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);
            cancellationToken.ThrowIfCancellationRequested();

            var result = await InvokeInFreshLoadContextAsync(script, globals, alc).ConfigureAwait(false);
            return ScriptExecutionOutcome.Completed(result, writer.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ScriptExecutionOutcome.Cancelled(writer.ToString());
        }
        catch (OperationCanceledException ex)
        {
            // v1 integrated review: an OperationCanceledException the SCRIPT itself threw (its own
            // code, or a Revit call surfacing one) with no cancellation ever requested is a script
            // failure, not a cancellation -- PRD §06 defines `cancelled` as "the agent asked for
            // this", and reporting it otherwise records a cancel nobody issued. The when-guard
            // above keeps the genuine path (token signalled, script observed it) exactly as it was.
            return ScriptExecutionOutcome.Failed(ex, writer.ToString());
        }
        catch (CompilationErrorException ex)
        {
            return ScriptExecutionOutcome.Failed(ex, writer.ToString());
        }
        catch (Exception ex)
        {
            return ScriptExecutionOutcome.Failed(ex, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            alc.Unload();
        }
    }

    /// <summary>
    /// Emits the script's already-bound Compilation, loads it into <paramref name="alc"/>, and invokes the
    /// generated submission entry point directly -- see the class doc comment for why this replaces
    /// Script&lt;object&gt;.RunAsync(). Isolated into its own method (not inlined into RunAsync) so that once
    /// it returns, none of its locals (the loaded Assembly, its Type/MethodInfo, the emitted MemoryStream)
    /// are still rooted on RunAsync's stack frame -- that matters for the ALC to actually become collectible
    /// once Unload() is called in RunAsync's finally block.
    /// </summary>
    private static async Task<object?> InvokeInFreshLoadContextAsync(Script<object> script, ScriptGlobals globals, AssemblyLoadContext alc)
    {
        var compilation = script.GetCompilation();

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
            throw new CompilationErrorException(
                string.Join(Environment.NewLine, errors.Select(e => e.ToString())),
                ImmutableArray.CreateRange(errors));
        }

        peStream.Position = 0;
        var assembly = alc.LoadFromStream(peStream);

        var submissionType = assembly.GetType(SubmissionTypeName)
            ?? throw new InvalidOperationException(
                $"Roslyn's generated submission type '{SubmissionTypeName}' was not found in the emitted assembly. " +
                "This is an internal Roslyn scripting codegen convention (not a public contract) and this indicates " +
                "the Microsoft.CodeAnalysis.CSharp.Scripting version in use no longer matches it.");

        var factory = submissionType.GetMethod(FactoryMethodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"Roslyn's generated submission entry point '{SubmissionTypeName}.{FactoryMethodName}' was not found. " +
                "This is an internal Roslyn scripting codegen convention (not a public contract) and this indicates " +
                "the Microsoft.CodeAnalysis.CSharp.Scripting version in use no longer matches it.");

        // The submission state array convention: index 0 is the globals object, index 1 is a slot Roslyn
        // fills in with this submission's own generated instance (used to persist state across a chained
        // REPL-style session; unused here since every MCP Bridge script is a standalone, unchained
        // submission). Verified empirically against Microsoft.CodeAnalysis.CSharp.Scripting 4.11.0.
        var submissionStates = new object?[] { globals, null };

        Task<object> resultTask;
        try
        {
            resultTask = (Task<object>)factory.Invoke(null, new object?[] { submissionStates })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // A synchronous throw from Invoke() (as opposed to the returned Task faulting) can only happen
            // before the generated async state machine's first await point; unwrap it the same way `await`
            // would so callers see the real script exception, not a reflection wrapper. Second review
            // finding: a plain `throw tie.InnerException;` here would reset the exception's stack trace to
            // this line, losing where it actually originated inside the script. ExceptionDispatchInfo
            // preserves the original stack trace across the rethrow the same way `await`-ing a faulted Task
            // does.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw tie.InnerException; // unreachable -- ExceptionDispatchInfo.Throw() never returns, but the compiler can't see that; satisfies flow analysis (this method's return type is not void).
        }

        return await resultTask.ConfigureAwait(false);
    }

    private CompiledScript GetOrCompile(string scriptText)
    {
        // The await check only needs to run on a genuine cache miss: the cache key is the verbatim script
        // text, and only a script that already passed RejectTopLevelAwait below is ever inserted into it
        // (see _cache.Set below), so a cache hit can never newly contain an `await` -- re-checking it on
        // every cached re-run (the common case for a REPL-style agent workflow) would be pure waste
        // (PR #2 review, efficiency finding).
        if (_cache.TryGet(scriptText, out var cached))
        {
            return cached!;
        }

        var script = CSharpScript.Create<object>(scriptText, _options, typeof(ScriptGlobals));
        RejectTopLevelAwait(script);

        // Force a diagnostics pass now so a compile error surfaces on this call
        // (and is never cached), rather than lazily on the first RunAsync.
        var diagnostics = script.Compile();
        var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            throw new CompilationErrorException(
                string.Join(Environment.NewLine, errors.Select(e => e.ToString())),
                ImmutableArray.CreateRange(errors));
        }

        // PRD §14: the denylist needs bound symbols, so it runs only once Compile() has reported no
        // errors -- before that, every expression would resolve to an error symbol and the check could
        // only re-report ordinary compile errors as denylist violations. Placed before _compileCounter/
        // _cache.Set for the same reason RejectTopLevelAwait sits before them: a script rejected
        // UNCONDITIONALLY (transaction construction -- Analyze throws for that) must never be counted as
        // a successful compilation nor enter the cache, or an identical later run would hit the cache and
        // skip the check entirely.
        //
        // What Analyze RETURNS (the confirmation-gated lifecycle members it found) is the opposite case:
        // it is cached on purpose, because it is a property of this script text and cannot change between
        // runs. Only the decision made from it is per-run -- see RunAsync.
        var analysis = ScriptApiDenylist.Analyze(script.GetCompilation());

        _compileCounter?.Invoke();
        var compiled = new CompiledScript(script, analysis);
        _cache.Set(scriptText, compiled);
        return compiled;
    }

    /// <summary>
    /// PR #2 review, Fix 1's confirmed architecture decision: agent-supplied scripts must not contain their
    /// own top-level `await`, because Execute() blocks on the whole script's Task synchronously via
    /// .GetAwaiter().GetResult() (see ExternalEventBridge&lt;TResult&gt;), which is deadlock-safe only when a
    /// script with no internal await runs to completion before its Task is even returned (dotnet/roslyn
    /// #6928). Checked via a syntax-tree walk before compilation -- never silently hung, never silently
    /// dropped (PRD §01 observability-over-silence): a script containing `await` is rejected here every time
    /// it's newly compiled. Walks <paramref name="script"/>'s own already-parsed SyntaxTree (via
    /// GetCompilation()) rather than re-parsing scriptText independently -- CSharpScript.Create doesn't
    /// itself parse eagerly, but this reuses the one parse the compilation pipeline needs anyway instead of
    /// doing a second, redundant one (PR #2 review, efficiency finding).
    /// </summary>
    private static void RejectTopLevelAwait(Script<object> script)
    {
        var tree = script.GetCompilation().SyntaxTrees.Single();

        // TOKENS, not AwaitExpressionSyntax nodes (PR #45 review finding, pre-existing): `await using`
        // and `await foreach` carry their await as a keyword TOKEN on the using/foreach/declaration
        // statement, with no AwaitExpressionSyntax anywhere in the tree -- the same
        // compiler-synthesized-shape class as ScriptApiDenylist's using-Dispose gap, one guard over.
        // A node-typed walk let a script-defined IAsyncDisposable smuggle a genuine yield past this
        // guard, resuming script code off Revit's API context with the ambient transaction open. The
        // ordinary `await expr` form also contains an AwaitKeyword token, so this single check covers
        // every spelling; an identifier merely NAMED await lexes as an IdentifierToken, not this kind,
        // and stays unaffected.
        var hasAwait = tree.GetRoot().DescendantTokens().Any(t => t.IsKind(SyntaxKind.AwaitKeyword));
        if (hasAwait)
        {
            throw new ScriptAwaitNotAllowedException();
        }
    }
}
