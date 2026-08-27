using System;
using System.Collections.Generic;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

/// <summary>
/// Fake behind the IUncPathResolver seam (per the revit-connector-development skill's testing
/// strategy) -- a settable drive-letter -> UNC-root mapping, default passthrough for anything
/// not explicitly mapped.
/// </summary>
public sealed class FakeUncPathResolver : IUncPathResolver
{
    private readonly Dictionary<string, string> _mappings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers driveLetter (e.g. "Z:") -&gt; uncRoot (e.g. "\\server\share") used by Resolve.</summary>
    public void Map(string driveLetter, string uncRoot) => _mappings[driveLetter] = uncRoot;

    public string Resolve(string path)
    {
        if (path.Length >= 2 && _mappings.TryGetValue(path[..2], out var uncRoot))
        {
            return uncRoot + path[2..];
        }

        return path;
    }
}
