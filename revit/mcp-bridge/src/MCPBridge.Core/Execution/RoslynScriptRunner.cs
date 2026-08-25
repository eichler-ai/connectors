using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
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
/// - Every *run* -- cached compilation or not -- still emits and loads a fresh
///   assembly into its own short-lived, collectible AssemblyLoadContext via
///   AssemblyLoadContext.EnterContextualReflection(), unloaded once the run
///   completes and its result is captured. This is what actually reclaims memory:
///   caching the Script object only saves recompilation, it does not keep the
///   emitted assembly (or its ALC) alive across runs.
/// </summary>
public sealed class RoslynScriptRunner
{
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

        var alc = _alcFactory();
        var originalOut = Console.Out;
        var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);

            // Run a fresh functional clone of the cached Script<object>, never the cached
            // instance itself: Roslyn's Script<T> memoizes its compiled "executor" delegate
            // on the instance after its first RunAsync, which pins whatever assembly that
            // run loaded (i.e. this call's ALC) for as long as anything holds the Script
            // object -- and our LRU deliberately holds it across many later runs. Cloning
            // via WithOptions (a functional no-op here) gets an instance that reuses the
            // already-bound Compilation (so parse/bind is still skipped -- the actual cache
            // win) but starts with its own empty executor slot, so nothing outlives this
            // call to keep the ALC alive once it's unloaded below.
            var runScript = script.WithOptions(script.Options);

            ScriptState<object> state;
            using (alc.EnterContextualReflection())
            {
                state = await runScript.RunAsync(globals, cancellationToken).ConfigureAwait(false);
            }

            if (state.Exception is not null)
            {
                return ScriptExecutionOutcome.Failed(state.Exception, writer.ToString());
            }

            return ScriptExecutionOutcome.Completed(state.ReturnValue, writer.ToString());
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

    private Script<object> GetOrCompile(string scriptText)
    {
        if (_cache.TryGet(scriptText, out var cached))
        {
            return cached!;
        }

        var script = CSharpScript.Create<object>(scriptText, _options, typeof(ScriptGlobals));

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
}
