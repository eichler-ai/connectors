using System.Runtime.InteropServices;
using Rhino.MCPBridge.Core.Connection;
using Rhino.MCPBridge.Core.Diagnostics;
using Rhino.MCPBridge.Core.Execution;
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
            var host = new BridgeHost(InstanceId, RhinoApp.Version.ToString(), BridgeVersion, mainThread, documents, runHost, launcher, new RhinoViewCapture(), new RhinoCodePythonHost(), AppDataPaths.InstancesDir(), LogConnection);
            host.Start();
            CurrentHost = host;

            RhinoDoc.EndOpenDocument += OnDocumentsChanged;
            RhinoDoc.CloseDocument += OnDocumentsChanged;
            RhinoDoc.NewDocument += OnDocumentsChanged;
            RhinoDoc.ActiveDocumentChanged += OnDocumentsChanged;
            RhinoDoc.EndSaveDocument += OnDocumentsChanged; // a first save changes the id (tmp- -> doc-, PRD §12)
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

    /// <summary>connection.log under the connector root, size-capped like Revit's (issue #11 there).</summary>
    internal static void LogConnection(string message) =>
        RollingDiagnosticLog.Append(AppDataPaths.ConnectorRoot, "connection.log", message);

    internal static void LogStartup(string message) =>
        RollingDiagnosticLog.Append(AppDataPaths.ConnectorRoot, "startup-errors.log", message);
}
