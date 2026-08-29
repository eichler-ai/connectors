using System;
using Microsoft.CodeAnalysis.Scripting;

namespace MCPBridge.Core.Execution;

/// <summary>
/// One compiled script plus everything derived from its text that a later run would otherwise have to
/// recompute -- its <see cref="ScriptApiAnalysis"/> (PRD §14) and, once first emitted, its PE image.
/// All of it is cached together, keyed by verbatim script text, because all of it depends on exactly
/// that and nothing else; the per-request confirmation flag deliberately lives outside this type (see
/// ScriptApiAnalysis).
/// </summary>
internal sealed class CompiledScript
{
    private readonly object _emitLock = new();
    private byte[]? _peImage;

    public CompiledScript(Script<object> script, ScriptApiAnalysis analysis)
    {
        Script = script;
        Analysis = analysis;
    }

    public Script<object> Script { get; }

    public ScriptApiAnalysis Analysis { get; }

    /// <summary>
    /// The emitted PE image, produced at most once per compiled script (issue #52): Emit is
    /// deterministic for an immutable Compilation, yet it used to run on EVERY execution -- even
    /// LRU hits, the exact verbatim-re-run case the cache exists for -- because each run loads a
    /// fresh collectible ALC (PRD §06). Caching the bytes turns a re-run into just
    /// ALC-load + invoke. A failed emit is NOT cached: the exception propagates each time (the
    /// pre-caching behavior), so a transient emit failure can't poison the entry. Memory cost is
    /// bounded by the LRU's own capacity, and a small script's image is a few KB.
    /// </summary>
    public byte[] GetOrEmitPeImage(Func<Script<object>, byte[]> emit)
    {
        if (_peImage is { } cached)
        {
            return cached;
        }

        lock (_emitLock)
        {
            return _peImage ??= emit(Script);
        }
    }
}
