using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Eichler.Connectors.Rhino;

namespace Rhino.MCPBridge.Core.Execution.Python;

/// <summary>
/// The Python 3 peer of <see cref="RoslynScriptRunner"/> (PRD §06): the same pre-flight contract (the
/// guard runs on the text, identically here and on the connection thread), the same globals bound under
/// their Python names, the same outcome shape. What differs is stated here rather than hidden:
///
/// - There is no compile step, so a syntax error is reported by CPython at run time and mapped onto
///   <c>script-compilation-failed</c> from the traceback (<see cref="PythonScriptException.IsSyntaxError"/>).
/// - A module has no return statement: the script assigns <c>result</c> and the runner reads it back.
/// - Cancellation is cooperative through <c>cancel</c> (<see cref="CancelSignal"/>); the host cannot interrupt.
/// - The script is prefixed with the <c>#! python 3</c> shebang the host requires and a best-effort
///   preamble (try/except) that points <c>scriptcontext.doc</c> at the routed document, so the
///   rhinoscriptsyntax layer acts on the same document the C# host would; a partially-initialised
///   RhinoCode without scriptcontext on sys.path (issue #287) does not then fail the run. Traceback line
///   numbers are shifted back by the preamble's line count (<see cref="PrefixLines"/>).
/// </summary>
internal sealed class PythonScriptRunner : IScriptRunner
{
    public const string ResultName = "result";
    public static IReadOnlyList<string> GlobalNames { get; } = new[] { "doc", "ghdoc", "connector", "cancel" };

    /// <summary>The lines placed before the script: shebang, then the preamble. Tracebacks are shifted by
    /// their count (<see cref="PrefixLines"/>). The scriptcontext import is best-effort: on Windows a
    /// partially-initialised RhinoCode can leave scriptcontext off sys.path (issue #287), and a hard
    /// import there would fail every run, even a Rhino.Geometry-only one; try/except keeps such runs alive.</summary>
    internal const string Prefix = "#! python 3\ntry: import scriptcontext as __mcp_sc; __mcp_sc.doc = doc\nexcept Exception: pass\n";
    internal const int PrefixLines = 3;

    /// <summary>Run after every script (see RunAsync). Best-effort for the same #287 reason as the preamble.</summary>
    internal const string RestoreScriptContext = "#! python 3\ntry: import scriptcontext as __mcp_sc, Rhino as __mcp_rh; __mcp_sc.doc = __mcp_rh.RhinoDoc.ActiveDoc\nexcept Exception: pass\n";

    /// <summary>A cancellation, not a script error that happened to coincide with one: the .NET
    /// OperationCanceledException from cancel.Check() somewhere in the chain, or its Python spelling
    /// on stderr. A script that swallowed the cancel and then failed on its own keeps its own error.</summary>
    private static bool IsCancellation(Exception error, string stderr)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException) return true;
        }

        return stderr.Contains("OperationCanceledException", StringComparison.Ordinal) || error.Message.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A traceback frame in the script itself. RhinoCode stages the text as a file under
    /// ~/.rhinocode/stage/ (verified live); "&lt;string&gt;" is the host-neutral spelling.</summary>
    private static readonly Regex ScriptFrame = new(@"File ""(?<file>[^""]*(?:[/\\]stage[/\\][^""]*|<string>))"", line (?<line>\d+)", RegexOptions.Compiled);

    /// <summary>RhinoCode's compile error location: <c>file:///…/stage/x.y:[line:col]</c> (verified live).</summary>
    private static readonly Regex CompileLocation = new(@"(?<file>\S*[/\\]stage[/\\]\S*?):\[(?<line>\d+):(?<col>\d+)\]", RegexOptions.Compiled);

    private readonly IPythonHost _host;

    public PythonScriptRunner(IPythonHost host)
    {
        _host = host;
    }

    public string Language => "python";

    /// <summary>The guard is pure text analysis with no warm-up, so pre-flight is always cheap.</summary>
    public bool IsWarm => true;

    public string? UnavailableReason => _host.UnavailableReason;

    public ScriptExecutionOutcome? TryPreflight(string scriptText, bool confirmLifecycleActions)
    {
        PythonScriptGuard.Analysis analysis;
        try
        {
            analysis = PythonScriptGuard.Analyze(scriptText);
        }
        catch (ScriptApiDenylistViolationException ex)
        {
            return ScriptExecutionOutcome.Failed(ex, stdOut: "");
        }

        if (analysis.RequiresLifecycleConfirmation && !confirmLifecycleActions)
        {
            return ScriptExecutionOutcome.Failed(ScriptApiDenylistViolationException.LifecycleConfirmationRequired(analysis.LifecycleMembers), stdOut: "");
        }

        return null;
    }

    public Task<ScriptExecutionOutcome> RunAsync(string scriptText, ScriptGlobals globals, CancellationToken cancellationToken, bool confirmLifecycleActions)
    {
        var rejection = TryPreflight(scriptText, confirmLifecycleActions);
        if (rejection is not null)
        {
            return Task.FromResult(rejection);
        }

        if (_host.UnavailableReason is { } reason)
        {
            return Task.FromResult(ScriptExecutionOutcome.Failed(new InvalidOperationException("the Python host is not available: " + reason), stdOut: ""));
        }

        var inputs = new Dictionary<string, object?>
        {
            ["doc"] = globals.Document,
            ["ghdoc"] = null,
            ["connector"] = globals.Connector,
            ["cancel"] = new CancelSignal(cancellationToken),
        };

        PythonRunResult run;
        try
        {
            run = _host.Run(Prefix + scriptText, inputs, ResultName);
        }
        finally
        {
            // scriptcontext is one module in the one interpreter Rhino's own editor shares (a .NET
            // module with a `doc` property, verified live): point it back at the active document so a
            // run routed to another document does not leave the person's next editor script on it.
            try { _host.Run(RestoreScriptContext, new Dictionary<string, object?>(), ResultName); } catch { }
        }

        if (run.Error is null)
        {
            // stderr on a successful run (warnings, sys.stderr writes) is worth seeing, marked as such.
            var stdout = run.StdErr.Length == 0 ? run.StdOut : run.StdOut + "[stderr]\n" + run.StdErr;
            return Task.FromResult(ScriptExecutionOutcome.Completed(run.Result, stdout));
        }

        if (cancellationToken.IsCancellationRequested && IsCancellation(run.Error, run.StdErr))
        {
            // cancel.Check() raised inside the script.
            return Task.FromResult(ScriptExecutionOutcome.Cancelled(run.StdOut));
        }

        var traceback = ShiftTraceback(run.StdErr);
        return Task.FromResult(ScriptExecutionOutcome.Failed(new PythonScriptException(run.Error, traceback), run.StdOut));
    }

    /// <summary>Rewrites the script's own frames so their line numbers match the text the caller sent
    /// (the prefix lines subtracted) and their file reads <c>&lt;script&gt;</c> instead of the staging
    /// path; library frames keep their numbers. Frames inside the prefix itself are left as they are.</summary>
    internal static string ShiftTraceback(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return "";
        }

        var shifted = ScriptFrame.Replace(stderr, m =>
        {
            var n = int.Parse(m.Groups["line"].Value);
            return n > PrefixLines ? $"File \"{ScriptFileName}\", line {n - PrefixLines}" : m.Value;
        });
        return CompileLocation.Replace(shifted, m =>
        {
            var n = int.Parse(m.Groups["line"].Value);
            return n > PrefixLines ? $"{ScriptFileName}:[{n - PrefixLines}:{m.Groups["col"].Value}]" : m.Value;
        });
    }

    internal const string ScriptFileName = "<script>";
}

/// <summary>A Python exception from a run: the Python message, with the traceback (line numbers already
/// shifted) as detail. <see cref="IsSyntaxError"/> routes SyntaxError/IndentationError to the compile code.</summary>
public sealed class PythonScriptException : Exception
{
    public PythonScriptException(Exception inner, string traceback)
        : base(Describe(inner.Message, traceback), inner)
    {
        Traceback = traceback;
    }

    /// <summary>"Compile Error" alone says nothing; append CPython's own line from stderr
    /// ("invalid syntax  (Error CPYC01) &lt;script&gt;:[2:1]").</summary>
    private static string Describe(string message, string traceback)
    {
        if (!message.StartsWith("Compile Error", StringComparison.Ordinal))
        {
            return message;
        }

        foreach (var line in traceback.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0 && !t.StartsWith("Compile Error", StringComparison.Ordinal))
            {
                return "Compile Error: " + t;
            }
        }

        return message;
    }

    public string Traceback { get; }

    /// <summary>RhinoCode reports a script that does not parse as an ExecuteException with the message
    /// "Compile Error" and the CPython message on stderr (verified live); a script-raised SyntaxError
    /// (from exec of a bad string, say) would carry the usual traceback.</summary>
    public bool IsSyntaxError => Message.StartsWith("Compile Error", StringComparison.Ordinal) || HasErrorLine(Traceback, "SyntaxError:") || HasErrorLine(Traceback, "IndentationError:");

    private static bool HasErrorLine(string traceback, string prefix)
    {
        foreach (var line in traceback.Split('\n'))
        {
            if (line.TrimStart().StartsWith(prefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
