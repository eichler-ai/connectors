using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Rhino.MCPBridge.Core.Capture;
using Rhino.MCPBridge.Core.Connection;
using Rhino.MCPBridge.Core.Diagnostics;
using Rhino.MCPBridge.Core.Dispatch;
using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.Core.Execution.Python;
using DiagnosticSource = Rhino.MCPBridge.Core.Diagnostics.DiagnosticSource;
using Rhino.MCPBridge.Core.Protocol;
using Rhino.MCPBridge.RhinoAdapter;

namespace Rhino.MCPBridge.PlugIn;

/// <summary>
/// Owns the listener for one Rhino process (PRD §05, the dial-in design): binds 127.0.0.1 on an
/// ephemeral port, writes instances/&lt;pid&gt;.json, accepts every server that dials in and runs a
/// <see cref="ConnectionSession"/> per connection, fans register refreshes and heartbeat pings out to
/// all of them, and deletes the instance file on stop. Composition, not decision logic: everything it
/// wires is tier-1 tested in Core. Not unit-tested itself (real sockets, real Rhino), exercised by
/// the live harness.
///
/// The register snapshot is a CACHE, rebuilt on the main thread at start and on every document event
/// (which fire on the main thread), and read by connection threads without any main-thread hop. So a
/// server that dials in while Rhino's main thread is inside a modal -- the template chooser a fresh
/// launch sits at -- still gets an auth answer and a register (with the last-known documents) instead
/// of a connection that hangs forever (review of #281).
/// </summary>
internal sealed class BridgeHost : ISessionEnvironment
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    private readonly Guid _instanceId;
    private readonly string _rhinoVersion;
    private readonly string _bridgeVersion;
    private readonly IMainThread _mainThread;
    private readonly IDocumentSnapshotSource _documents;
    private readonly RequestDispatcher _dispatcher;
    private readonly TransactionlessWarmup _warmup;
    private Timer? _tickTimer;
    private readonly string _instancesDir;
    private readonly Action<string> _log;
    private readonly SessionSet _sessions = new();
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset _startedAt;

    private TcpListener? _listener;
    private Thread? _acceptThread;
    private Timer? _pingTimer;
    private int _pingInFlight;
    private volatile RegisterSnapshot _snapshot;
    private IDisposable? _changeMonitor;
    private Rhino.MCPBridge.Core.Discovery.DiscoveryCache? _discoveryCache;
    private readonly string _rhinoVersionForDiscovery;

    public string Token { get; } = InstanceFile.MintToken();
    public int Port { get; private set; }
    public string? InstanceFilePath { get; private set; }
    public int ConnectionCount => _sessions.Count;

    public BridgeHost(Guid instanceId, string rhinoVersion, string bridgeVersion, IMainThread mainThread, IDocumentSnapshotSource documents, IRunHost runHost, IRunLauncher launcher, IViewCapture viewCapture, IPythonHost pythonHost, IWindowInventory? windowInventory, string instancesDir, Action<string> log)
    {
        var runner = new RoslynScriptRunner();
        var executor = new UndoRunExecutor(new ScriptRunners(runner, new PythonScriptRunner(pythonHost)), runHost);
        // API discovery (PRD §09): reflect RhinoCommon + loaded plug-ins into the persistent cache. A
        // one-time ~1.5s cost on the first launch; later launches sync only deltas. Never fails the bridge.
        _rhinoVersionForDiscovery = rhinoVersion;
        _dispatcher = new RequestDispatcher(ExecutionManager.CreateDefault(ExecutionRingBuffer.CreateDefault()), executor, launcher, log,
            capture: new ViewCaptureService(viewCapture), onMainThread: f => mainThread.Invoke(f), windowInventory: windowInventory);
        // The undo tool's gate (PRD §07): every document change outside the connector's own work.
        _changeMonitor = runHost.MonitorChanges(executor.Clock.NoteChange);
        // list_instances' last_run per document (PRD §05): re-send register when the ledger changes.
        _dispatcher.LedgerChanged += PushRegisterRefresh;
        _warmup = new TransactionlessWarmup(runner, pythonHost, log);
        _instanceId = instanceId;
        _rhinoVersion = rhinoVersion;
        _bridgeVersion = bridgeVersion;
        _mainThread = mainThread;
        _documents = documents;
        _instancesDir = instancesDir;
        _log = log;
        _snapshot = new RegisterSnapshot(instanceId, Environment.ProcessId, rhinoVersion, AppDataPaths.PlatformName(), bridgeVersion, Array.Empty<RegisteredDocument>());
    }

    /// <summary>Must be called on the main thread (OnLoad is).</summary>
    public void Start()
    {
        RebuildSnapshot();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _startedAt = DateTimeOffset.UtcNow;
        WriteInstanceFile();
        _log($"listening on 127.0.0.1:{Port}; instance file {InstanceFilePath}");

        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MCPBridge accept" };
        _acceptThread.Start();
        _pingTimer = new Timer(_ => _ = PingAsync(), null, PingInterval, PingInterval);
        _tickTimer = new Timer(_ => { try { _dispatcher.Tick(); } catch (Exception ex) { _log("tick failed: " + ex.Message); } }, null, RequestDispatcher.TickInterval, RequestDispatcher.TickInterval);
        // Warm the Roslyn pipeline off the main thread so the first script's compile is not on the
        // response path (Revit #67/#136). Scripts arriving before it finishes take the slow path.
        _warmup.Start();
        // API discovery (PRD §09): the ~1.5s cold cache build is pure reflection with no main-thread
        // requirement, so build it off the main thread too — the listener is already up (review of #294,
        // M1). Discovery calls in the meantime answer discovery-unavailable rather than blocking OnLoad.
        new Thread(() =>
        {
            var (service, cache) = DiscoveryBootstrap.Create(_rhinoVersionForDiscovery, _log);
            _discoveryCache = cache;
            if (service is not null)
            {
                _dispatcher.SetDiscoveryService(service);
            }

            // rhinoscript (PRD §09): its Python source only exists once Rhino's CPython runtime has been
            // deployed (during the #287 warm-up), which races this build on a fresh machine. Retry a few
            // times so it lands without a Rhino restart; RhinoCommon discovery is already live regardless.
            if (cache is not null)
            {
                for (var attempt = 0; attempt < 7; attempt++)
                {
                    if (DiscoveryBootstrap.SyncRhinoScript(cache, _log))
                    {
                        break;
                    }

                    System.Threading.Thread.Sleep(5000);
                }

                // grasshopper catalog (PRD §09): unlike rhinoscript, Grasshopper loads only when the user opens
                // it (its ComponentServer is empty until then), so there is no bounded warm-up window. Retry on
                // a slow cadence until it loads, so the catalog appears whenever GH is opened without a restart;
                // the loop ends on success, a genuine index error, or bridge shutdown.
                while (!_stop.IsCancellationRequested)
                {
                    if (DiscoveryBootstrap.SyncGrasshopper(cache, _log))
                    {
                        break;
                    }

                    _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                }
            }
        }) { IsBackground = true, Name = "MCPBridge discovery build" }.Start();
    }

    public void Stop()
    {
        _stop.Cancel();
        _pingTimer?.Dispose();
        _tickTimer?.Dispose();
        try { _listener?.Stop(); } catch { }
        try { _changeMonitor?.Dispose(); } catch { }
        try { _discoveryCache?.Dispose(); } catch { }
        // Close every live socket now rather than waiting for each session's read to notice the
        // cancellation: a plug-in unload must leave no server holding a half-open connection.
        foreach (var s in _sessions.Snapshot())
        {
            try { s.Close(); } catch { }
        }

        if (!InstanceFile.TryDelete(_instancesDir, Environment.ProcessId))
        {
            _log("could not delete the instance file; the server's liveness check will reclaim it");
        }
    }

    /// <summary>Called on the main thread by the document-event subscriptions: rebuild the cached
    /// snapshot and re-send `register` everywhere.</summary>
    public void PushRegisterRefresh()
    {
        try { RebuildSnapshot(); }
        catch (Exception ex) { _log("register refresh skipped: " + ex.Message); return; }
        _ = _sessions.BroadcastAsync(RegisterMessage.ToJson(Snapshot()), _stop.Token);
    }

    /// <summary>Main thread only. Reads the documents directly when called on the main thread and
    /// marshals (with the adapter's bounded wait) otherwise, so a caller on the wrong thread degrades to
    /// a timeout rather than a corrupting off-thread RhinoCommon call.</summary>
    private void RebuildSnapshot()
    {
        var snap = _mainThread.Invoke(() => _documents.Snapshot());
        var docs = snap.Documents;
        // A document that is gone takes its ledger and clock evidence with it (review of #285).
        var open = new HashSet<string>(docs.Select(d => d.DocumentId));
        foreach (var gone in _snapshot.Documents.Select(d => d.DocumentId).Where(id => !open.Contains(id)))
        {
            _dispatcher.ForgetDocument(gone);
        }

        // PRD §05: each document carries the connector's last completed run on it.
        docs = docs.Select(d => d.WithLastRun(_dispatcher.Ledger.Get(d.DocumentId))).ToList();
        _snapshot = new RegisterSnapshot(_instanceId, Environment.ProcessId, _rhinoVersion, AppDataPaths.PlatformName(), _bridgeVersion, docs, _dispatcher.ExecutionState, snap.GrasshopperDocuments);
    }

    private void AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = _listener!.AcceptTcpClient(); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (Exception ex) { _log("accept failed: " + ex.Message); continue; }
            client.NoDelay = true;
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var session = new ConnectionSession(client.GetStream(), this);
        _sessions.Add(session);
        try
        {
            await session.RunAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log("session ended with error: " + ex.Message); }
        finally
        {
            _sessions.Remove(session);
            try { client.Close(); } catch { }
        }
    }

    private async Task PingAsync()
    {
        // Non-overlapping: a broadcast slowed by a peer must not stack a second one behind it.
        if (Interlocked.Exchange(ref _pingInFlight, 1) == 1) return;
        try
        {
            // Self-heal: a server that misjudged this process as dead deletes the instance file; without
            // this, the Rhino would be invisible to every server until its next start (review of #281).
            if (InstanceFilePath is not null && !InstanceFile.Exists(_instancesDir, Environment.ProcessId))
            {
                _log("instance file was missing; re-writing it");
                WriteInstanceFile();
            }

            await _sessions.BroadcastAsync(PingMessage.ToJson(MemorySnapshot.Capture(), _dispatcher.ExecutionState), _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log("ping failed: " + ex.Message); }
        finally
        {
            Interlocked.Exchange(ref _pingInFlight, 0);
        }
    }

    private void WriteInstanceFile()
    {
        InstanceFilePath = new InstanceFile
        {
            InstanceId = _instanceId.ToString(),
            Pid = Environment.ProcessId,
            Port = Port,
            Token = Token,
            RhinoVersion = _rhinoVersion,
            Platform = AppDataPaths.PlatformName(),
            BridgeVersion = _bridgeVersion,
            StartedAt = _startedAt,
        }.Write(_instancesDir);
    }

    // ISessionEnvironment

    /// <summary>The cache; never blocks (see the class doc).</summary>
    /// <summary>The cached documents with the execution state as it is NOW -- a snapshot built during
    /// a run would otherwise freeze "busy" until the next document event (seen live).</summary>
    public RegisterSnapshot Snapshot()
    {
        var s = _snapshot;
        return new RegisterSnapshot(s.InstanceId, s.Pid, s.RhinoVersion, s.Platform, s.BridgeVersion, s.Documents, _dispatcher.ExecutionState, s.GrasshopperDocuments);
    }

    public Task<string> DispatchAsync(JsonRpcRequest request, CancellationToken cancellationToken) =>
        _dispatcher.DispatchAsync(request, cancellationToken);

    public void Log(string message) => _log(message);
}
