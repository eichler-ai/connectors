namespace MCPBridge.Core.Execution;

/// <summary>Roslyn-specific wrapper around <see cref="LruCache{TKey,TValue}"/>, keyed by verbatim script text (PRD §06).</summary>
internal sealed class ScriptCompilationCache
{
    private readonly LruCache<string, CompiledScript> _inner;

    public ScriptCompilationCache(int capacity)
    {
        _inner = new LruCache<string, CompiledScript>(capacity);
    }

    public bool TryGet(string scriptText, out CompiledScript? compiled) => _inner.TryGet(scriptText, out compiled);

    public void Set(string scriptText, CompiledScript compiled) => _inner.Set(scriptText, compiled);
}
