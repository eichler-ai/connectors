using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using MCPBridge.Core.Connection;
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
    private Timer? _timeoutTimer;

    /// <summary>How often <see cref="_timeoutTimer"/> re-checks max_duration_ms/the cancellation grace period.</summary>
    private static readonly TimeSpan TimeoutCheckInterval = TimeSpan.FromSeconds(1);

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

        // The document-snapshot ExternalEvent has no circular dependency (its handler doesn't wrap
        // anything else), so it's created directly.
        var documentSnapshotHandler = new DocumentSnapshotHandler();
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
        var dispatcher = new RequestDispatcher(_executionManager, scriptBridge, scriptExecutor);

        _stopCts = new CancellationTokenSource();
        var stopToken = _stopCts.Token;

        _workerThread = new Thread(() => RunConnectionLoop(dispatcher, documentSnapshotHandler, documentSnapshotEvent, stopToken))
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
                    // bridge state for the rest of the process's life.
                    if (_executionManager.CheckMaxDuration(now))
                    {
                        scriptBridge.Abandon();
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

        _workerThread?.Join(TimeSpan.FromSeconds(5));
        _workerThread = null;
    }

    private void RunConnectionLoop(RequestDispatcher dispatcher, DocumentSnapshotHandler documentSnapshotHandler, ExternalEvent documentSnapshotEvent, CancellationToken stopToken)
    {
        var discovery = new BrokerDiscovery(_discoveryOptions);
        var reconnectController = new ReconnectLoopController(_backoffPolicy);

        while (!stopToken.IsCancellationRequested)
        {
            var discoveryResult = discovery.TryDiscover();
            if (!discoveryResult.Found || discoveryResult.BrokerJson is null || discoveryResult.Address is null)
            {
                // Not found (or found but unreadable/malformed -- BrokerDiscovery already reports both as
                // "not found"), or a remote-mode fallback address with no token to authenticate with:
                // treat identically as a failed attempt and back off, per PRD §05's single retry loop.
                Backoff(reconnectController, stopToken);
                continue;
            }

            try
            {
                RunOneConnection(discoveryResult.BrokerJson, discoveryResult.Address, dispatcher, documentSnapshotHandler, documentSnapshotEvent, reconnectController, stopToken);
            }
            catch (Exception)
            {
                // Any failure during this connection's lifetime (auth rejected, socket error, broker
                // closed unexpectedly) falls through to backoff-and-retry -- the reconnect loop is
                // indefinite by design (PRD §05), never fatal to this thread.
            }
            finally
            {
                _activeTcpClient = null;
            }

            if (!stopToken.IsCancellationRequested)
            {
                Backoff(reconnectController, stopToken);
            }
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
        // returns (which happens on disconnect, the opposite condition).
        reconnectController.OnConnectSucceeded();

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
