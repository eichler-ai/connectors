using System;
using System.Collections.Generic;

namespace Rhino.MCPBridge.Core.Execution.Python;

/// <summary>
/// The CPython host behind <see cref="PythonScriptRunner"/>: Rhino 8's <c>Rhino.Runtime.Code</c> in
/// production (RhinoAdapter), a fake in tier 1. The contract is the four things the spikes verified
/// (phase-1a-findings §1): the language loads lazily and must be waited for once; a run takes named
/// inputs, declares named outputs, and captures stdout/stderr; a Python exception comes back as a
/// .NET exception whose message is the Python message, with the traceback on stderr.
/// </summary>
internal interface IPythonHost
{
    /// <summary>Null once <see cref="EnsureLoaded"/> has succeeded; before that, or after it failed, why
    /// scripts cannot run.</summary>
    string? UnavailableReason { get; }

    /// <summary>Loads the Python 3 language, blocking until it is ready (~2 s warm, ~35 s the first
    /// time on a machine). Called once at plug-in load, off the main thread. Never throws: a failure
    /// becomes <see cref="UnavailableReason"/>.</summary>
    void EnsureLoaded();

    /// <summary>Runs the text (shebang included) with the given globals bound, on the calling thread.
    /// <paramref name="resultName"/> names the global read back after the run.</summary>
    PythonRunResult Run(string text, IReadOnlyDictionary<string, object?> inputs, string resultName);
}

internal sealed class PythonRunResult
{
    public object? Result { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    /// <summary>The exception the host raised, or null when the script completed.</summary>
    public Exception? Error { get; init; }
}
