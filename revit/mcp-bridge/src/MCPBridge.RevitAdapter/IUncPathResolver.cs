namespace MCPBridge.RevitAdapter;

/// <summary>
/// Best-effort resolution of a mapped network drive letter to its UNC form (PRD §09: "a mapped
/// network drive letter is resolved to its UNC target first ... so the same file opened via
/// Z:\House.rvt and \\server\share\House.rvt still hashes identically"). Implemented behind this
/// seam so DocumentIdentity's resolution logic (MCPBridge.RevitAdapter) stays unit-testable
/// against a fake mapping (see FakeUncPathResolver in MCPBridge.Core.Tests), with the real Win32 call
/// (<see cref="Win32UncPathResolver"/>) confined to RevitAdapter like every other P/Invoke in
/// this project.
/// </summary>
public interface IUncPathResolver
{
    /// <summary>
    /// Returns the UNC-resolved form of <paramref name="path"/> if it begins with a mapped
    /// network drive letter, or <paramref name="path"/> unchanged for a local disk path, an
    /// already-UNC path, or on any resolution failure -- never throws.
    /// </summary>
    string Resolve(string path);
}
