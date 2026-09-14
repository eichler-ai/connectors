using System.Runtime.InteropServices;
using Rhino.MCPBridge.Core.Connection;
using Rhino.MCPBridge.Core.Diagnostics;
using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.Core.Discovery;
using Rhino.MCPBridge.RhinoAdapter;
using Rhino.PlugIns;

[assembly: PlugInDescription(DescriptionType.Organization, "Eichler")]
[assembly: PlugInDescription(DescriptionType.Address, "https://github.com/eichler-ai/connectors")]
[assembly: System.Runtime.InteropServices.Guid("7d2f5a41-0c1e-4b7a-9f3d-1e6c2a9b8d01")]

namespace Rhino.MCPBridge.PlugIn;

/// <summary>
/// The plug-in's entry point (PRD §04: intentionally thin). OnLoad mints the instance id, starts the
/// listener, subscribes the document events that re-send `register`; OnShutdown tears down. No
/// protocol or decision logic here.
/// </summary>
public sealed class RhinoMCPBridgePlugIn : Rhino.PlugIns.PlugIn
{
    /// <summary>Minted once per Rhino process (PRD §05); doubles as the unsaved-document identity salt (§12).</summary>
    public static Guid InstanceId { get; private set; }

    internal static BridgeHost? CurrentHost { get; private set; }
    internal static string BridgeVersion => typeof(RhinoMCPBridgePlugIn).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false) is [System.Reflection.AssemblyInformationalVersionAttribute a] ? a.InformationalVersion : "dev";

    public RhinoMCPBridgePlugIn() { Instance = this; }
    public static RhinoMCPBridgePlugIn? Instance { get; private set; }

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        try
        {
            InstanceId = Guid.NewGuid();
            var mainThread = new RhinoMainThread(Environment.CurrentManagedThreadId);
            var caseInsensitive = !RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            var documents = new RhinoDocumentSnapshotSource(InstanceId, caseInsensitive, LogConnection);
            var runHost = new RhinoRunHost(InstanceId, caseInsensitive, BridgeVersion, Core.Execution.UndoRunExecutor.RunCommandName, () => MCPBridgeRunCommand.Instance?.Id ?? Guid.Empty, LogConnection);
            var launcher = new RhinoRunLauncher(LogConnection);
            RoslynAssemblyIsolation.EnsureInitialized();
            // Discovery's SQLite deps resolve the same way under Rhino's LoadFrom-style plug-in load (PRD §09).
            SqliteAssemblyIsolation.EnsureInitialized();
            var pythonHost = new RhinoCodePythonHost();
            // §08 v1 modal-dialog diagnostic: the platform window inventory (null on any other OS turns the
            // feature off). Win32 EnumWindows on Windows, Core Graphics CGWindowList on Mac — both enumerate
            // off the main thread, which the §08 fallback requires.
            Core.Diagnostics.IWindowInventory? windowInventory = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) windowInventory = new Win32WindowInventory();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) windowInventory = new MacWindowInventory();
            var host = new BridgeHost(InstanceId, RhinoApp.Version.ToString(), BridgeVersion, mainThread, documents, runHost, launcher, new RhinoViewCapture(), pythonHost, windowInventory, AppDataPaths.InstancesDir(), LogConnection);
            host.Start();
            CurrentHost = host;

            // #287: RhinoCodePlugin is demand-loaded ONLY on Windows, so Python 3 never registers there
            // until it is loaded; force it, or the Python warm-up times out. On Mac it is AtStartup and
            // already loaded, so this is skipped rather than relying on LoadPlugIn's idempotency (and the
            // Mac path stays untouched). Deferred to the first Idle tick, on the main thread.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                ForceLoadRhinoCode(pythonHost);
            }

            RhinoDoc.EndOpenDocument += OnDocumentsChanged;
            RhinoDoc.CloseDocument += OnDocumentsChanged;
            RhinoDoc.NewDocument += OnDocumentsChanged;
            RhinoDoc.ActiveDocumentChanged += OnDocumentsChanged;
            RhinoDoc.EndSaveDocument += OnDocumentsChanged; // a first save changes the id (tmp- -> doc-, PRD §12)

            // Grasshopper is process-global and demand-loaded, so its definitions do not ride RhinoDoc
            // events; watch its DocumentServer (once it loads) so an opened/closed .gh refreshes the
            // snapshot immediately (PRD §10, review of PR #304).
            GrasshopperWatcher.Start(() => CurrentHost?.PushRegisterRefresh(), LogConnection);

            // Phase 7: if the server binary is installed beside us but the user's Claude config doesn't yet
            // point at it, leave one notice in connection.log. We never edit the config on load — the user
            // runs MCPBridgeRegister — so this is the "notice" half of the command+notice registration UX.
            ScheduleRegistrationNotice();
            return LoadReturnCode.Success;
        }
        catch (Exception ex)
        {
            // A failed load must not take Rhino down, and must leave a trace (Revit's lesson: silent no-load).
            LogStartup("OnLoad failed: " + ex);
            errorMessage = "MCP Bridge failed to start: " + ex.Message;
            return LoadReturnCode.ErrorShowDialog;
        }
    }

    protected override void OnShutdown()
    {
        RhinoDoc.EndOpenDocument -= OnDocumentsChanged;
        RhinoDoc.CloseDocument -= OnDocumentsChanged;
        RhinoDoc.NewDocument -= OnDocumentsChanged;
        RhinoDoc.ActiveDocumentChanged -= OnDocumentsChanged;
        RhinoDoc.EndSaveDocument -= OnDocumentsChanged;
        CurrentHost?.Stop();
        CurrentHost = null;
        base.OnShutdown();
    }

    private static void OnDocumentsChanged(object? sender, EventArgs e) => CurrentHost?.PushRegisterRefresh();

    /// <summary>McNeel's RhinoCodePlugin — the Python 3 / ScriptEditor host. Verified 2026-09-11 to be
    /// demand-loaded on Windows (registry LoadMode=2 / WhenNeeded) while every other plug-in, ours
    /// included, is AtStartup; on Mac it loads at startup. Its GUID is stable across installs.</summary>
    private static readonly Guid RhinoCodePluginId = new("c9cba87a-23ce-4f15-a918-97645c05cde7");

    /// <summary>Force-loads RhinoCodePlugin so the Python host can initialise without the person opening
    /// the ScriptEditor (issue #287). Deferred to the first Idle tick — loading another plug-in from
    /// inside our own OnLoad, while Rhino is still bringing plug-ins up, risks reentrancy — and run on
    /// the main thread, which LoadPlugIn requires. A load failure becomes the Python host's unavailable
    /// reason rather than a silent 180 s warm-up timeout. No-op on Mac, where it is already loaded.</summary>
    private static void ForceLoadRhinoCode(RhinoCodePythonHost pythonHost)
    {
        EventHandler? onIdle = null;
        onIdle = (_, _) =>
        {
            RhinoApp.Idle -= onIdle;
            try
            {
                var loaded = Rhino.PlugIns.PlugIn.LoadPlugIn(RhinoCodePluginId);
                LogConnection($"force-load RhinoCodePlugin ({RhinoCodePluginId}): {loaded}");
                if (!loaded)
                {
                    pythonHost.NoteRhinoCodeLoadFailed($"could not force-load RhinoCodePlugin {RhinoCodePluginId} (issue #287)");
                }
            }
            catch (Exception ex)
            {
                LogConnection("force-load RhinoCodePlugin threw: " + ex.Message);
                pythonHost.NoteRhinoCodeLoadFailed($"force-loading RhinoCodePlugin threw: {ex.Message} (issue #287)");
            }
        };
        RhinoApp.Idle += onIdle;
    }

    /// <summary>One-shot, on the first Idle tick: if the server binary is installed beside the plug-in
    /// (a yak install, not a dev build) but the user's Claude config does not register it, note it in
    /// connection.log. Stays silent for a dev build with no binary beside us, so it does not nag the
    /// harness loop (which registers by RHINO_MCP_SERVER_PATH out of band). PRD §15.</summary>
    private static void ScheduleRegistrationNotice()
    {
        EventHandler? onIdle = null;
        onIdle = (_, _) =>
        {
            RhinoApp.Idle -= onIdle;
            try
            {
                // Only the packaged case: a binary sitting beside the plug-in. A dev build returns null here
                // (no binary; the env override is a path elsewhere) and stays quiet, so this never nags the
                // harness loop. `register --check` exits non-zero when no Claude client points at us.
                var server = ServerBinaryLocator.LocatePackagedOnly();
                if (server is null) return;

                if (ServerRegistration.RunWith(server, "register", "--check").exitCode != 0)
                {
                    LogConnection("MCP server is installed but not registered with a Claude client — run MCPBridgeRegister in Rhino to connect Claude Code and/or Claude Desktop.");
                }
            }
            catch (Exception ex)
            {
                LogConnection("registration notice check failed: " + ex.Message);
            }
        };
        RhinoApp.Idle += onIdle;
    }

    /// <summary>connection.log under the connector root, size-capped like Revit's (issue #11 there).</summary>
    internal static void LogConnection(string message) =>
        RollingDiagnosticLog.Append(AppDataPaths.ConnectorRoot, "connection.log", message);

    internal static void LogStartup(string message) =>
        RollingDiagnosticLog.Append(AppDataPaths.ConnectorRoot, "startup-errors.log", message);
}
