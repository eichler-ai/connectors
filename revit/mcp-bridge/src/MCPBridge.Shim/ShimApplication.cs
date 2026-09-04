using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.UI;

namespace MCPBridge.Shim;

/// <summary>
/// The stable, manifest-named add-in (revit/docs/self-update-architecture.md §4.2, issue #211).
///
/// Revit loads this assembly from Addins\&lt;year&gt;\. It reads the installer's pointer file
/// (<c>&lt;app dir&gt;\addin\current.json</c>, <c>{"version":"v0.1.5"}</c>) ONCE, <see cref="Assembly.LoadFrom"/>s
/// the real add-in out of <c>addin\&lt;version&gt;\&lt;year&gt;\MCPBridge.AddIn.dll</c>, and forwards
/// <see cref="OnStartup"/> / <see cref="OnShutdown"/> to <c>MCPBridge.AddIn.MCPBridgeApplication</c> by
/// reflection. An add-in update is then "write a new version folder, flip the pointer": a running Revit
/// keeps the files it mapped and the new version loads at its next start, with nothing asked to close.
///
/// Load-context correctness (§4.3, the part that must not regress):
/// <list type="bullet">
/// <item><c>Assembly.LoadFrom</c> from a real path -- never <c>Assembly.Load(byte[])</c>. The real add-in's
/// <c>AssemblyResolution</c>, its ribbon <c>PushButtonData</c> and <c>XmlDocIndex</c> all need a non-empty
/// <c>Assembly.Location</c> (Appendix A.3). After this LoadFrom, <c>typeof(MCPBridgeApplication).Assembly.Location</c>
/// is the versioned path, which is exactly what those consumers want.</item>
/// <item>Exactly ONE version of the real add-in per process: the pointer is read once, in OnStartup, and
/// <see cref="s_real"/> makes a second load structurally impossible. Two versions in one AppDomain is the
/// one thing that breaks Roslyn's reference enumeration and <c>Eichler.Connectors.Revit</c> discovery.</item>
/// <item>The <c>AssemblyResolve</c> handler below makes the versioned folder the probe root for the real
/// add-in's immediate references before its own deps.json-scoped <c>AssemblyResolution.Register()</c> runs
/// (the first thing its OnStartup does). It serves a name only when a same-named DLL sits in that folder,
/// and is removed again on shutdown.</item>
/// </list>
///
/// Validated live (2026-09-04, Revit 2027): a shim built exactly like this loaded the real v0.1.5 add-in from
/// <c>addin\v0.1.5\2027\</c> -- ribbon, broker connect, Roslyn warm-up and discovery all ran unchanged, with
/// no trust prompt because the shim is signed with the same certificate as the add-in (§4.6).
///
/// Contract with the installer (revit/install.ps1): the installer writes the versioned folders first and
/// <c>current.json</c> last, atomically; both sides tolerate a UTF-8 BOM on the pointer (Windows PowerShell's
/// <c>Out-File -Encoding utf8</c> writes one). Every failure here logs to
/// <c>%LOCALAPPDATA%\Connectors\Revit\startup-errors.log</c> (the same file the real add-in uses) and returns
/// <see cref="Result.Failed"/>; nothing ever throws out of OnStartup (same contract as the real add-in's).
/// </summary>
public sealed class ShimApplication : IExternalApplication
{
    private const string RealAssemblyFileName = "MCPBridge.AddIn.dll";
    private const string RealApplicationTypeName = "MCPBridge.AddIn.MCPBridgeApplication";

    // Process-wide, deliberately: Revit constructs one IExternalApplication per manifest, but the
    // "exactly one real add-in per process" invariant (§4.3) must hold even if that ever changed.
    private static object? s_real;
    private static MethodInfo? s_shutdown;
    private static string? s_versionDir;
    private static ResolveEventHandler? s_resolveHandler;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            if (s_real is not null)
            {
                Log("shim: OnStartup called again in this process -- the real add-in is already loaded from " + s_versionDir + "; refusing to load a second copy (§4.3).");
                return Result.Failed;
            }

            var addinRoot = ResolveAddinRoot();
            var version = ReadCurrentVersion(Path.Combine(addinRoot, "current.json"));
            var year = application.ControlledApplication.VersionNumber;

            var dir = ResolveVersionDir(addinRoot, version, year);
            if (dir is null)
            {
                Log($"shim: current.json names {version} but no folder under {addinRoot} has {year}\\{RealAssemblyFileName}; nothing to load.");
                return Result.Failed;
            }
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(dir)), version, StringComparison.Ordinal))
            {
                Log($"shim: current.json names {version}, which has no Revit {year} payload -- falling back to {dir} (§4.2).");
            }

            s_versionDir = dir;
            s_resolveHandler = ResolveFromVersionDir;
            AppDomain.CurrentDomain.AssemblyResolve += s_resolveHandler;

            var asm = Assembly.LoadFrom(Path.Combine(dir, RealAssemblyFileName));
            var type = asm.GetType(RealApplicationTypeName)
                ?? throw new InvalidOperationException($"{RealApplicationTypeName} not found in {asm.Location}");
            var startup = type.GetMethod("OnStartup", new[] { typeof(UIControlledApplication) })
                ?? throw new InvalidOperationException($"{RealApplicationTypeName}.OnStartup(UIControlledApplication) not found");
            s_shutdown = type.GetMethod("OnShutdown", new[] { typeof(UIControlledApplication) });
            s_real = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"could not instantiate {RealApplicationTypeName}");

            Log($"shim: loaded real add-in from {asm.Location}");
            return (Result)startup.Invoke(s_real, new object[] { application })!;
        }
        catch (Exception ex)
        {
            Log("shim: OnStartup failed: " + ex);
            // Revit does not call OnShutdown for an application whose OnStartup failed, so a handler
            // registered above must be detached here or it stays live process-wide for the session (and a
            // second OnStartup attempt would add another). Only on the failure path: a successful start
            // keeps it until OnShutdown, and s_real is the record of that.
            if (s_real is null)
            {
                DetachResolveHandler();
                s_versionDir = null;
            }
            return Result.Failed;
        }
    }

    private static void DetachResolveHandler()
    {
        if (s_resolveHandler is not null)
        {
            AppDomain.CurrentDomain.AssemblyResolve -= s_resolveHandler;
            s_resolveHandler = null;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            var result = s_real is not null && s_shutdown is not null
                ? (Result)s_shutdown.Invoke(s_real, new object[] { application })!
                : Result.Succeeded;
            DetachResolveHandler();
            return result;
        }
        catch (Exception ex)
        {
            Log("shim: OnShutdown failed: " + ex);
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// <c>&lt;app dir&gt;\addin</c>, where the app dir is what install.ps1's Get-AppDir resolves for the scope
    /// this shim was installed at -- derived from where THIS DLL was loaded from, exactly as the real
    /// add-in's UpdateTrigger.ResolveInstallLocation does: the all-users Addins folder means the all-users
    /// app dir; anything else is the per-user install.
    /// </summary>
    internal static string ResolveAddinRoot()
    {
        var self = typeof(ShimApplication).Assembly.Location;
        var allUsers = !string.IsNullOrEmpty(self)
            && self.Contains(@"\Program Files\Autodesk\Revit\Addins\", StringComparison.OrdinalIgnoreCase);
        var appDir = allUsers
            ? @"C:\Program Files\MCPBridge"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MCPBridge");
        return Path.Combine(appDir, "addin");
    }

    /// <summary>Reads <c>version</c> from current.json. File.ReadAllText strips a UTF-8 BOM itself.</summary>
    internal static string ReadCurrentVersion(string pointerPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(pointerPath));
        if (!doc.RootElement.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
        {
            throw new InvalidOperationException($"{pointerPath} has no \"version\" string");
        }
        var version = v.GetString()!.Trim();
        if (version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{pointerPath}: \"{version}\" is not a plausible version folder name");
        }
        return version;
    }

    /// <summary>
    /// <c>addin\&lt;version&gt;\&lt;year&gt;</c> when it holds the real add-in; otherwise the newest version folder
    /// (by numeric version order, so v0.1.10 sorts after v0.1.9) that does -- a release may ship for only one
    /// Revit year (§4.2 fallback). Null when none does.
    /// </summary>
    internal static string? ResolveVersionDir(string addinRoot, string version, string year)
    {
        var pointed = Path.Combine(addinRoot, version, year);
        if (File.Exists(Path.Combine(pointed, RealAssemblyFileName)))
        {
            return pointed;
        }
        if (!Directory.Exists(addinRoot))
        {
            return null;
        }
        return Directory.GetDirectories(addinRoot)
            // *.stale-* is a folder the installer is mid-way through deleting (Remove-StaleAddinVersions).
            .Where(d => !Path.GetFileName(d).Contains(".stale-", StringComparison.Ordinal))
            .OrderByDescending(Path.GetFileName, VersionFolderComparer.Instance)
            .Select(d => Path.Combine(d, year))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, RealAssemblyFileName)));
    }

    private static Assembly? ResolveFromVersionDir(object? sender, ResolveEventArgs args)
    {
        try
        {
            var dir = s_versionDir;
            if (dir is null)
            {
                return null;
            }
            var name = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            var candidate = Path.Combine(dir, name + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        }
        catch
        {
            return null; // a resolver must never throw into the loader; let the CLR report the miss.
        }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Connectors", "Revit");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "startup-errors.log"), $"{DateTime.UtcNow:o} {message}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort: a logging failure must never mask the real one.
        }
    }

    /// <summary>
    /// Orders version folder names such as <c>v0.1.10</c> numerically by dotted component; anything that does
    /// not parse (e.g. install.ps1's <c>local-20260904120000</c> dev tags) sorts below every real version and
    /// ordinally among itself.
    /// </summary>
    internal sealed class VersionFolderComparer : IComparer<string?>
    {
        public static readonly VersionFolderComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var px = Parse(x);
            var py = Parse(y);
            if (px is null && py is null) return string.CompareOrdinal(x, y);
            if (px is null) return -1;
            if (py is null) return 1;
            for (var i = 0; i < Math.Max(px.Length, py.Length); i++)
            {
                var a = i < px.Length ? px[i] : 0;
                var b = i < py.Length ? py[i] : 0;
                if (a != b) return a.CompareTo(b);
            }
            return string.CompareOrdinal(x, y);
        }

        private static int[]? Parse(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var s = name[0] is 'v' or 'V' ? name.Substring(1) : name;
            var parts = s.Split('.');
            var result = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out result[i])) return null;
            }
            return result;
        }
    }
}
