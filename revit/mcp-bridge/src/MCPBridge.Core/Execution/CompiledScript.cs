using System;
using Microsoft.CodeAnalysis.Scripting;

namespace MCPBridge.Core.Execution;

/// <summary>
/// One compiled script plus everything derived from its text that a later run would otherwise have to
/// recompute -- its <see cref="ScriptApiAnalysis"/> (PRD §14) and, once first emitted, its PE image.
/// Both are cached together, keyed by verbatim script text, because both depend on exactly that and
/// nothing else; the per-request confirmation flag deliberately lives outside this type (see
/// ScriptApiAnalysis).
///
/// The Roslyn <see cref="Script{T}"/> itself is held ONLY until the first emit and then released
/// (issue #31): it roots the whole Compilation -- symbol tables for the referenced RevitAPI assemblies
/// included, tens of MB per entry -- and after emit the run path needs only the (small) PE image and the
/// analysis. A gcdump of a long-lived session found ~10M managed objects / ~0.6GB dominated by
/// Microsoft.CodeAnalysis, held by exactly these compilations across the bounded LRU; releasing the
/// Script here is what that finding pointed to.
/// </summary>
internal sealed class CompiledScript
{
    private readonly object _emitLock = new();
    private byte[]? _peImage;

    // Released (set null) after the first successful emit -- see GetOrEmitPeImage. Nullable, not
    // readonly, precisely so the heavy Compilation it roots becomes collectible once it is no longer
    // needed.
    private Script<object>? _script;

    public CompiledScript(Script<object> script, ScriptApiAnalysis analysis)
    {
        _script = script;
        Analysis = analysis;
    }

    public ScriptApiAnalysis Analysis { get; }

    /// <summary>
    /// True once the Roslyn <see cref="Script{T}"/> (and the Compilation it roots) has been released --
    /// i.e. after the first successful emit. Test seam for issue #31's cache-retention fix; the memory
    /// win is not otherwise observable deterministically at the unit seam.
    /// </summary>
    internal bool CompilationReleased => _script is null;

    /// <summary>
    /// The emitted PE image, produced at most once per compiled script (issue #52): Emit is
    /// deterministic for an immutable Compilation, yet it used to run on EVERY execution -- even
    /// LRU hits, the exact verbatim-re-run case the cache exists for -- because each run loads a
    /// fresh collectible ALC (PRD §06). Caching the bytes turns a re-run into just ALC-load + invoke.
    ///
    /// The <see cref="Script{T}"/> is released immediately after a successful emit (issue #31): the
    /// image plus <see cref="Analysis"/> are all any later run needs, and the Script otherwise keeps the
    /// entire Compilation rooted for the entry's life. A failed emit is NOT cached and does NOT release
    /// the Script: the exception propagates each time (the pre-caching behavior), so a transient emit
    /// failure can be retried against the same Script and cannot poison the entry.
    /// </summary>
    public byte[] GetOrEmitPeImage(Func<Script<object>, byte[]> emit)
    {
        if (_peImage is { } cached)
        {
            return cached;
        }

        lock (_emitLock)
        {
            if (_peImage is { } already)
            {
                return already;
            }

            // emit() runs FIRST and may throw; only on success are the bytes cached and the Script
            // released, so a throw leaves both untouched for a clean retry.
            var image = emit(_script!);
            _peImage = image;
            _script = null;
            return image;
        }
    }
}
