using System.Text;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Детерминированно проверяет wire-format и строгую валидацию proxy-handshake.</summary>
public sealed class ProxyTunnelProtocolTests
{
    [Fact]
    public async Task HttpConnectWritesAuthorityAndAcceptsSuccessfulStatusLine()
    {
        await using var stream = new ScriptedDuplexStream(
            Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\nProxy-Agent: test\r\n\r\n"));

        await ProxyTunnelProtocol.EstablishHttpConnectAsync(
            stream, "example.com", 443, CancellationToken.None);

        Assert.Equal(
            "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\nProxy-Connection: keep-alive\r\n\r\n",
            Encoding.ASCII.GetString(stream.Written));
    }

    [Fact]
    public async Task HttpConnectDoesNotAcceptFake200InsideHeader()
    {
        await using var stream = new ScriptedDuplexStream(
            Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nX-Fake: 200 OK\r\n\r\n"));

        await Assert.ThrowsAsync<IOException>(() => ProxyTunnelProtocol.EstablishHttpConnectAsync(
            stream, "example.com", 443, CancellationToken.None));
    }

    [Fact]
    public async Task Socks4aWritesDnsTargetAndValidatesReplyVersion()
    {
        await using var stream = new ScriptedDuplexStream([0, 90, 0, 0, 0, 0, 0, 0]);

        await ProxyTunnelProtocol.EstablishSocks4aAsync(
            stream, "example.com", 443, CancellationToken.None);

        var expected = new byte[] { 4, 1, 1, 187, 0, 0, 0, 1, 0 }
            .Concat(Encoding.ASCII.GetBytes("example.com")).Append((byte)0).ToArray();
        Assert.Equal(expected, stream.Written);
    }

    [Fact]
    public async Task Socks5WritesNoAuthDnsConnectAndConsumesBoundAddress()
    {
        await using var stream = new ScriptedDuplexStream([
            5, 0, // Выбран метод без авторизации.
            5, 0, 0, 1, 127, 0, 0, 1, 0x1f, 0x90 // Успешный CONNECT и IPv4 bind endpoint.
        ]);

        await ProxyTunnelProtocol.EstablishSocks5Async(
            stream, "example.com", 443, CancellationToken.None);

        var expectedConnect = new byte[] { 5, 1, 0, 3, 11 }
            .Concat(Encoding.ASCII.GetBytes("example.com")).Concat(new byte[] { 1, 187 }).ToArray();
        Assert.Equal(new byte[] { 5, 1, 0 }.Concat(expectedConnect), stream.Written);
    }

    [Fact]
    public async Task Socks5RejectsReplyWithWrongProtocolVersion()
    {
        await using var stream = new ScriptedDuplexStream([5, 0, 4, 0, 0, 1]);

        await Assert.ThrowsAsync<IOException>(() => ProxyTunnelProtocol.EstablishSocks5Async(
            stream, "example.com", 443, CancellationToken.None));
    }

    /// <summary>Отдаёт заранее заданные байты сервера и отдельно записывает запрос клиента.</summary>
    private sealed class ScriptedDuplexStream(byte[] response) : Stream
    {
        private readonly MemoryStream _read = new(response, writable: false);
        private readonly MemoryStream _written = new();
        public byte[] Written => _written.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _written.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _read.Dispose(); _written.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
