using System;
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
///   Script&lt;object&gt; per unique script text, so a verbatim re-run skips
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
/// </summary>
public sealed class RoslynScriptRunner
{
    private const string SubmissionTypeName = "Submission#0";
    private const string FactoryMethodName = "<Factory>";

    private readonly ScriptCompilationCache _cache;
    private readonly Func<AssemblyLoadContext> _alcFactory;
    private readonly Action? _compileCounter;
    private readonly ScriptOptions _options;

    public RoslynScriptRunner(int cacheCapacity = 32, Func<AssemblyLoadContext>? alcFactory = null, Action? compileCounter = null)
    {
        _cache = new ScriptCompilationCache(cacheCapacity);
        _alcFactory = alcFactory ?? (() => new AssemblyLoadContext($"mcpbridge-script-{Guid.NewGuid()}", isCollectible: true));
        _compileCounter = compileCounter;
        _options = ScriptOptions.Default
            .WithReferences(LoadableReferences())
            .WithImports("System");
    }

    private static Assembly[] LoadableReferences() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .ToArray();

    public async Task<ScriptExecutionOutcome> RunAsync(string scriptText, ScriptGlobals globals, CancellationToken cancellationToken)
    {
        Script<object> script;
        try
        {
            script = GetOrCompile(scriptText);
        }
        catch (CompilationErrorException ex)
        {
            return ScriptExecutionOutcome.Failed(ex, stdOut: "");
        }
        catch (ScriptAwaitNotAllowedException ex)
        {
            return ScriptExecutionOutcome.Failed(ex, stdOut: "");
        }

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
        catch (OperationCanceledException)
        {
            return ScriptExecutionOutcome.Cancelled(writer.ToString());
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
            // would so callers see the real script exception, not a reflection wrapper.
            throw tie.InnerException;
        }

        return await resultTask.ConfigureAwait(false);
    }

    private Script<object> GetOrCompile(string scriptText)
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

        _compileCounter?.Invoke();
        _cache.Set(scriptText, script);
        return script;
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
        var hasAwait = tree.GetRoot().DescendantNodes().OfType<AwaitExpressionSyntax>().Any();
        if (hasAwait)
        {
            throw new ScriptAwaitNotAllowedException();
        }
    }
}
