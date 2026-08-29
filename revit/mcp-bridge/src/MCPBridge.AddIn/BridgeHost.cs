using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using MCPBridge.Core.Connection;
using MCPBridge.Core.Discovery;
using MCPBridge.Core.Dispatch;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Protocol;
using MCPBridge.RevitAdapter;

namespace MCPBridge.AddIn;

/// <summary>
/// Owns the background connection thread lifecycle for one Revit process: dials the broker (via
/// BrokerDiscovery + ReconnectLoopController, indefinitely, per PRD §05), performs the mandatory
/// auth/register handshake (PRD §10), then runs an NDJSON read loop handing each incoming request to a
/// RequestDispatcher and writing its response back. Composes the ExternalEvent/ExternalEventBridge/
/// RevitScriptExecutionHandler triple (PR #2 review, Fix 1) so script execution actually runs on Revit's
/// UI thread.
///
/// Deliberately not unit-tested: this is real-socket + real-Revit-UI-thread composition, validated against
/// a live broker and a live Revit session (see the class's own prior review history) -- everything it
/// composes (ExecutionManager, BrokerDiscovery, ReconnectBackoffPolicy/ReconnectLoopController,
/// ExternalEventBridge, RequestDispatcher, NdjsonLineBuffer, AuthMessage/RegisterMessage) is itself fully
/// unit-tested in MCPBridge.Core.Tests.
/// </summary>
internal sealed class BridgeHost
{
    private readonly Guid _instanceId;
    private readonly ExecutionManager _executionManager;
    private readonly ReconnectBackoffPolicy _backoffPolicy;
    private readonly string _revitVersion;
    private readonly BrokerDiscoveryOptions _discoveryOptions;

    private readonly object _writeLock = new();
    private CancellationTokenSource? _stopCts;
    private Thread? _workerThread;
    private volatile TcpClient? _activeTcpClient;

    // The current connection's stream, published for PushRegisterRefresh (issue #30's live snapshot
    // push, called from Revit's UI thread on document events) -- set once auth+register has succeeded,
    // cleared in the same finally that clears _activeTcpClient. Every write through it goes via
    // WriteLine's _writeLock, the same serialization the heartbeat timer already relies on.
    private volatile NetworkStream? _activeStream;
    private Timer? _timeoutTimer;

    // Rooted for the same reason _timeoutTimer is a field rather than a local: an unreferenced Timer is
    // eligible for GC at any point before it fires, one-shot or not.
    private Timer? _discoveryResyncTimer;
    private DiscoveryCache? _discoveryCache;

    // Backing fields for the status snapshot the MCP Bridge ribbon button reads (see
    // MCPBridgeStatusCommand). Set from the connection thread at the exact same points that already
    // define "connected" for reconnect-backoff purposes (OnConnectSucceeded) and "disconnected"
    // (RunConnectionLoop's finally clearing _activeTcpClient) -- no separate state machine, just exposing
    // what this class already tracks internally. Read from Revit's UI thread (when the ribbon button is
    // clicked), so all three are volatile/interlocked-safe rather than requiring a lock a UI-thread read
    // would have no business contending with a background thread over.
    private volatile bool _isConnected;
    private volatile string? _brokerAddress;
    private long _connectedSinceUtcTicks;

    /// <summary>True once auth+register has succeeded on the current connection; false while disconnected/reconnecting.</summary>
    public bool IsConnected => _isConnected;

    /// <summary>The broker's "host:port" for the current (or most recent) connection, if one has ever succeeded.</summary>
    public string? BrokerAddress => _brokerAddress;

    /// <summary>When the current connection's auth+register last succeeded, if <see cref="IsConnected"/>.</summary>
    public DateTimeOffset? ConnectedSince
    {
        get
        {
            var ticks = Interlocked.Read(ref _connectedSinceUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>How often <see cref="_timeoutTimer"/> re-checks max_duration_ms/the cancellation grace period.</summary>
    private static readonly TimeSpan TimeoutCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often a `ping` notification (PRD §05 heartbeat) is sent on an established connection.
    /// Not independently configurable from the Go broker's <c>registry.UnresponsiveThreshold</c> (30s,
    /// three missed pings) -- the two constants aren't coupled across languages, so changing one should
    /// prompt reconsidering the other.
    /// </summary>
    private const int PingIntervalMs = 10_000;

    public BridgeHost(
        Guid instanceId,
        ExecutionManager executionManager,
        ReconnectBackoffPolicy backoffPolicy,
        string revitVersion,
        BrokerDiscoveryOptions discoveryOptions)
    {
        _instanceId = instanceId;
        _executionManager = executionManager;
        _backoffPolicy = backoffPolicy;
        _revitVersion = revitVersion;
        _discoveryOptions = discoveryOptions;
    }

    public void Start()
    {
        if (_workerThread is not null)
        {
            return; // already started; Start() is expected to be called once, from OnStartup.
        }

        // Partial mitigation for Roslyn/other-add-in version collisions (see its own doc comment) --
        // must happen before any script can ever compile.
        RoslynAssemblyIsolation.EnsureInitialized();
        SqliteAssemblyIsolation.EnsureInitialized();

        // The document-snapshot ExternalEvent has no circular dependency (its handler doesn't wrap
        // anything else), so it's created directly.
        var uncPathResolver = new Win32UncPathResolver();
        var documentSnapshotHandler = new DocumentSnapshotHandler(uncPathResolver);
        var documentSnapshotEvent = ExternalEvent.Create(documentSnapshotHandler);

        // The script-execution ExternalEvent has a genuine circular dependency: ExternalEventBridge needs
        // an IExternalEventRaiser wrapping the ExternalEvent, but ExternalEvent.Create needs a handler
        // wrapping the bridge, and the bridge doesn't exist yet. DeferredExternalEventRaiser breaks the
        // cycle: the bridge is constructed against it first (RunAsync/Abandon never touch the raiser until
        // called later, well after Start() has finished wiring), then it's bound to the real ExternalEvent
        // once ExternalEvent.Create actually returns one.
        var deferredRaiser = new DeferredExternalEventRaiser();
        var scriptBridge = new ExternalEventBridge<ScriptExecutionOutcome>(deferredRaiser);
        var scriptExecutionHandler = new RevitScriptExecutionHandler(scriptBridge);
        var scriptExternalEvent = ExternalEvent.Create(scriptExecutionHandler);
        deferredRaiser.Bind(scriptExternalEvent);

        var scriptExecutor = new TransactionScriptExecutor(new RoslynScriptRunner());

        // PRD §08 addendum: discovery is now backed by a persistent SQLite cache (Microsoft.Data.Sqlite +
        // FTS5) instead of reflecting fresh on every Revit process launch -- live-measured cost of the old
        // approach was ~1.5s to enumerate types plus ~700ms per full-corpus search_functions scan, paid
        // again on every restart with nothing carried over. %LOCALAPPDATA%\Connectors\Revit\ is the same
        // app-data root BrokerDiscoveryOptions.Local() already uses (see its own doc comment) -- one place
        // per machine, not a second convention. Independent PR review finding: scoped by _revitVersion --
        // without this, a user with two Revit versions installed launching one after the other would have
        // the second version's Sync() see the first version's RevitAPI.dll as "gone" and cascade-delete its
        // entire cached surface out from under a still-running first-version session.
        // Independent PR review finding (2nd round, M2): Directory.CreateDirectory used to sit OUTSIDE this
        // guard -- a failure there (a locked-down LOCALAPPDATA policy, a full disk, an offline-redirected
        // profile) escaped Start() entirely and took the whole bridge down (no connection loop, no ribbon
        // button, no dialog suppression) over a purely discovery-side path problem, exactly what this guard
        // exists to prevent. Now inside the same try/catch as opening the cache itself.
        //
        // A corrupt/locked cache file (a prior hard crash mid-write, a full disk) must not take down the
        // whole bridge either -- execute_script and everything else has no dependency on discovery. One
        // self-heal attempt (delete and recreate) before giving up and running with discovery disabled
        // entirely; RequestDispatcher accepts a null DiscoveryService for exactly this.
        var discoveryDbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Connectors", "Revit", _revitVersion, "discovery-cache.db");
        DiscoveryService? discoveryService = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(discoveryDbPath)!);
            _discoveryCache = new DiscoveryCache(discoveryDbPath);
        }
        catch (Exception ex)
        {
            LogConnectionDiagnostic($"discovery cache open FAILED, attempting one self-heal (delete and recreate): {ex}");
            try
            {
                // Independent PR review finding (2nd round, M1): DiscoveryCache's own constructor now
                // disposes its connection before rethrowing on any failure past Open() (the dominant real
                // corruption case, "database disk image is malformed", throws from PRAGMA/CreateSchema,
                // AFTER Open() already succeeded) -- without that fix, this File.Delete would routinely hit
                // a Windows sharing violation against a handle the failed constructor never released, and
                // self-heal would never actually self-heal.
                File.Delete(discoveryDbPath);
                _discoveryCache = new DiscoveryCache(discoveryDbPath);
            }
            catch (Exception retryEx)
            {
                LogConnectionDiagnostic($"discovery cache self-heal FAILED, continuing with discovery disabled for this session: {retryEx}");
                _discoveryCache = null;
            }
        }

        if (_discoveryCache is not null)
        {
            SyncDiscoveryCache("initial");
            discoveryService = new DiscoveryService(_discoveryCache);
        }

        // Revit doesn't guarantee add-in load order (PRD §05 already documents the analogous
        // startup-ordering problem for the broker connection itself) -- an add-in that finishes loading
        // AFTER this OnStartup call has already returned would otherwise never get picked up until the next
        // full Revit restart. One deferred, one-shot re-check catches that window without a recurring poll
        // loop; 8s was chosen as comfortably past typical add-in OnStartup duration without meaningfully
        // delaying when a late-loading add-in's API becomes discoverable.
        if (_discoveryCache is not null)
        {
            _discoveryResyncTimer = new Timer(
                _ => SyncDiscoveryCache("deferred"),
                state: null,
                dueTime: TimeSpan.FromSeconds(8),
                period: Timeout.InfiniteTimeSpan);
        }

        var dispatcher = new RequestDispatcher(
            _executionManager,
            scriptBridge,
            scriptExecutor,
            windowInventory: new Win32WindowInventory(),
            discoveryService: discoveryService,
            instanceId: _instanceId.ToString());

        _stopCts = new CancellationTokenSource();
        var stopToken = _stopCts.Token;

        _workerThread = new Thread(() =>
        {
            // Last-resort guard (v1 integrated review): an unhandled exception on a manual thread
            // terminates the entire process -- here, all of Revit, with the user's open models. The
            // loop handles its own per-attempt failures; this catch exists for whatever nobody
            // anticipated, trading "crash the host" for "connection loop dead until Revit restarts,
            // with a log line saying exactly that". OperationCanceledException is the loop's own
            // clean Stop() unwind and needs no log.
            try
            {
                RunConnectionLoop(dispatcher, documentSnapshotHandler, documentSnapshotEvent, stopToken);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                // Stop()'s own clean unwind -- the only OCE that means "shut down quietly". An OCE
                // from any other source falls through to the fatal log below rather than being
                // silently mistaken for a shutdown (PR review suggestion).
            }
            catch (Exception ex)
            {
                LogConnectionDiagnostic($"FATAL: connection loop terminated by an unexpected exception; the add-in will not reconnect until Revit restarts: {ex}");
            }
        })
        {
            IsBackground = true,
            Name = "MCPBridge-Connection",
        };
        _workerThread.Start();

        // Second live-wiring review finding: ExecutionManager.CheckMaxDuration/CheckGraceExpiry
        // (ExecutionManager.cs's own doc comment: "a caller (the AddIn wiring) is expected to drive
        // [these] periodically") were never actually driven anywhere in the add-in -- this IS that AddIn
        // wiring. Without it, max_duration_ms was accepted and stored but never enforced, and the
        // cancellation-grace -> Unrecoverable escalation could never fire from the bridge side. Both calls
        // are pure in-memory state-machine checks (ExecutionManager does its own locking; neither touches
        // the Revit API), so a threadpool Timer callback is fine -- no UI-thread dependency.
        _timeoutTimer = new Timer(
            _ =>
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;

                    // Independent PR review finding: CheckMaxDuration auto-cancelling a still-Pending
                    // execution is the exact same situation RequestDispatcher.HandleCancelExecution calls
                    // scriptBridge.Abandon() for (see its own comment) -- the ExternalEventBridge TCS for
                    // that execution's raise will otherwise never be completed by anything. Without this,
                    // the raise eventually fires once whatever blocked Revit's idle loop clears, finds
                    // nothing awaiting it, and every execute_script after that shares the same wedged
                    // bridge state for the rest of the process's life. Abandon() takes the specific
                    // execution_id (second independent PR review finding) so it can't abandon a different,
                    // unrelated execution's work item that started in the window between this check and
                    // the Abandon() call actually running -- see ExternalEventBridge.Abandon's own comment.
                    if (_executionManager.CheckMaxDuration(now) is { } cancelledPendingExecutionId)
                    {
                        scriptBridge.Abandon(cancelledPendingExecutionId);
                    }

                    _executionManager.CheckGraceExpiry(now);
                }
                catch
                {
                    // A threadpool Timer callback that throws crashes the process (unhandled exception on
                    // a threadpool thread) -- this must never take down all of Revit over a periodic
                    // in-memory state check. Worst case a check is skipped for one interval; the next tick
                    // tries again.
                }
            },
            state: null,
            dueTime: TimeoutCheckInterval,
            period: TimeoutCheckInterval);
    }

    public void Stop()
    {
        _stopCts?.Cancel();

        try
        {
            _activeTcpClient?.Close();
        }
        catch
        {
            // Best-effort: the socket may already be closed/faulted; Stop() must never throw.
        }

        _timeoutTimer?.Dispose();
        _timeoutTimer = null;

        _discoveryResyncTimer?.Dispose();
        _discoveryResyncTimer = null;

        _workerThread?.Join(TimeSpan.FromSeconds(5));
        _workerThread = null;

        _discoveryCache?.Dispose();
        _discoveryCache = null;
    }

    /// <summary>
    /// Reflects Revit's core API assemblies plus every currently-loaded add-in assembly into
    /// <see cref="_discoveryCache"/>'s persistent SQLite store (<see cref="DiscoveryCache.Sync"/> diffs
    /// against what's already there, so calling this twice -- initial + the deferred re-check above -- costs
    /// nothing extra for anything unchanged between the two calls).
    /// </summary>
    private void SyncDiscoveryCache(string reason)
    {
        try
        {
            var result = _discoveryCache!.Sync(CollectAssembliesToSync());
            LogConnectionDiagnostic($"discovery cache sync ({reason}): added={result.Added} updated={result.Updated} removed={result.Removed} unchanged={result.Unchanged}");
        }
        catch (Exception ex)
        {
            // A sync failure must never take down the connection thread or leave discovery permanently
            // broken -- worst case this pass's changes (a rebuilt add-in DLL, a newly-loaded add-in) are
            // missed until the next sync (the deferred one, or implicitly the next Revit restart).
            LogConnectionDiagnostic($"discovery cache sync ({reason}) FAILED: {ex}");
        }
    }

    /// <summary>
    /// PRD §08: discovery covers Revit's own API (RevitAPI.dll/RevitAPIUI.dll, "core") plus whatever other
    /// add-ins have loaded into this same process ("addin") -- an agent scripting against a live Revit
    /// session can call into another add-in's public API just as validly as Revit's own.
    ///
    /// <para>
    /// Independent PR review finding: a name-prefix exclusion list (the original approach) let through
    /// dozens of Autodesk's own internal, undocumented DLLs (UIFramework, AdWindows,
    /// Autodesk.Internal.*, RevitAddInUtility, and similar) -- none of them start with an excluded prefix,
    /// none of them ship an XML-doc sidecar (so <see cref="MCPBridge.Core.Discovery.DiscoveryReflector"/>'s
    /// no-sidecar escape hatch treats every public type in them as "documented"), and the combined noise
    /// dominated <c>list_functions</c>' no-args namespace listing over genuine Revit API and real
    /// third-party add-in namespaces. Filtering on install <em>location</em> instead is a much stronger
    /// signal: genuine third-party add-ins load from an Addins folder (per-user or per-machine), while
    /// Autodesk's own internal, non-API DLLs -- including RevitAPI.dll/RevitAPIUI.dll themselves -- sit
    /// directly in Revit's own install directory. Anything else loaded from that same install directory is
    /// exactly the noise this exclusion needs to catch.
    /// </para>
    ///
    /// <para>
    /// Independent PR review finding (2nd round): a legacy-pattern third-party add-in that was installed by
    /// copying its DLL directly into Revit's own install directory (an old but real workaround some add-ins
    /// used to dodge assembly-resolution problems) is now silently excluded too, with no way to tell that
    /// apart from genuine Autodesk noise after the fact -- so excluded assembly names are logged (once per
    /// sync, not per-assembly-forever) rather than disappearing with zero trace, matching PRD §01's
    /// observability principle. Also fixed: both path comparisons now use OrdinalIgnoreCase --
    /// Assembly.Location reflects whatever casing the loader happened to use (a .addin manifest, a registry
    /// value), which routinely differs from the on-disk casing on Windows' case-insensitive filesystem, and
    /// an ordinal case-sensitive compare could let exactly the noise this filter exists to catch back in.
    /// </para>
    /// </summary>
    private static IReadOnlyList<(string Kind, Assembly Assembly)> CollectAssembliesToSync()
    {
        var coreAssemblies = new[] { typeof(Autodesk.Revit.DB.Document).Assembly, typeof(UIApplication).Assembly };
        var assemblies = new List<(string Kind, Assembly Assembly)>(coreAssemblies.Select(a => ("core", a)));

        var revitInstallDir = Path.GetDirectoryName(coreAssemblies[0].Location);
        var excludedPrefixes = new[] { "System.", "Microsoft.", "MCPBridge.", "mscorlib", "netstandard", "WindowsBase", "PresentationCore", "PresentationFramework" };
        var excludedByInstallDir = new List<string>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(assembly.Location))
            {
                continue;
            }

            if (excludedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            if (coreAssemblies.Any(core => string.Equals(core.Location, assembly.Location, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // already added above as "core".
            }

            if (revitInstallDir is not null && string.Equals(Path.GetDirectoryName(assembly.Location), revitInstallDir, StringComparison.OrdinalIgnoreCase))
            {
                excludedByInstallDir.Add(name);
                continue; // Autodesk's own internal DLLs living alongside RevitAPI.dll -- not a real add-in.
            }

            assemblies.Add(("addin", assembly));
        }

        if (excludedByInstallDir.Count > 0)
        {
            LogConnectionDiagnostic($"discovery: excluded {excludedByInstallDir.Count} assembly(ies) loaded from Revit's own install directory: {string.Join(", ", excludedByInstallDir)}");
        }

        return assemblies;
    }

    /// <summary>How long a single broker.json discovery attempt (<see cref="TryDiscoverWithTimeout"/>) may
    /// take before being treated as a transient failure. broker.json lives on local disk in the default
    /// (local-mode) topology, so this should never be reached under healthy conditions -- it exists purely
    /// as a circuit-breaker for the host-level I/O stalls (antivirus real-time scanning, virtualized-disk
    /// contention under a busy VM) confirmed live to occasionally freeze a plain synchronous file read for
    /// an extended period on this project's own dev VM.</summary>
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);

    private void RunConnectionLoop(RequestDispatcher dispatcher, DocumentSnapshotHandler documentSnapshotHandler, ExternalEvent documentSnapshotEvent, CancellationToken stopToken)
    {
        LogConnectionDiagnostic($"RunConnectionLoop starting. Mode={_discoveryOptions.Mode} ConnectorRoot={_discoveryOptions.ConnectorRoot}");
        var discovery = new BrokerDiscovery(_discoveryOptions);
        var reconnectController = new ReconnectLoopController(_backoffPolicy);

        while (!stopToken.IsCancellationRequested)
        {
            LogConnectionDiagnostic("loop iteration: about to TryDiscover");

            // Discovery gets its own guard (v1 integrated review): it used to run bare, outside the
            // try that protects RunOneConnection below, so anything TryDiscover threw beyond the two
            // exception types BrokerDiscovery catches internally -- an UnauthorizedAccessException
            // from a flapping UNC share, historically a FormatException from a malformed broker.json
            // -- escaped this loop entirely. On a manual thread that means either the reconnect loop
            // silently dying (never reconnecting again for the life of the Revit session, breaking
            // PRD §05's indefinite-retry contract invisibly) or, since nothing above this frame
            // catches, an unhandled-exception crash of the whole Revit process. Both are worse than
            // the worst case a discovery attempt can actually represent, which is "no broker this
            // attempt -- try again".
            BrokerDiscoveryResult discoveryResult;
            try
            {
                discoveryResult = TryDiscoverWithTimeout(discovery, stopToken);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                break; // Stop() was called during the discovery wait -- clean thread exit, not a crash.
            }
            catch (Exception ex)
            {
                // Includes an OCE that ISN'T Stop()'s (the when-filter above): a cancellation nobody
                // requested is an anomaly to log and retry past, not a reason to end the loop.
                LogConnectionDiagnostic($"broker discovery attempt threw unexpectedly: {ex}");
                Backoff(reconnectController, stopToken);
                continue;
            }

            if (!discoveryResult.Found || discoveryResult.BrokerJson is null || discoveryResult.Address is null)
            {
                // Not found (or found but unreadable/malformed -- BrokerDiscovery already reports both as
                // "not found"): a failed attempt; back off, per PRD §05's single retry loop. (This check
                // once also discarded a remote-mode fallback address here -- that config surface is gone
                // now, see BrokerDiscoveryOptions; on a not-found result there is simply no address.)
                //
                // Was previously silent -- PRD §01's observability principle ("caught and swallowed" must
                // still leave a trace, not mean invisible) didn't actually hold here: a broker.json that's
                // missing, unreadable, or fails to parse produced literally no evidence anywhere that this
                // loop was even trying, let alone why it kept failing -- discovered live, chasing exactly
                // that symptom, when even direct filesystem/network checks outside the add-in all
                // checked out fine and the real cause turned out to be reachable only from inside this
                // loop's own exception handling.
                LogConnectionDiagnostic($"broker discovery failed: {discoveryResult.Diagnostic?.Message ?? "(broker.json not found)"}");
                Backoff(reconnectController, stopToken);
                continue;
            }

            // Observability (PRD §01), and the direct fix for a real, repeated misdiagnosis: until this
            // line existed, a SUCCESSFUL discovery logged nothing at all, and RunOneConnection logged
            // nothing on entry or on success either -- so a perfectly healthy, authenticated, registered,
            // idle connection produced exactly the same log tail as an indefinite hang: "loop iteration:
            // about to TryDiscover" and then silence forever. That silence was twice root-caused as a
            // stall in the discovery read itself (once genuinely, once not), and the second time cost a
            // full investigation into a connection that was in fact already live and serving
            // list_instances. Both endpoints of every attempt are now logged, so "no further lines" can
            // only ever mean "still inside the step this line names".
            LogConnectionDiagnostic($"broker discovered at {discoveryResult.Address.Host}:{discoveryResult.Address.Port}; connecting");

            try
            {
                RunOneConnection(discoveryResult.BrokerJson, discoveryResult.Address, dispatcher, documentSnapshotHandler, documentSnapshotEvent, reconnectController, stopToken);
                LogConnectionDiagnostic($"connection to {discoveryResult.Address.Host}:{discoveryResult.Address.Port} ended (broker closed it, or Stop() was called); will reconnect");
            }
            catch (Exception ex)
            {
                // Same observability gap, on the other failure path: this used to catch and discard the
                // exception with zero trace -- an auth rejection, a connect timeout, or any socket-level
                // failure looked identical to "never even tried" from the outside.
                LogConnectionDiagnostic($"connection attempt to {discoveryResult.Address.Host}:{discoveryResult.Address.Port} failed: {ex}");
                // Any failure during this connection's lifetime (auth rejected, socket error, broker
                // closed unexpectedly) falls through to backoff-and-retry -- the reconnect loop is
                // indefinite by design (PRD §05), never fatal to this thread.
            }
            finally
            {
                _activeStream = null;
                _activeTcpClient = null;
                _isConnected = false;
            }

            if (!stopToken.IsCancellationRequested)
            {
                Backoff(reconnectController, stopToken);
            }
        }
    }

    /// <summary>
    /// Bounds <see cref="BrokerDiscovery.TryDiscover"/>'s otherwise-unbounded synchronous file read
    /// (<c>File.Exists</c>/<c>File.ReadAllText</c> against broker.json, with no timeout of its own) to
    /// <see cref="DiscoveryTimeout"/>. Root-caused live: this connection loop was observed silently
    /// stalling indefinitely (minutes to, in one case, over two hours) with the last log line always
    /// exactly "loop iteration: about to TryDiscover" and no follow-up ever -- no exception, no timeout,
    /// just silence, recoverable only by restarting Revit itself (never by waiting, and never by fixing
    /// whatever transient condition caused it, since by the time it's noticed the call is already wedged).
    /// The call itself is a plain synchronous local-disk read with nothing async or thread-pool-related in
    /// it, so the cause isn't contention for scheduling -- it's that a transient host-level I/O stall (this
    /// project's dev VM has independently confirmed antivirus real-time scanning and virtualized-storage
    /// contention as real, recurring sources of exactly this kind of stall) has no bound at all once it
    /// starts, and every other step in this loop is either already bounded (the TCP connect, the document
    /// snapshot wait) or fast by construction -- this was the one genuinely unbounded step.
    ///
    /// Uses the same "bounded wait, proceed without the value on timeout" shape as the document-snapshot
    /// wait above (<c>documentsTask.Wait(10_000, stopToken)</c>) rather than inventing a new pattern:
    /// <c>Task.Run</c> hands the synchronous read to a threadpool thread so this dedicated connection
    /// thread's own wait can be bounded and cancellation-aware; if the read is still stuck when
    /// <see cref="DiscoveryTimeout"/> elapses, this treats the attempt as a transient not-found (the same
    /// outcome <see cref="BrokerDiscovery.TryDiscover"/> itself already returns for a missing/unreadable
    /// broker.json) and lets the loop's existing backoff-and-retry take over -- the abandoned threadpool
    /// task is simply left to finish (or never finish) on its own and get garbage-collected; it holds no
    /// resource this process needs back.
    /// </summary>
    private BrokerDiscoveryResult TryDiscoverWithTimeout(BrokerDiscovery discovery, CancellationToken stopToken)
    {
        var discoverTask = Task.Run(discovery.TryDiscover, stopToken);
        try
        {
            if (discoverTask.Wait(DiscoveryTimeout, stopToken))
            {
                return discoverTask.Result;
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Stop() was called during the wait -- let this connection attempt unwind cleanly.
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException; // Preserve TryDiscover's own exception shape/stack for the outer catch.
        }

        LogConnectionDiagnostic($"broker discovery timed out after {DiscoveryTimeout.TotalSeconds:0}s (a stalled broker.json read) -- treating as not found and retrying.");
        return BrokerDiscoveryResult.NotFound();
    }

    /// <summary>
    /// Best-effort append to %LocalAppData%\Connectors\Revit\connection.log -- the reconnect loop's own
    /// equivalent of MCPBridgeApplication.TryLogDiagnostic (PRD §01 observability: a caught-and-swallowed
    /// failure still deserves a trace somewhere, not total silence). Deliberately a separate file from
    /// startup-errors.log: this loop retries indefinitely and can log far more often than a one-shot
    /// OnStartup failure ever would, so keeping them apart means a busy connection log never buries a
    /// startup failure underneath it. Always the LOCAL per-machine directory regardless of local/remote
    /// topology (see TryLogDiagnostic's own comment for why), reusing BrokerDiscoveryOptions.Local()'s
    /// path computation rather than hand-rolling it a second time.
    /// </summary>
    private static void LogConnectionDiagnostic(string message)
    {
        try
        {
            var directory = BrokerDiscoveryOptions.Local().ConnectorRoot;
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "connection.log"), $"{DateTimeOffset.UtcNow:O} {message}\n");
        }
        catch
        {
            // Best-effort diagnostic only -- a failure here must never mask or interfere with the
            // reconnect loop itself, which already handles its own failures independently.
        }
    }

    private static void Backoff(ReconnectLoopController reconnectController, CancellationToken stopToken)
    {
        var delay = reconnectController.OnConnectFailed();
        try
        {
            Task.Delay(delay, stopToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Stop() was called during the backoff wait -- fine, the outer loop's own stopToken check
            // ends the loop next.
        }
    }

    private void RunOneConnection(
        BrokerJson brokerJson,
        BrokerAddress address,
        RequestDispatcher dispatcher,
        DocumentSnapshotHandler documentSnapshotHandler,
        ExternalEvent documentSnapshotEvent,
        ReconnectLoopController reconnectController,
        CancellationToken stopToken)
    {
        using var tcpClient = new TcpClient();
        _activeTcpClient = tcpClient;

        if (!tcpClient.ConnectAsync(address.Host, address.Port).Wait(TimeSpan.FromSeconds(5)))
        {
            throw new IOException($"connect to broker at {address.Host}:{address.Port} timed out.");
        }

        using var stream = tcpClient.GetStream();

        // Bounded writes (PR #50 review finding): stream.Write against a wedged-but-connected broker
        // blocks indefinitely by default -- the heartbeat's self-rearm design already documents that
        // hazard for its own thread, but PushRegisterRefresh writes from Revit's UI THREAD (document
        // events), where an unbounded block freezes Revit itself with no escape. A timed-out write
        // throws IOException into the existing per-path handling: the push's catch drops the refresh
        // (the next connect's register carries it), and a response/heartbeat write tears the
        // connection down into the normal reconnect loop -- both the right outcome against a peer
        // that has stopped draining the socket.
        stream.WriteTimeout = 10_000;
        var buffer = new NdjsonLineBuffer();
        var pendingLines = new Queue<string>();
        var readBuffer = new byte[8192];

        // MANDATORY two-step handshake, per PRD §10 and the Go broker's actual behavior (broker.go's
        // handleConn): `auth` must be the very first message; only after it succeeds does the broker
        // expect `register`. On failure the broker replies with a JSON-RPC error and closes the
        // connection outright -- there is no retry within the same socket.
        //
        // The document snapshot (a Revit UI-thread ExternalEvent dispatch) doesn't depend on auth
        // succeeding -- it's only needed once `register` is actually built, below -- so it's kicked off
        // here, immediately after writing the auth request, and only awaited once the auth round trip has
        // already completed. That overlaps one network round trip with one UI-thread dispatch instead of
        // paying both latencies back-to-back on every connect/reconnect.
        var authMessage = new AuthMessage(id: 1, token: brokerJson.Token, role: AuthRole.AddIn);
        WriteLine(stream, authMessage.ToJson());
        var documentsTask = documentSnapshotHandler.SnapshotAsync(documentSnapshotEvent);

        var authResponseLine = ReadOneLine(stream, buffer, pendingLines, readBuffer, stopToken)
            ?? throw new IOException("broker closed the connection before an auth response arrived.");

        using (var authDoc = JsonDocument.Parse(authResponseLine))
        {
            if (!authDoc.RootElement.TryGetProperty("result", out var resultElement) ||
                !resultElement.TryGetProperty("ok", out var okElement) ||
                okElement.ValueKind != JsonValueKind.True)
            {
                throw new IOException($"broker rejected auth: {authResponseLine}");
            }
        }

        // `register`, sent on every successful connect -- first connect and every reconnect alike (PRD
        // §05) -- with the real, live values: the stable per-process instance_id, the real process id, the
        // real Revit version, and a fresh snapshot of currently open documents.
        //
        // Second live-wiring review finding: ExternalEvent.Raise() returning Accepted only means "queued" --
        // Revit runs the handler whenever its idle loop next gets to it, which can be indefinitely delayed
        // (a modal dialog open, another long-running ExternalEvent already in flight). An unbounded wait
        // here would block this connection thread forever in that case, and since Stop() (called from
        // OnShutdown, which runs ON Revit's UI thread) can't do anything to unblock a wait that itself
        // depends on the UI thread, that also turns Stop() into a guaranteed-to-time-out Join(). Bound the
        // wait instead: if the snapshot doesn't arrive in time, proceed with an empty document list rather
        // than never sending `register` at all -- a live connection with an incomplete document list is far
        // better than no connection.
        List<RegisteredDocument> documents;
        try
        {
            documents = documentsTask.Wait(10_000, stopToken)
                ? documentsTask.GetAwaiter().GetResult()
                : new List<RegisteredDocument>();
        }
        catch (OperationCanceledException)
        {
            throw; // Stop() was called during the wait -- let this connection attempt unwind cleanly.
        }
        catch
        {
            // The snapshot ExternalEvent faulted (Denied/TimedOut raise, or an exception building it) --
            // proceed with an empty document list rather than throwing away an otherwise-good, already-
            // authenticated connection over a problem unrelated to the connection itself.
            documents = new List<RegisteredDocument>();
        }

        var registerMessage = new RegisterMessage(_instanceId, Process.GetCurrentProcess().Id, _revitVersion, documents);
        WriteLine(stream, registerMessage.ToJson());

        // A live connection with a successful auth+register exchange is what "connected" means for
        // backoff-reset purposes -- reset here, not merely on TCP connect, and not merely once this method
        // returns (which happens on disconnect, the opposite condition). Same moment defines "connected"
        // for the ribbon status button.
        reconnectController.OnConnectSucceeded();
        // _isConnected MUST be written last, after _brokerAddress/_connectedSinceUtcTicks (independent PR
        // review confirmed this ordering is what makes the three safe to read together from another
        // thread without a lock): _isConnected is the volatile "release" a UI-thread reader synchronizes
        // on -- observing _isConnected == true is only meaningful as a guarantee that the writes before it
        // are also visible if it's genuinely the LAST of the three to be written. Reordering these three
        // lines would reopen a window where a status read could observe IsConnected=true alongside a
        // stale/null BrokerAddress from a previous connection.
        _brokerAddress = $"{address.Host}:{address.Port}";
        Interlocked.Exchange(ref _connectedSinceUtcTicks, DateTimeOffset.UtcNow.Ticks);
        _activeStream = stream; // published only once the connection is fully established (auth+register done)
        _isConnected = true;

        // The single most important line in this log: "connected" is otherwise invisible, and its absence
        // was being read as evidence of a hang (see RunConnectionLoop's own comment on this). Logged after
        // the three status fields above so a log line saying "connected" can never be observed alongside a
        // status snapshot that doesn't yet agree.
        LogConnectionDiagnostic($"connected: auth+register succeeded at {address.Host}:{address.Port} (instance {_instanceId}, {documents.Count} document(s)); entering read loop");

        // Heartbeat (PRD §05): a periodic `ping` notification so the broker can tell a live-but-wedged
        // Revit process apart from a merely-quiet one. Scoped as a `using var` local, not a field like
        // _timeoutTimer/_discoveryResyncTimer -- its whole lifetime is this one connection attempt, so
        // the local variable itself keeps it rooted for exactly as long as it needs to be, and it's
        // disposed automatically on every exit path from this method (clean return, exception, or
        // falling through) rather than needing a separate cleanup step, so a stale timer from a dead
        // connection never keeps firing writes against it. This thread is the only writer of the
        // NetworkStream's actual bytes -- the timer callback goes through WriteLine's own _writeLock like
        // every other writer, so it's safe to fire from the timer's own thread pool thread concurrently
        // with this connection's read loop.
        //
        // One-shot-and-self-rearm (period: Timeout.Infinite, re-armed at the end of the callback) rather
        // than a recurring period: WriteLine's underlying stream.Write blocks indefinitely if the peer
        // stops draining the socket (a wedged/paused broker) -- with a recurring period, every 10s another
        // thread-pool thread would pile into the callback and block on _writeLock behind the first one,
        // unbounded, for as long as that condition persists. Self-rearming caps this at one outstanding
        // ping attempt at a time.
        Timer? heartbeatTimer = null;
        heartbeatTimer = new Timer(
            _ =>
            {
                try
                {
                    WriteLine(stream, PingMessage.ToJson());
                }
                catch
                {
                    // A failed ping write means the connection is already dead; the read loop's own next
                    // ReadOneLine call will observe that and trigger a reconnect -- same tolerated-write-
                    // failure pattern as the dispatch-response write below.
                }
                finally
                {
                    try
                    {
                        heartbeatTimer?.Change(PingIntervalMs, Timeout.Infinite);
                    }
                    catch (ObjectDisposedException)
                    {
                        // This connection's loop already exited and disposed the timer between the write
                        // attempt above and this re-arm -- nothing left to reschedule.
                    }
                }
            },
            state: null,
            dueTime: PingIntervalMs,
            period: Timeout.Infinite);
        using var heartbeatTimerDisposal = heartbeatTimer;

        while (!stopToken.IsCancellationRequested)
        {
            var line = ReadOneLine(stream, buffer, pendingLines, readBuffer, stopToken);
            if (line is null)
            {
                return; // broker closed the connection (or the read was aborted by Stop()); reconnect.
            }

            JsonRpcRequest request;
            try
            {
                request = JsonRpcRequest.Parse(line);
            }
            catch
            {
                continue; // malformed line -- skip it rather than tearing down the whole connection.
            }

            // Fire-and-continue: execute_script's own dispatch can take a while (up to its timeout_ms) and
            // must not block this thread from reading/dispatching poll_execution/cancel_execution for
            // other in-flight work in the meantime.
            //
            // Second live-wiring review finding: DispatchAsync's poll_execution/cancel_execution branches
            // are non-async (an expression-bodied switch), so any exception they throw (e.g. a malformed
            // request that slips past JsonRpcRequest.Parse's own validation) surfaces SYNCHRONOUSLY from
            // this very call, not via the returned Task -- it would otherwise propagate straight out of
            // this while loop, past the ContinueWith below (which never gets attached), and be caught only
            // by RunConnectionLoop's outer catch, tearing down and re-dialing an otherwise-healthy
            // connection over one bad request. Wrapped here so a dispatch failure -- synchronous or via a
            // faulted Task -- always produces a JSON-RPC error response instead of silently doing nothing
            // (dropping the response entirely) or killing the connection.
            Task<string> dispatchTask;
            try
            {
                dispatchTask = dispatcher.DispatchAsync(request);
            }
            catch (Exception ex)
            {
                dispatchTask = Task.FromResult(JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InternalError, $"dispatch failed: {ex.Message}", null));
            }

            _ = dispatchTask.ContinueWith(
                t =>
                {
                    var response = t.IsCompletedSuccessfully
                        ? t.Result
                        : JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InternalError, $"dispatch failed: {t.Exception?.GetBaseException().Message}", null);

                    try
                    {
                        WriteLine(stream, response);
                    }
                    catch
                    {
                        // A write failure here means the connection is already dead; the read loop's own
                        // next ReadOneLine call will observe that and trigger a reconnect.
                    }
                },
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Sends a fresh `register` (same message, same broker-side replace semantics as the connect-time
    /// one) over the current live connection -- the live document-snapshot push that closes issue #30's
    /// one-shot-snapshot race. Called from Revit's UI thread by MCPBridgeApplication's document-event
    /// handlers (DocumentOpened/Closed/Created, ViewActivated); WriteLine's _writeLock serializes it
    /// against the connection thread's own writes and the heartbeat timer, the same way the heartbeat
    /// already shares that stream. A push racing a disconnect fails or no-ops harmlessly: the next
    /// connect's own register carries an equally-fresh snapshot regardless.
    /// </summary>
    public void PushRegisterRefresh(List<RegisteredDocument> documents)
    {
        var stream = _activeStream;
        if (stream is null || !_isConnected)
        {
            return; // no live connection -- connect-time register will carry the current snapshot
        }

        var message = new RegisterMessage(_instanceId, Process.GetCurrentProcess().Id, _revitVersion, documents);
        try
        {
            WriteLine(stream, message.ToJson());

            // This log line's shape is LOAD-BEARING for dev tooling: redeploy-and-verify.ps1's
            // registration wait matches "register refreshed: ... N document(s)" as evidence the
            // document snapshot caught up, now that the push (not a forced broker restart) is what
            // refreshes it. Keep the count phrasing in sync with that script if either changes.
            LogConnectionDiagnostic($"register refreshed: {documents.Count} document(s), pushed on a document open/close/activate event");
        }
        catch (Exception ex)
        {
            LogConnectionDiagnostic($"register refresh push failed (connection likely tearing down; the next reconnect's register carries the same state): {ex.Message}");
        }
    }

    private void WriteLine(NetworkStream stream, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(NdjsonLineBuffer.Encode(json));
        lock (_writeLock)
        {
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    private static string? ReadOneLine(NetworkStream stream, NdjsonLineBuffer buffer, Queue<string> pendingLines, byte[] readBuffer, CancellationToken stopToken)
    {
        while (pendingLines.Count == 0)
        {
            if (stopToken.IsCancellationRequested)
            {
                return null;
            }

            int read;
            try
            {
                read = stream.Read(readBuffer, 0, readBuffer.Length);
            }
            catch (IOException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            if (read == 0)
            {
                return null; // graceful close
            }

            foreach (var line in buffer.Append(readBuffer.AsSpan(0, read)))
            {
                pendingLines.Enqueue(line);
            }
        }

        return pendingLines.Dequeue();
    }

    /// <summary>
    /// Breaks the ExternalEventBridge/ExternalEvent circular construction dependency (see Start()'s own
    /// comment): the bridge is built against this raiser before the real ExternalEvent exists, and
    /// Bind(...) attaches the real one afterward. Raise() is only ever called later, via
    /// ExternalEventBridge.RunAsync, well after Start() has finished wiring everything up, so by the time
    /// it's called Bind(...) has always already run.
    /// </summary>
    private sealed class DeferredExternalEventRaiser : IExternalEventRaiser
    {
        private RevitExternalEventRaiser? _inner;

        public void Bind(ExternalEvent externalEvent) => _inner = new RevitExternalEventRaiser(externalEvent);

        public ExternalEventRaiseOutcome Raise() =>
            _inner?.Raise() ?? throw new InvalidOperationException("DeferredExternalEventRaiser.Bind must be called before Raise().");
    }
}
