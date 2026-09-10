using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Rhino.MCPBridge.Core.Connection;
using Rhino.MCPBridge.Core.Diagnostics;
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
/// </summary>
internal sealed class BridgeHost : ISessionEnvironment
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    private readonly Guid _instanceId;
    private readonly string _rhinoVersion;
    private readonly string _bridgeVersion;
    private readonly IMainThread _mainThread;
    private readonly IDocumentSnapshotSource _documents;
    private readonly string _instancesDir;
    private readonly Action<string> _log;
    private readonly SessionSet _sessions = new();
    private readonly CancellationTokenSource _stop = new();

    private TcpListener? _listener;
    private Thread? _acceptThread;
    private Timer? _pingTimer;

    public string Token { get; } = InstanceFile.MintToken();
    public int Port { get; private set; }
    public string? InstanceFilePath { get; private set; }
    public int ConnectionCount => _sessions.Count;

    public BridgeHost(Guid instanceId, string rhinoVersion, string bridgeVersion, IMainThread mainThread, IDocumentSnapshotSource documents, string instancesDir, Action<string> log)
    {
        _instanceId = instanceId;
        _rhinoVersion = rhinoVersion;
        _bridgeVersion = bridgeVersion;
        _mainThread = mainThread;
        _documents = documents;
        _instancesDir = instancesDir;
        _log = log;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        InstanceFilePath = new InstanceFile
        {
            InstanceId = _instanceId.ToString(),
            Pid = Environment.ProcessId,
            Port = Port,
            Token = Token,
            RhinoVersion = _rhinoVersion,
            Platform = AppDataPaths.PlatformName(),
            BridgeVersion = _bridgeVersion,
            StartedAt = DateTimeOffset.UtcNow,
        }.Write(_instancesDir);
        _log($"listening on 127.0.0.1:{Port}; instance file {InstanceFilePath}");

        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MCPBridge accept" };
        _acceptThread.Start();
        _pingTimer = new Timer(_ => _ = PingAsync(), null, PingInterval, PingInterval);
    }

    public void Stop()
    {
        _stop.Cancel();
        _pingTimer?.Dispose();
        try { _listener?.Stop(); } catch { }
        if (!InstanceFile.TryDelete(_instancesDir, Environment.ProcessId))
        {
            _log("could not delete the instance file; the server's liveness check will reclaim it");
        }
    }

    /// <summary>Called on the main thread by the document-event subscriptions: re-send `register` everywhere.</summary>
    public void PushRegisterRefresh()
    {
        RegisterSnapshot snapshot;
        try { snapshot = Snapshot(); }
        catch (Exception ex) { _log("register refresh skipped: " + ex.Message); return; }
        _ = _sessions.BroadcastAsync(RegisterMessage.ToJson(snapshot), _stop.Token);
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
        try
        {
            await _sessions.BroadcastAsync(PingMessage.ToJson(MemorySnapshot.Capture()), _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log("ping failed: " + ex.Message); }
    }

    // ISessionEnvironment

    public RegisterSnapshot Snapshot() => _mainThread.Invoke(() => new RegisterSnapshot(
        _instanceId, Environment.ProcessId, _rhinoVersion, AppDataPaths.PlatformName(), _bridgeVersion, _documents.Snapshot()));

    public Task<string> DispatchAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        // Phase 1 PR 1: no methods yet. Every request gets the method-not-found record the Revit
        // dispatcher sends, so a server built ahead of the plug-in sees a legible answer.
        var record = DiagnosticRecord.Create(DiagnosticSeverity.Error, "unknown-method", DiagnosticSource.Connection,
            $"unknown method '{request.Method}'", detail: new Dictionary<string, object?> { ["method"] = request.Method, ["supported_methods"] = Array.Empty<string>() },
            remedy: new[] { "This bridge build serves no request methods yet; update the bridge." });
        return Task.FromResult(JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.MethodNotFound, $"unknown method '{request.Method}'", record));
    }

    public void Log(string message) => _log(message);
}
