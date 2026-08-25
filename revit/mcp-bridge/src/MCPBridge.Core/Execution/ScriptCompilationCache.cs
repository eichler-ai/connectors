using Microsoft.CodeAnalysis.Scripting;

namespace MCPBridge.Core.Execution;

/// <summary>Roslyn-specific wrapper around <see cref="LruCache{TKey,TValue}"/>, keyed by verbatim script text (PRD §06).</summary>
internal sealed class ScriptCompilationCache
{
    private readonly LruCache<string, Script<object>> _inner;

    public ScriptCompilationCache(int capacity)
    {
        _inner = new LruCache<string, Script<object>>(capacity);
    }

    public bool TryGet(string scriptText, out Script<object>? script) => _inner.TryGet(scriptText, out script);

    public void Set(string scriptText, Script<object> script) => _inner.Set(scriptText, script);
}
