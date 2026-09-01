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
///
/// The exact snippet above no longer compiles as written, because issue #91 moved Publish behind the
/// Connector facade. Do not read that as the hole having grown a second lock: this type being
/// internal is still the only thing closing it, and reflection still reaches everything, which is the
/// accepted guard-not-sandbox line.
///
/// What #91 did change is how much the Delegate.Target route yields. It now yields the Connector
/// facade -- one private IConnectorRuntime reference -- rather than the live ScriptGlobals.
///
/// An earlier version of this comment claimed the shape was undiminished because "Document is still a
/// bare global over the live ScriptGlobals, so a method-group delegate over it hands back the same
/// object". That is WRONG, and worth leaving recorded because it is the exact error this file warns
/// about elsewhere: asserting a capability without trying it. Document is a PROPERTY, not a method, so
/// there is no method group to convert and no delegate whose Target is the globals object. After #91
/// every public instance member of ScriptGlobals is a property, so the Target route to ScriptGlobals
/// is gone entirely -- narrower than the paragraph above used to claim, and still not the thing
/// holding the boundary.
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
            // Eichler.Connectors.Revit is appended EXPLICITLY, not left to the scan, and this is
            // load-bearing rather than defensive. GetAssemblies() reports what the CLR has actually
            // loaded, and the CLR loads lazily: a referenced assembly appears only once something
            // touches a type in it. This runner is constructed at add-in startup, before any
            // ScriptGlobals exists, so at that moment nothing has touched Connector and the scan would
            // not list it -- leaving every script that writes `Connector.Publish(...)` to fail
            // compilation with the assembly simply absent from its references. The typeof() below is
            // what forces the load, so the reference is present rather than merely likely.
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            // Appended AFTER the filter, not before. Before it, the deliberately-added reference was
            // subject to the very predicate it exists to bypass: an assembly loaded with an empty Location
            // (single-file bundling, Assembly.Load(byte[]), an ILMerge step) would be silently dropped and
            // every script writing Connector.Publish(...) would fail with a bare CS0103 pointing nowhere.
            // The comment above claimed the reference was "present rather than merely likely", which is
            // only true in this order (review finding).
            .Append(typeof(Eichler.Connectors.Revit.Connector).Assembly)
            .Distinct()
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
        // #67: the compile-time rejection is a pure property of the script text and runs identically here
        // (on whatever thread reached execution) and in TryPreflight (on the connection thread, before the
        // ExternalEvent is raised). Sharing the one method keeps the two from ever drifting -- a script the
        // pre-flight accepts must never be rejected at run time, and vice versa.
        var rejection = TryPreflight(scriptText, confirmLifecycleActions);
        if (rejection is not null)
        {
            return rejection;
        }

        // Cache HIT: TryPreflight just compiled (or found) this exact text, so this returns the cached
        // CompiledScript with no re-parse/emit/analyze (LruCache is fully locked; the compile is not redone).
        var compiled = GetOrCompile(scriptText);

        var alc = _alcFactory();
        var writer = new StringWriter();

        // AsyncLocal-scoped capture, not a process-global Console.SetOut swap (issue #52, resolving
        // #35 properly): the global swap leaked any OTHER thread's console writes into this script's
        // stdout, and -- measured consequence -- made parallel test classes (xunit's default) a latent
        // cross-capture race. The ambient writer flows with the execution context, so only THIS
        // run's code (and whatever it awaits) writes into `writer`; everything else keeps the real
        // console. A script that calls Console.SetOut itself can still stomp the process-wide router,
        // exactly as it could stomp the old swap -- unchanged, accepted, same bucket as reflection.
        using var capture = ScriptConsoleCapture.Begin(writer);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await InvokeInFreshLoadContextAsync(compiled, globals, alc).ConfigureAwait(false);
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
            alc.Unload();
        }
    }

    /// <summary>
    /// #67: the compile-time half of <see cref="RunAsync"/> — compile, the top-level-<c>await</c> rejection,
    /// the denylist, and the per-request lifecycle gate — with NO execution. Returns the same
    /// <see cref="ScriptExecutionOutcome"/> failure <see cref="RunAsync"/> would for a rejected script, or
    /// <c>null</c> if the script compiles and is allowed to run.
    ///
    /// Every rejection here is a pure property of <paramref name="scriptText"/> (plus
    /// <paramref name="confirmLifecycleActions"/>): compilation reads only file-metadata references, never a
    /// live Revit object, and the denylist analyses bound symbols — the same reason <see cref="WarmupCompile"/>
    /// runs on a background thread and the denylist checks are unit-testable with no live Revit. So the
    /// dispatcher can call this on the connection thread BEFORE raising the ExternalEvent and reject an
    /// invalid script immediately and deterministically, instead of queuing it behind a congested UI thread
    /// where the rejection is delayed past timeout_ms and misreported as `running` (issue #67). On success it
    /// leaves the compiled script warm in the thread-safe cache, so the UI-thread run reuses it with no
    /// recompile.
    /// </summary>
    internal ScriptExecutionOutcome? TryPreflight(string scriptText, bool confirmLifecycleActions = false)
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

        // PRD §14, the per-request half of the denylist. Deliberately after compilation (cached or not) and
        // before anything is emitted or executed, so a refused run has the same "nothing happened" property
        // as the compile-time rejections above. Reached identically on a cache HIT and a cache MISS, which is
        // the whole point: an unconfirmed rerun of a script confirmed a moment ago is still refused.
        if (compiled.Analysis.RequiresLifecycleConfirmation && !confirmLifecycleActions)
        {
            return ScriptExecutionOutcome.Failed(
                ScriptApiDenylistViolationException.LifecycleConfirmationRequired(compiled.Analysis.LifecycleMembers),
                stdOut: "");
        }

        return null;
    }

    /// <summary>
    /// Emits the script's already-bound Compilation, loads it into <paramref name="alc"/>, and invokes the
    /// generated submission entry point directly -- see the class doc comment for why this replaces
    /// Script&lt;object&gt;.RunAsync(). Isolated into its own method (not inlined into RunAsync) so that once
    /// it returns, none of its locals (the loaded Assembly, its Type/MethodInfo, the emitted MemoryStream)
    /// are still rooted on RunAsync's stack frame -- that matters for the ALC to actually become collectible
    /// once Unload() is called in RunAsync's finally block.
    /// </summary>
    private static async Task<object?> InvokeInFreshLoadContextAsync(CompiledScript compiled, ScriptGlobals globals, AssemblyLoadContext alc)
    {
        // Emitted at most once per compiled script (issue #52) -- see CompiledScript.GetOrEmitPeImage
        // for the caching contract. A verbatim re-run (the LRU's whole purpose) now skips straight to
        // ALC-load + invoke.
        var peImage = compiled.GetOrEmitPeImage(EmitToPeImage);

        using var peStream = new MemoryStream(peImage, writable: false);
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
    /// <summary>
    /// Emits one compiled script's Compilation to a PE image. Split out so
    /// <see cref="CompiledScript.GetOrEmitPeImage"/> can own the once-per-script caching while the
    /// emit mechanics (and the emit-failure exception shape RunAsync's catch understands) stay here.
    /// </summary>
    private static byte[] EmitToPeImage(Script<object> script)
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

        return peStream.ToArray();
    }

    /// <summary>
    /// Compiles, analyzes, and emits a trivial marker script, swallowing every failure -- the
    /// startup warmup (issue #52). The first real script in a Revit session otherwise pays Roslyn's
    /// own cold start (assembly JIT, reference-metadata load: seconds) inside the agent's first
    /// execute_script call; running it here, on whatever background thread the caller chooses (none
    /// of this touches the Revit API context), hides that cost inside Revit's startup instead.
    /// Failure is deliberately silent to the caller -- warmup must never affect startup -- and the
    /// cost is one LRU slot for the marker text.
    /// </summary>
    internal void WarmupCompile()
    {
        try
        {
            var compiled = GetOrCompile("/* mcpbridge warmup */ 1 + 1");
            compiled.GetOrEmitPeImage(EmitToPeImage);
        }
        catch
        {
            // Best-effort by contract; the first real script simply pays the cold start as before.
        }
    }

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
