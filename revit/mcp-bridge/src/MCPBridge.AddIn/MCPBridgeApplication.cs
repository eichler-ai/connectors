using System;
using System.IO;
using Autodesk.Revit.UI;
using MCPBridge.Core.Connection;
using MCPBridge.Core.Execution;

namespace MCPBridge.AddIn;

/// <summary>
/// The add-in's entry point (PRD §04: "the add-in stays intentionally thin"). Wires
/// OnStartup/OnShutdown and delegates everything else to MCPBridge.Core -- no
/// protocol, threading, or execution decision logic lives here.
/// </summary>
public sealed class MCPBridgeApplication : IExternalApplication
{
    /// <summary>Minted once per Revit process at OnStartup; stable for the process's lifetime (PRD §05).</summary>
    public static Guid InstanceId { get; private set; }

    /// <summary>
    /// The live BridgeHost for this process, if OnStartup has run -- read by MCPBridgeStatusCommand (the
    /// ribbon button) to show connection status without needing its own separate channel to the
    /// connection thread. Revit's own add-in model guarantees at most one MCPBridgeApplication instance
    /// per process, so a static reference here is safe and matches the existing InstanceId pattern above.
    /// </summary>
    internal static BridgeHost? CurrentHost { get; private set; }

    private BridgeHost? _host;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            // Second independent PR review finding: this used to sit outside the try block below, so a
            // throwing Register() (e.g. a future change to it that isn't as defensive as its current
            // implementation) would propagate out of OnStartup completely unlogged -- the exact silent-
            // failure symptom TryLogStartupFailure exists to close off, just for a different call.
            AssemblyResolution.Register();

            InstanceId = Guid.NewGuid();

            var ringBuffer = ExecutionRingBuffer.CreateDefault();
            var executionManager = ExecutionManager.CreateDefault(ringBuffer);

            // e.g. "2027" -- available directly off ControlledApplication, no live UIApplication needed
            // (unlike the open-documents list, which register also needs -- see DocumentSnapshotHandler).
            var revitVersion = application.ControlledApplication.VersionNumber;
            TryLogDiagnostic($"raw env MCPBRIDGE_BROKER_MODE={Environment.GetEnvironmentVariable("MCPBRIDGE_BROKER_MODE") ?? "(null)"} MCPBRIDGE_SHARED_ROOT={Environment.GetEnvironmentVariable("MCPBRIDGE_SHARED_ROOT") ?? "(null)"}");
            var discoveryOptions = BuildDiscoveryOptions();
            TryLogDiagnostic($"resolved discoveryOptions Mode={discoveryOptions.Mode} ConnectorRoot={discoveryOptions.ConnectorRoot}");

            _host = new BridgeHost(InstanceId, executionManager, ReconnectBackoffPolicy.Default, revitVersion, discoveryOptions);
            CurrentHost = _host;
            _host.Start();

            CreateStatusRibbonButton(application);
            DialogSuppressionHandler.Register(application, TryLogDiagnostic);
            SubscribeDocumentEvents(application);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            // A failed OnStartup must not take down all of Revit -- report failure to the AddInLoader
            // instead of letting the exception propagate (PRD §04). Independent PR review finding: this
            // used to swallow the exception with zero trace anywhere, reproducing the exact silent-no-load
            // symptom the "verifying you're actually debugging the binary you just built" skill-file
            // section (added this same session) exists to make fast to rule out -- a genuinely-thrown
            // OnStartup exception and a manifest Revit never loaded at all were otherwise indistinguishable
            // from the outside. Best-effort only (never let a logging failure mask the real one); path is
            // computed per-machine, not hardcoded to any one developer's username.
            TryLogDiagnostic($"OnStartup failed: {ex}");
            return Result.Failed;
        }
    }

    /// <summary>Best-effort append to %LOCALAPPDATA%\Connectors\Revit\startup-errors.log -- shared by
    /// OnStartup's own failure path and CreateStatusRibbonButton's (PRD §01 observability: a
    /// caught-and-swallowed failure still deserves a trace somewhere, not total silence, even when it
    /// must not fail the whole add-in load). Deliberately always the LOCAL per-machine directory
    /// (CONVENTIONS.md's app-data layout), not the resolved discoveryOptions' ConnectorRoot -- a human
    /// debugging on this machine needs to find this file here regardless of local/remote topology, and
    /// in remote mode ConnectorRoot points at a shared network drive, which would be actively worse for
    /// that. Reuses BrokerDiscoveryOptions.Local()'s own path computation rather than hand-rolling
    /// "Connectors"/"Revit" a second time, so the two can't drift apart (a docs-sync audit found this
    /// directory literally hardcoded as "MCPBridge" here, diverging from the documented convention --
    /// this is that fix).</summary>
    private static void TryLogDiagnostic(string message)
    {
        try
        {
            var directory = BrokerDiscoveryOptions.Local().ConnectorRoot;
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup-errors.log"), $"{DateTimeOffset.UtcNow:O} {message}\n");
        }
        catch
        {
            // Best-effort diagnostic only -- a failure here must never mask or replace the original
            // exception, which is already being reported to the AddInLoader via Result.Failed.
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        UnsubscribeDocumentEvents(application);
        DialogSuppressionHandler.Unregister(application);
        _host?.Stop();
        _host = null;
        CurrentHost = null;
        return Result.Succeeded;
    }

    /// <summary>
    /// The live document-snapshot push that closes issue #30's one-shot-register race: PRD §05's
    /// register document list was a snapshot taken only at connect time, so a document opened, closed,
    /// or activated afterwards was invisible to list_instances until something forced a reconnect (the
    /// entire reason redeploy-and-verify.sh grew its forced-broker-restart scaffolding, now removed).
    /// Every one of these events fires ON Revit's UI thread, so the handler can build the snapshot
    /// directly -- the exact same construction as the connect-time one (DocumentSnapshotHandler
    /// .BuildSnapshotFor), no ExternalEvent detour -- and hand it to BridgeHost.PushRegisterRefresh,
    /// whose write is serialized with every other socket write. Bursts (opening a document fires
    /// Created/Opened plus a ViewActivated) just push a few registers in a row; the broker's Register
    /// replaces the entry each time, so coalescing would buy nothing but a timer to own.
    /// </summary>
    private void SubscribeDocumentEvents(UIControlledApplication application)
    {
        application.ControlledApplication.DocumentOpened += OnDocumentOpened;
        application.ControlledApplication.DocumentCreated += OnDocumentCreated;
        application.ControlledApplication.DocumentClosed += OnDocumentClosed;
        application.ViewActivated += OnViewActivated;
    }

    private void UnsubscribeDocumentEvents(UIControlledApplication application)
    {
        application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
        application.ControlledApplication.DocumentCreated -= OnDocumentCreated;
        application.ControlledApplication.DocumentClosed -= OnDocumentClosed;
        application.ViewActivated -= OnViewActivated;
    }

    private void OnDocumentOpened(object? sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e) =>
        PushSnapshotFromApplicationEvent(sender);

    private void OnDocumentCreated(object? sender, Autodesk.Revit.DB.Events.DocumentCreatedEventArgs e) =>
        PushSnapshotFromApplicationEvent(sender);

    private void OnDocumentClosed(object? sender, Autodesk.Revit.DB.Events.DocumentClosedEventArgs e) =>
        PushSnapshotFromApplicationEvent(sender);

    private void OnViewActivated(object? sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
    {
        // ViewActivated's sender is the live UIApplication itself -- the active document may have
        // changed, which flips the snapshot's per-document active flag.
        if (sender is UIApplication uiApplication)
        {
            PushSnapshot(uiApplication);
        }
    }

    private void PushSnapshotFromApplicationEvent(object? sender)
    {
        // Application-level events' sender is the ApplicationServices.Application; a UIApplication is
        // constructible from it (the documented way to reach UI state from an application event).
        if (sender is Autodesk.Revit.ApplicationServices.Application app)
        {
            PushSnapshot(new UIApplication(app));
        }
    }

    private void PushSnapshot(UIApplication uiApplication)
    {
        try
        {
            var host = _host;
            if (host is null || !host.IsConnected)
            {
                return; // no live connection -- the next connect's own register carries the state
            }

            host.PushRegisterRefresh(DocumentSnapshotHandler.BuildSnapshotFor(uiApplication));
        }
        catch (Exception ex)
        {
            // A refresh push must never turn a document open/close into a UI-thread exception --
            // logged, per PRD §01, never rethrown into Revit's event dispatch.
            TryLogDiagnostic($"document snapshot push failed: {ex}");
        }
    }

    /// <summary>
    /// Adds a single "Status" button to a new "MCP Bridge" ribbon panel on the Add-Ins tab, per the
    /// user's own request: a quick, no-context-needed way to check "is this actually connected" and "what
    /// build/commit is this" without going through logs or an external tool -- exactly the two questions
    /// that took the most manual digging to answer during this add-in's own live-wiring development.
    /// Best-effort: a ribbon-creation failure (e.g. a panel name collision with another add-in) must not
    /// fail the whole add-in load over a UI nicety, so it's caught and swallowed here specifically, not
    /// folded into OnStartup's own broader catch.
    /// </summary>
    private static void CreateStatusRibbonButton(UIControlledApplication application)
    {
        try
        {
            var panel = application.CreateRibbonPanel("MCP Bridge");
            var assemblyLocation = typeof(MCPBridgeApplication).Assembly.Location;
            var buttonData = new PushButtonData(
                "MCPBridgeStatus",
                "Status",
                assemblyLocation,
                typeof(MCPBridgeStatusCommand).FullName)
            {
                ToolTip = "Show MCP Bridge connection status and build info.",
            };
            panel.AddItem(buttonData);
        }
        catch (Exception ex)
        {
            // Best-effort UI nicety -- see this method's own doc comment. Still logged (independent PR
            // review finding: a bare catch{} here silently violated PRD §01's observability principle --
            // "caught and swallowed" should mean "doesn't fail the load," not "leaves zero trace").
            TryLogDiagnostic($"CreateStatusRibbonButton failed: {ex}");
        }
    }

    /// <summary>
    /// Local mode (PRD §05: "the real target deployment") is the default. Remote mode -- needed for this
    /// project's own Mac+Parallels dev setup, where the broker and Revit are on different machines -- is
    /// opt-in via environment variables, since there's no other configuration mechanism in this add-in
    /// yet: MCPBRIDGE_BROKER_MODE=remote plus MCPBRIDGE_SHARED_ROOT (a UNC path, e.g.
    /// \\psf\connectors). Falls back to local mode on any misconfiguration (missing shared root, not a
    /// UNC path) rather than throwing out of OnStartup and failing the whole add-in load over a topology
    /// setting. (MCPBRIDGE_FALLBACK_HOST/MCPBRIDGE_FALLBACK_PORT were once read here too and are
    /// deliberately gone -- see BrokerDiscoveryOptions for why the fallback address could never work.)
    /// </summary>
    private static BrokerDiscoveryOptions BuildDiscoveryOptions()
    {
        var mode = Environment.GetEnvironmentVariable("MCPBRIDGE_BROKER_MODE");
        if (!string.Equals(mode, "remote", StringComparison.OrdinalIgnoreCase))
        {
            return BrokerDiscoveryOptions.Local();
        }

        var sharedRoot = Environment.GetEnvironmentVariable("MCPBRIDGE_SHARED_ROOT");
        if (string.IsNullOrWhiteSpace(sharedRoot))
        {
            return BrokerDiscoveryOptions.Local();
        }

        try
        {
            return BrokerDiscoveryOptions.Remote(sharedRoot);
        }
        catch (ArgumentException)
        {
            return BrokerDiscoveryOptions.Local();
        }
    }
}
