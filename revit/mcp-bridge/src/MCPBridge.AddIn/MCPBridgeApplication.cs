using System;
using System.IO;
using Autodesk.Revit.UI;
using MCPBridge.Core.Connection;
using MCPBridge.Core.Diagnostics;
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

    /// <summary>
    /// The ribbon's "MCP Server: Local / REMOTE" toggle (issue #185), kept so its label can be rewritten
    /// after a switch -- see <see cref="UpdateBrokerModeButton"/>. Null if ribbon creation failed (a
    /// best-effort UI nicety, like the Status button it sits beside).
    /// </summary>
    private static PushButton? _brokerModeButton;

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
            UpdateBrokerModeButton(discoveryOptions);
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
    /// this is that fix).
    /// <para>Size-capped by RollingDiagnosticLog for the same reason connection.log is (issue #11).
    /// Less urgent here -- this file's writes are per-startup, not per-retry -- but it is the same
    /// unbounded append into the same directory, and leaving one of the two capped would only invite
    /// the question of why.</para></summary>
    internal static void TryLogDiagnostic(string message)
        => RollingDiagnosticLog.Append(() => BrokerDiscoveryOptions.Local().ConnectorRoot, "startup-errors.log", message);

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
        // Saves matter too (PR #50 review finding): a save is the ONE event that CHANGES a
        // document's id (tmp- -> doc- promotion on first save; a new doc- on Save-As), so without
        // these the registry advertises a dead tmp- id until some unrelated event fires -- making
        // the routing error's "list_instances reflects the current state" remedy false in exactly
        // that window.
        application.ControlledApplication.DocumentSaved += OnDocumentSaved;
        application.ControlledApplication.DocumentSavedAs += OnDocumentSavedAs;
        application.ViewActivated += OnViewActivated;
    }

    private void UnsubscribeDocumentEvents(UIControlledApplication application)
    {
        application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
        application.ControlledApplication.DocumentCreated -= OnDocumentCreated;
        application.ControlledApplication.DocumentClosed -= OnDocumentClosed;
        application.ControlledApplication.DocumentSaved -= OnDocumentSaved;
        application.ControlledApplication.DocumentSavedAs -= OnDocumentSavedAs;
        application.ViewActivated -= OnViewActivated;
    }

    private void OnDocumentSaved(object? sender, Autodesk.Revit.DB.Events.DocumentSavedEventArgs e) =>
        PushSnapshotFromApplicationEvent(sender);

    private void OnDocumentSavedAs(object? sender, Autodesk.Revit.DB.Events.DocumentSavedAsEventArgs e) =>
        PushSnapshotFromApplicationEvent(sender);

    private void OnDocumentOpened(object? sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e) =>
        PushSnapshotFromApplicationEvent(sender);

    private void OnDocumentCreated(object? sender, Autodesk.Revit.DB.Events.DocumentCreatedEventArgs e) =>
        PushSnapshotFromApplicationEvent(sender);

    // NOTE on §09's "tmp/ cleared on document close" (issue #13): deliberately NOT done here.
    // DocumentClosedEventArgs carries only Revit's internal integer DocumentId -- the §09 identity
    // (path- or title-derived) is unrecoverable once the document object is gone, so this handler
    // cannot know WHICH tmp/<instance-id>/ under WHICH workspace belonged to the closed document.
    // Pairing the cancellable DocumentClosing event (which still has the Document) with Closed just
    // to delete scratch a few days earlier than ExecutionAuditTrail's 14-day sweep would is
    // machinery the overengineering test refuses; the sweep is the retention mechanism.
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
    /// Adds the "MCP Bridge" ribbon panel on the Add-Ins tab: "Status" (per the user's own request: a
    /// quick, no-context-needed way to check "is this actually connected" and "what build/commit is
    /// this" without going through logs or an external tool -- exactly the two questions that took the
    /// most manual digging to answer during this add-in's own live-wiring development), plus issue
    /// #185's "Reconnect" and "MCP Server: Local/REMOTE" buttons (the reconnect tool, and the topology
    /// switch that makes the Mac-broker dev case an explicit, VISIBLE choice rather than an environment
    /// variable nobody can see). Best-effort: a ribbon-creation failure (e.g. a panel name collision
    /// with another add-in) must not fail the whole add-in load over a UI nicety, so it's caught and
    /// swallowed here specifically, not folded into OnStartup's own broader catch.
    /// </summary>
    private static void CreateStatusRibbonButton(UIControlledApplication application)
    {
        try
        {
            var panel = application.CreateRibbonPanel("MCP Bridge");
            var assemblyLocation = typeof(MCPBridgeApplication).Assembly.Location;
            // All three stay enabled with no document open -- see MCPBridgeCommandAvailability.
            var availability = typeof(MCPBridgeCommandAvailability).FullName;
            panel.AddItem(new PushButtonData(
                "MCPBridgeStatus",
                "Status",
                assemblyLocation,
                typeof(MCPBridgeStatusCommand).FullName)
            {
                ToolTip = "Show the MCP Bridge connection status, which MCP Server it uses (local or remote), and build info.",
                AvailabilityClassName = availability,
            });
            panel.AddItem(new PushButtonData(
                "MCPBridgeReconnect",
                "Reconnect",
                assemblyLocation,
                typeof(MCPBridgeReconnectCommand).FullName)
            {
                ToolTip = "Reconnect to the MCP Server now (e.g. after it was restarted), instead of waiting for the automatic retry.",
                AvailabilityClassName = availability,
            });
            // Label/tooltip are placeholders here; UpdateBrokerModeButton writes the real ones once the
            // resolved options are known (OnStartup) and after every switch.
            _brokerModeButton = panel.AddItem(new PushButtonData(
                "MCPBridgeBrokerMode",
                "MCP Server",
                assemblyLocation,
                typeof(MCPBridgeBrokerModeCommand).FullName)
            {
                AvailabilityClassName = availability,
            }) as PushButton;
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
    /// Rewrites the broker-mode toggle's label to the ACTIVE topology -- "MCP Server: Local" or,
    /// loudly, "MCP Server: REMOTE" (user-facing text says "MCP Server", CONVENTIONS.md's name for
    /// the broker; "broker" is the internal term and means nothing to a user) -- so that during
    /// development it is unambiguous from the ribbon alone which
    /// broker owns this Revit session (the #185 symptom was precisely a Revit that looked healthy while
    /// registered with a broker nobody was querying). Called from OnStartup and from the toggle command
    /// after a switch; both run on Revit's UI thread, which is where ribbon items may be mutated.
    /// </summary>
    internal static void UpdateBrokerModeButton(BrokerDiscoveryOptions options)
    {
        var button = _brokerModeButton;
        if (button is null)
        {
            return;
        }

        try
        {
            if (options.Mode == BrokerTopologyMode.Remote)
            {
                button.ItemText = "MCP Server:\nREMOTE";
                button.ToolTip = $"Using a REMOTE MCP Server on another machine, found via the shared drive at {options.ConnectorRoot} (this project's Mac+Parallels dev setup). Click to switch back to the MCP Server on this machine.";
            }
            else
            {
                button.ItemText = "MCP Server:\nLocal";
                button.ToolTip = $"Using the MCP Server on this machine (the default), found via {options.ConnectorRoot}. Click to switch to a REMOTE MCP Server on another machine, via a shared drive.";
            }
        }
        catch (Exception ex)
        {
            TryLogDiagnostic($"UpdateBrokerModeButton failed: {ex}");
        }
    }

    /// <summary>
    /// Resolves the broker topology to dial at startup (issue #185): bridge-config.json (written by
    /// the ribbon's broker-mode switch) → MCPBRIDGE_BROKER_MODE/MCPBRIDGE_SHARED_ROOT (the original
    /// mechanism, kept for the dev launchers) → Local (PRD §05: "the real target deployment"). The
    /// precedence, its rationale, and every fallback rule live in <see cref="BrokerModeResolver"/>,
    /// which is pure and unit-tested; this method only supplies the file read and the real
    /// environment, and logs the decision -- source included -- so "why is Revit dialing THAT broker"
    /// is answerable from startup-errors.log alone. Never throws over a topology setting: a bad or
    /// unreadable config, or a remote choice with no usable shared root, is logged and falls back to
    /// Local, exactly as the env-only version always did. (MCPBRIDGE_FALLBACK_HOST/PORT were once read
    /// here too and are deliberately gone -- see BrokerDiscoveryOptions for why they could never work.)
    /// </summary>
    private static BrokerDiscoveryOptions BuildDiscoveryOptions()
    {
        var configPath = BridgeConfig.DefaultPath();
        var loaded = BridgeConfig.Load(configPath);
        if (loaded.Diagnostic is { } loadDiagnostic)
        {
            TryLogDiagnostic($"{loadDiagnostic.Code}: {loadDiagnostic.Message}");
        }

        var resolution = BrokerModeResolver.Resolve(loaded.Config, Environment.GetEnvironmentVariable);
        if (resolution.ConfigDiagnostic is { } configDiagnostic)
        {
            TryLogDiagnostic($"{configDiagnostic.Code}: {configDiagnostic.Message}");
        }

        if (resolution.Diagnostic is { } resolveDiagnostic)
        {
            TryLogDiagnostic($"{resolveDiagnostic.Code}: {resolveDiagnostic.Message}");
        }

        TryLogDiagnostic($"broker mode decided by {resolution.Source} (config file: {(loaded.Config is null ? "absent" : configPath)})");
        return resolution.Options;
    }
}
