using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Rhino.MCPBridge.Core.Capture;
using Rhino.MCPBridge.Core.Connection;
using Rhino.MCPBridge.Core.Diagnostics;
using Rhino.MCPBridge.Core.Dispatch;
using Rhino.MCPBridge.Core.Execution;
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

    public string Token { get; } = InstanceFile.MintToken();
    public int Port { get; private set; }
    public string? InstanceFilePath { get; private set; }
    public int ConnectionCount => _sessions.Count;

    public BridgeHost(Guid instanceId, string rhinoVersion, string bridgeVersion, IMainThread mainThread, IDocumentSnapshotSource documents, IRunHost runHost, IRunLauncher launcher, IViewCapture viewCapture, string instancesDir, Action<string> log)
    {
        var runner = new RoslynScriptRunner();
        var executor = new UndoRunExecutor(runner, runHost);
        _dispatcher = new RequestDispatcher(ExecutionManager.CreateDefault(ExecutionRingBuffer.CreateDefault()), executor, launcher, log,
            capture: new ViewCaptureService(viewCapture), onMainThread: f => mainThread.Invoke(f));
        _warmup = new TransactionlessWarmup(runner, log);
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
    }

    public void Stop()
    {
        _stop.Cancel();
        _pingTimer?.Dispose();
        _tickTimer?.Dispose();
        try { _listener?.Stop(); } catch { }
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
        _ = _sessions.BroadcastAsync(RegisterMessage.ToJson(_snapshot), _stop.Token);
    }

    /// <summary>Main thread only. Reads the documents directly when called on the main thread and
    /// marshals (with the adapter's bounded wait) otherwise, so a caller on the wrong thread degrades to
    /// a timeout rather than a corrupting off-thread RhinoCommon call.</summary>
    private void RebuildSnapshot()
    {
        var docs = _mainThread.Invoke(() => _documents.Snapshot());
        _snapshot = new RegisterSnapshot(_instanceId, Environment.ProcessId, _rhinoVersion, AppDataPaths.PlatformName(), _bridgeVersion, docs);
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

            await _sessions.BroadcastAsync(PingMessage.ToJson(MemorySnapshot.Capture()), _stop.Token).ConfigureAwait(false);
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
    public RegisterSnapshot Snapshot() => _snapshot;

    public Task<string> DispatchAsync(JsonRpcRequest request, CancellationToken cancellationToken) =>
        _dispatcher.DispatchAsync(request, cancellationToken);

    public void Log(string message) => _log(message);
}
