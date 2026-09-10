using System.Text;
using System.Text.Json;
using Rhino.MCPBridge.Core.Connection;
using Rhino.MCPBridge.Core.Protocol;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Connection;

/// <summary>The per-connection state machine (PRD §05/§13) end to end over an in-memory pipe: auth
/// first, register second, then request/response; the shapes a server's dialer depends on.</summary>
public sealed class ConnectionSessionTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private sealed class Env : ISessionEnvironment
    {
        public string Token { get; init; } = ConnectionSessionTests.Token;
        public List<string> Logs { get; } = new();
        public int SnapshotCalls;
        public Func<JsonRpcRequest, string> Dispatch { get; set; } = r => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = r.Id, result = new { echo = r.Method } });
        public RegisterSnapshot Snapshot() { SnapshotCalls++; return new RegisterSnapshot(Guid.Empty, 1, "8", "macos", "dev", Array.Empty<RegisteredDocument>()); }
        public Task<string> DispatchAsync(JsonRpcRequest request, CancellationToken ct) => Task.FromResult(Dispatch(request));
        public void Log(string message) => Logs.Add(message);
    }

    private sealed class Peer : IDisposable
    {
        private readonly Stream _s;
        private readonly StreamReader _r;
        public Peer(Stream s) { _s = s; _r = new StreamReader(s, Encoding.UTF8); }
        public async Task SendAsync(string line) { await _s.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")); await _s.FlushAsync(); }
        public async Task<JsonElement> ReadAsync()
        {
            var line = await _r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(line);
            return JsonDocument.Parse(line!).RootElement.Clone();
        }
        public void Dispose() => _s.Dispose();
    }

    private static string Auth(string token = Token) => $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"auth\",\"params\":{{\"token\":\"{token}\",\"role\":\"agent-client\",\"server_id\":\"srv\"}}}}";

    [Fact]
    public async Task HappyPath_AuthOk_ThenRegister_ThenDispatch()
    {
        var (pluginSide, serverSide) = DuplexPipeStream.CreatePair();
        var env = new Env();
        var session = new ConnectionSession(pluginSide, env);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = session.RunAsync(cts.Token);
        using var peer = new Peer(serverSide);

        await peer.SendAsync(Auth());
        var ok = await peer.ReadAsync();
        Assert.True(ok.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal(1, ok.GetProperty("id").GetInt32());

        var reg = await peer.ReadAsync();
        Assert.Equal("register", reg.GetProperty("method").GetString());
        Assert.Equal(1, env.SnapshotCalls);
        Assert.True(session.Authenticated);
        Assert.Equal("srv", session.ServerId);

        await peer.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"list_functions\",\"params\":{}}");
        var resp = await peer.ReadAsync();
        Assert.Equal(7, resp.GetProperty("id").GetInt32());
        Assert.Equal("list_functions", resp.GetProperty("result").GetProperty("echo").GetString());

        peer.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(env.Logs, l => l.Contains("closed by peer"));
    }

    [Fact]
    public async Task BadToken_GetsOneErrorLine_AndTheSessionEnds_WithoutRegistering()
    {
        var (pluginSide, serverSide) = DuplexPipeStream.CreatePair();
        var env = new Env();
        var session = new ConnectionSession(pluginSide, env);
        var run = session.RunAsync(CancellationToken.None);
        using var peer = new Peer(serverSide);

        await peer.SendAsync(Auth("nope"));
        var err = await peer.ReadAsync();
        Assert.Equal("auth-invalid-token", err.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.Authenticated);
        Assert.Equal(0, env.SnapshotCalls);
    }

    [Fact]
    public async Task FirstLineNotAuth_IsRejected()
    {
        var (pluginSide, serverSide) = DuplexPipeStream.CreatePair();
        var session = new ConnectionSession(pluginSide, new Env());
        var run = session.RunAsync(CancellationToken.None);
        using var peer = new Peer(serverSide);
        await peer.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"execute_script\",\"params\":{}}");
        var err = await peer.ReadAsync();
        Assert.Equal("auth-required", err.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MalformedRequestAfterAuth_GetsAnErrorLine_AndTheSessionSurvives()
    {
        var (pluginSide, serverSide) = DuplexPipeStream.CreatePair();
        var session = new ConnectionSession(pluginSide, new Env());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = session.RunAsync(cts.Token);
        using var peer = new Peer(serverSide);
        await peer.SendAsync(Auth()); await peer.ReadAsync(); await peer.ReadAsync();

        await peer.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}"); // no id
        var err = await peer.ReadAsync();
        Assert.Equal("malformed-request", err.GetProperty("error").GetProperty("data").GetProperty("code").GetString());

        await peer.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"x\"}");
        Assert.Equal(2, (await peer.ReadAsync()).GetProperty("id").GetInt32());
        Assert.False(run.IsCompleted);
    }

    [Fact]
    public async Task DispatcherThrowing_IsReportedAsInternalError_NotAClosedConnection()
    {
        var (pluginSide, serverSide) = DuplexPipeStream.CreatePair();
        var env = new Env { Dispatch = _ => throw new InvalidOperationException("boom") };
        var session = new ConnectionSession(pluginSide, env);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = session.RunAsync(cts.Token);
        using var peer = new Peer(serverSide);
        await peer.SendAsync(Auth()); await peer.ReadAsync(); await peer.ReadAsync();
        await peer.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"x\"}");
        var err = await peer.ReadAsync();
        Assert.Equal("dispatch-failed", err.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.Contains("boom", err.GetProperty("error").GetProperty("message").GetString());
        Assert.False(run.IsCompleted);
    }

    [Fact]
    public async Task APeerThatStopsReading_IsTornDownByTheWriteTimeout_NotQueuedForever()
    {
        // review of #281: one wedged server must not stall the broadcast to the others or accumulate
        // writes without bound. The pipe pair has a bounded buffer; fill it without reading.
        var (pluginSide, serverSide) = DuplexPipeStream.CreatePair();
        var env = new Env();
        var session = new ConnectionSession(pluginSide, env);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = session.RunAsync(cts.Token);
        using var peer = new Peer(serverSide);
        await peer.SendAsync(Auth()); await peer.ReadAsync(); await peer.ReadAsync();

        var big = new string('x', 256 * 1024);
        var payload = "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"params\":{\"pad\":\"" + big + "\"}}";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Exception? thrown = null;
        // Keep writing until the pipe is full and a send has to wait for the timeout.
        for (int i = 0; i < 64 && thrown is null; i++)
        {
            try { await session.SendAsync(payload, cts.Token); }
            catch (Exception ex) { thrown = ex; }
        }
        Assert.IsType<TimeoutException>(thrown);
        Assert.True(sw.Elapsed < ConnectionSession.WriteTimeout + TimeSpan.FromSeconds(5), "must give up at the write timeout, not later");
        await run.WaitAsync(TimeSpan.FromSeconds(5)); // the stream was closed, so the session ended
        Assert.Contains(env.Logs, l => l.Contains("stopped reading"));
    }

    [Fact]
    public async Task SendAsync_InterleavesWithResponses_LineAtATime()
    {
        var (pluginSide, serverSide) = DuplexPipeStream.CreatePair();
        var session = new ConnectionSession(pluginSide, new Env());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = session.RunAsync(cts.Token);
        using var peer = new Peer(serverSide);
        await peer.SendAsync(Auth()); await peer.ReadAsync(); await peer.ReadAsync();

        var pushes = Enumerable.Range(0, 50).Select(i => session.SendAsync(PingMessage.ToJson(), cts.Token)).ToArray();
        await Task.WhenAll(pushes);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal("ping", (await peer.ReadAsync()).GetProperty("method").GetString());
        }
    }
}
