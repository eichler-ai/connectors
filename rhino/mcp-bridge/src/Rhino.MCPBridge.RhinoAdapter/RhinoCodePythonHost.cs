using System.IO;
using System.Reflection;
using System.Text;
using Rhino.MCPBridge.Core.Execution.Python;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// <see cref="IPythonHost"/> over Rhino 8's <c>Rhino.Runtime.Code</c> (spikes §1). The assembly ships
/// inside Rhino and not on NuGet, so it is bound at run time by name rather than referenced at compile
/// time -- the plug-in builds on both CI runners with the RhinoCommon package alone, and a Rhino whose
/// <c>Rhino.Runtime.Code</c> surface has moved reports itself through <see cref="UnavailableReason"/>
/// instead of failing to load. The surface used is exactly the spike's: <c>RhinoCode.Languages.
/// WaitStatusComplete(LanguageSpec.Python3)</c>, <c>new RunContext { AutoApplyParams = true }</c> with
/// <c>Inputs.Set</c>/<c>Outputs.Set</c>/<c>Outputs.Get&lt;object&gt;</c>, <c>OutputStream</c>/
/// <c>ErrorStream</c>, and <c>RhinoCode.RunScript(text, context)</c>.
/// </summary>
internal sealed class RhinoCodePythonHost : IPythonHost
{
    private const string AssemblyName = "Rhino.Runtime.Code";
    /// <summary>Generous: the first run on a machine deploys the CPython runtime (~35 s, spikes §1).</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(3);

    private volatile string? _unavailable = "the Python 3 language has not finished loading";
    private Type? _rhinoCode;
    private Type? _runContext;
    private MethodInfo? _runScript;

    public string? UnavailableReason => _unavailable;

    public void EnsureLoaded()
    {
        try
        {
            var asm = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == AssemblyName) ?? Assembly.Load(AssemblyName);
            _rhinoCode = asm.GetType("Rhino.Runtime.Code.RhinoCode", throwOnError: true)!;
            _runContext = asm.GetType("Rhino.Runtime.Code.Execution.RunContext", throwOnError: true)!;
            var languageSpec = asm.GetType("Rhino.Runtime.Code.Languages.LanguageSpec", throwOnError: true)!;
            var python3 = languageSpec.GetProperty("Python3", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? throw new MissingMemberException("LanguageSpec.Python3");
            dynamic languages = _rhinoCode.GetProperty("Languages", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? throw new MissingMemberException("RhinoCode.Languages");
            // RunScript(string, IRunContext) -- found by shape so the parameter's declared type (the
            // interface, or the class) does not matter.
            _runScript = Array.Find(_rhinoCode.GetMethods(BindingFlags.Public | BindingFlags.Static), m =>
                m.Name == "RunScript" && m.GetParameters() is { Length: 2 } ps && ps[0].ParameterType == typeof(string) && ps[1].ParameterType.IsAssignableFrom(_runContext))
                ?? throw new MissingMethodException("RhinoCode.RunScript(string, RunContext)");
            // At plug-in load the registry may not have queued Python yet, so WaitStatusComplete
            // returns at once with nothing loaded (verified live: 63 ms, then "Can not determine
            // language" on the first run). Poll until the language is registered, then wait for ready.
            var deadline = DateTime.UtcNow + LoadTimeout;
            object? language = null;
            while (language is null)
            {
                languages.WaitStatusComplete((dynamic)python3);
                language = languages.QueryLatest((dynamic)python3);
                if (language is null)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new TimeoutException($"Python 3 was not registered by RhinoCode within {LoadTimeout.TotalSeconds:0} s");
                    }

                    Thread.Sleep(500);
                }
            }

            // The language object is an internal type behind public interfaces, which the dynamic
            // binder refuses ('object' does not contain a definition for 'Status', verified live);
            // reach ILanguage.Status / ILanguageStatus.WaitReady through the interfaces instead.
            var status = GetViaInterfaces(language, "Status");
            InvokeViaInterfaces(status, "WaitReady");
            _unavailable = null;
        }
        catch (Exception ex)
        {
            _unavailable = $"Rhino.Runtime.Code could not be initialised ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static object GetViaInterfaces(object target, string property)
    {
        foreach (var type in TypeAndInterfaces(target.GetType()))
        {
            var p = type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
            if (p is not null) return p.GetValue(target) ?? throw new MissingMemberException(type.Name + "." + property + " is null");
        }

        throw new MissingMemberException(target.GetType().Name + "." + property);
    }

    private static void InvokeViaInterfaces(object target, string method)
    {
        foreach (var type in TypeAndInterfaces(target.GetType()))
        {
            var m = type.GetMethod(method, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (m is not null) { m.Invoke(target, null); return; }
        }

        throw new MissingMethodException(target.GetType().Name + "." + method);
    }

    private static IEnumerable<Type> TypeAndInterfaces(Type t)
    {
        yield return t;
        foreach (var i in t.GetInterfaces()) yield return i;
    }

    public PythonRunResult Run(string text, IReadOnlyDictionary<string, object?> inputs, string resultName)
    {
        if (_unavailable is not null || _rhinoCode is null || _runContext is null)
        {
            return new PythonRunResult { Error = new InvalidOperationException(_unavailable ?? "python host not loaded") };
        }

        // RunContext(bool defaultOutputStream = false, bool defaultErrorStream = false): the optional
        // parameters are compile-time sugar, so CreateInstance needs them spelled out.
        dynamic ctx = Activator.CreateInstance(_runContext, new object[] { false, false })!;
        ctx.AutoApplyParams = true;
        foreach (var kv in inputs)
        {
            ctx.Inputs.Set(kv.Key, kv.Value);
        }

        ctx.Outputs.Set(resultName, (object?)null);
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        ctx.OutputStream = stdout;
        ctx.ErrorStream = stderr;

        Exception? error = null;
        try
        {
            _runScript!.Invoke(null, new object[] { text, (object)ctx });
        }
        catch (TargetInvocationException tie)
        {
            error = tie.InnerException ?? tie;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        object? result = null;
        if (error is null)
        {
            try { result = ctx.Outputs.Get<object>(resultName); }
            catch { result = null; } // the script never assigned it
        }

        return new PythonRunResult
        {
            Result = result,
            StdOut = Encoding.UTF8.GetString(stdout.ToArray()),
            StdErr = Encoding.UTF8.GetString(stderr.ToArray()),
            Error = error,
        };
    }
}
