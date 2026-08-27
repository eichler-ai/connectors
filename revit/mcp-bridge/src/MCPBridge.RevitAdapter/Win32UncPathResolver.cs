using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Real WNetGetConnection-based implementation of <see cref="IUncPathResolver"/> (PRD §09).
/// Mirrors <see cref="Win32WindowInventory"/>'s exact shape and posture -- try/catch-degrade to
/// returning the original path on any failure, no unit tests (this is adapter-side P/Invoke,
/// not decision logic; see that class's own doc comment for why).
/// </summary>
public sealed class Win32UncPathResolver : IUncPathResolver
{
    private const int InitialBufferLength = 512;
    private const int NoError = 0;
    private const int ErrorMoreData = 234;

    public string Resolve(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':')
        {
            // Not a "X:\..." drive-letter path (already UNC, relative, or malformed) -- nothing to
            // resolve; return unchanged, same as the "non-network drive" case below.
            return path;
        }

        var driveRoot = path[..2]; // e.g. "Z:"

        try
        {
            var length = InitialBufferLength;
            var buffer = new StringBuilder(length);
            var result = WNetGetConnectionW(driveRoot, buffer, ref length);

            if (result == ErrorMoreData)
            {
                // Buffer was too small; WNetGetConnectionW filled in the required length -- retry once.
                buffer = new StringBuilder(length);
                result = WNetGetConnectionW(driveRoot, buffer, ref length);
            }

            if (result != NoError)
            {
                // Local disk, substed path, or no mapping at all -- return unchanged rather than guess.
                return path;
            }

            var uncRoot = buffer.ToString();
            return uncRoot.Length == 0 ? path : uncRoot + path[2..];
        }
        catch
        {
            // Best-effort, same posture as Win32WindowInventory: any failure degrades to the
            // original path rather than risking the caller's document-identity computation.
            return path;
        }
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string lpLocalName, StringBuilder lpRemoteName, ref int lpnLength);
}
