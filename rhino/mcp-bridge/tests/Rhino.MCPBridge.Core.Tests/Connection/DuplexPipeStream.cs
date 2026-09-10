using System.IO.Pipelines;

namespace Rhino.MCPBridge.Core.Tests.Connection;

/// <summary>An in-memory duplex stream pair: what one side writes, the other reads. Lets the session
/// state machine run exactly as it does over a socket, with no socket.</summary>
internal static class DuplexPipeStream
{
    public static (Stream pluginSide, Stream serverSide) CreatePair()
    {
        var toPlugin = new Pipe();
        var toServer = new Pipe();
        var plugin = new PipeStream(toPlugin.Reader, toServer.Writer);
        var server = new PipeStream(toServer.Reader, toPlugin.Writer);
        return (plugin, server);
    }

    private sealed class PipeStream : Stream
    {
        private readonly PipeReader _reader;
        private readonly Stream _read;
        private readonly Stream _write;
        public PipeStream(PipeReader reader, PipeWriter writer) { _reader = reader; _read = reader.AsStream(); _write = writer.AsStream(); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _write.Flush();
        public override Task FlushAsync(CancellationToken ct) => _write.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _read.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _write.WriteAsync(buffer, ct);
        // Like a socket: disposing ends a pending read on this side (a real NetworkStream throws from
        // the read once the socket is closed; the pipe's stream would otherwise stay pending forever).
        protected override void Dispose(bool disposing) { if (disposing) { _write.Dispose(); _reader.CancelPendingRead(); _read.Dispose(); } base.Dispose(disposing); }
    }
}
