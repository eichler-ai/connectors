using System.Text;
using System.Text.Json;
using Rhino.MCPBridge.Core.Diagnostics;
using Rhino.MCPBridge.Core.Protocol;

namespace Rhino.MCPBridge.Core.Connection;

/// <summary>
/// One accepted server connection, from auth to EOF (PRD §05, §13). The listener hands it a duplex
/// stream; it (1) reads the first line within <see cref="AuthTimeout"/> and runs
/// <see cref="AuthHandshake"/>, closing on rejection; (2) writes `register`; (3) loops reading NDJSON
/// lines, dispatching each request through the environment and writing the response. Every write —
/// responses, the register refreshes and pings the host pushes through <see cref="SendAsync"/> — goes
/// through one lock so lines never interleave. Requests are dispatched sequentially per connection;
/// the executor behind the dispatcher owns cross-connection serialisation (PRD §06).
/// </summary>
internal sealed class ConnectionSession
{
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A write that does not complete in this long means the peer has stopped reading (its
    /// socket buffer is full); the session is torn down rather than queueing writes without bound
    /// (review of #281). Generous: a healthy server drains a line in microseconds.</summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    private readonly Stream _stream;
    private readonly ISessionEnvironment _env;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly NdjsonLineBuffer _lines = new();

    private volatile bool _authenticated;

    public string? ServerId { get; private set; }
    public string? ServerVersion { get; private set; }

    /// <summary>Read by the host's broadcast from other threads; written once, on the session's own task.</summary>
    public bool Authenticated => _authenticated;

    public ConnectionSession(Stream stream, ISessionEnvironment env)
    {
        _stream = stream;
        _env = env;
    }

    /// <summary>Runs to EOF, cancellation, or auth rejection. Never throws for a peer's misbehaviour;
    /// the reason is logged. Throws only on cancellation.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var pending = new Queue<string>();

        // 1. Auth, bounded: a peer that connects and says nothing must not hold a slot forever.
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        authCts.CancelAfter(AuthTimeout);
        string? first;
        try
        {
            first = await ReadLineAsync(buffer, pending, authCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _env.Log("connection closed: no auth line within " + AuthTimeout.TotalSeconds + "s");
            return;
        }

        if (first is null)
        {
            _env.Log("connection closed before sending auth");
            return;
        }

        var verdict = AuthHandshake.Evaluate(first, _env.Token);
        await SendAsync(verdict.ResponseJson, cancellationToken).ConfigureAwait(false);
        if (!verdict.Accepted)
        {
            _env.Log("auth rejected: " + verdict.RejectionCode);
            return;
        }

        ServerId = verdict.ServerId;
        ServerVersion = verdict.ServerVersion;
        _authenticated = true;
        _env.Log($"auth ok: server {ServerId ?? "(unnamed)"} {ServerVersion ?? ""}".TrimEnd());

        // 2. Register, immediately — the server's registry is empty until this arrives.
        await SendAsync(RegisterMessage.ToJson(_env.Snapshot()), cancellationToken).ConfigureAwait(false);

        // 3. Serve.
        while (true)
        {
            var line = await ReadLineAsync(buffer, pending, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                _env.Log("connection closed by peer");
                return;
            }

            string response;
            try
            {
                var request = JsonRpcRequest.Parse(line);
                response = await _env.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonRpcParamException ex)
            {
                response = JsonRpcErrorMessage.ToJson(JsonSerializer.SerializeToElement((object?)null), JsonRpcErrorCode.InvalidRequest, ex.Message, ex.Diagnostic);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A dispatcher bug must not kill the connection: PRD §01, wrap don't replace.
                var record = DiagnosticRecord.Create(DiagnosticSeverity.Error, "dispatch-failed", DiagnosticSource.Connection,
                    $"the request could not be dispatched: {ex.GetType().Name}: {ex.Message}", detail: null, remedy: null);
                response = JsonRpcErrorMessage.ToJson(JsonSerializer.SerializeToElement((object?)null), JsonRpcErrorCode.InternalError, ex.Message, record);
            }

            await SendAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Closes the underlying stream, which ends <see cref="RunAsync"/>. Idempotent.</summary>
    public void Close()
    {
        try { _stream.Dispose(); } catch { }
    }

    /// <summary>Writes one JSON line. Safe from any thread; used by the host for register refreshes and
    /// pings. Bounded by <see cref="WriteTimeout"/> including the wait for the write lock: on timeout the
    /// stream is closed, which ends <see cref="RunAsync"/> and removes the session, and a
    /// <see cref="TimeoutException"/> is thrown to the caller.</summary>
    public async Task SendAsync(string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(NdjsonLineBuffer.Encode(json));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WriteTimeout);
        try
        {
            await _writeLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _env.Log($"write timed out after {WriteTimeout.TotalSeconds}s; the server {ServerId ?? "(unnamed)"} stopped reading -- closing the connection");
            try { _stream.Dispose(); } catch { }
            throw new TimeoutException("the peer stopped reading");
        }
    }

    private async Task<string?> ReadLineAsync(byte[] buffer, Queue<string> pending, CancellationToken cancellationToken)
    {
        while (pending.Count == 0)
        {
            int n;
            try
            {
                n = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // A reset socket, or our own Close()/write-timeout teardown: the connection is gone.
                // Reported as EOF so the caller logs "closed" and returns, instead of faulting the task.
                return null;
            }

            if (n == 0)
            {
                return null;
            }

            foreach (var line in _lines.Append(buffer.AsSpan(0, n)))
            {
                pending.Enqueue(line);
            }
        }

        return pending.Dequeue();
    }
}
