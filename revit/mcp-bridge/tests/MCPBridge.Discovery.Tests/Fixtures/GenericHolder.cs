namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>
/// Fixture for generic-method member_ids. XmlDocId appends a "``N" arity suffix for a generic method
/// (<c>Read``1(System.String)</c>) while the reflected member's Name is plain <c>Read</c>, so
/// member_id-only resolution has to strip that suffix to match any candidate. The two same-named
/// members below — one generic, one not — also make member_id do real work: both share the arity-
/// stripped name, so only the exact member_id distinguishes them.
/// </summary>
public class GenericHolder
{
    /// <summary>Reads a value of the requested type.</summary>
    /// <param name="key">The lookup key.</param>
    /// <returns>The stored value.</returns>
    public T Read<T>(string key) => default!;

    /// <summary>Reads a raw value, without a type argument.</summary>
    /// <param name="key">The lookup key.</param>
    /// <returns>The stored value, unconverted.</returns>
    public string Read(string key) => key;
}
