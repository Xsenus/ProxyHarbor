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
        Assert.Equal(1, stream.ReadCount);
    }

    [Fact]
    public async Task HttpConnectHandlesFragmentedHeaderWithoutPerByteAllocations()
    {
        await using var stream = new ScriptedDuplexStream(
            Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nProxy-Agent: fragmented\r\n\r\n"),
            maxReadSize: 3);

        await ProxyTunnelProtocol.EstablishHttpConnectAsync(
            stream, "example.com", 443, CancellationToken.None);

        Assert.InRange(stream.ReadCount, 2, 32);
    }

    [Theory]
    [InlineData("HTTP/2 200 OK\r\n\r\n")]
    [InlineData("HTTP/X 200 OK\r\n\r\n")]
    [InlineData("HTTP/1.1 +200 OK\r\n\r\n")]
    public async Task HttpConnectRejectsInvalidVersionOrStatusSyntax(string response)
    {
        await using var stream = new ScriptedDuplexStream(Encoding.ASCII.GetBytes(response));

        await Assert.ThrowsAsync<IOException>(() => ProxyTunnelProtocol.EstablishHttpConnectAsync(
            stream, "example.com", 443, CancellationToken.None));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x0A)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    public async Task HttpConnectRejectsInvalidHeaderBytes(int invalidByte)
    {
        byte[] response =
        [
            .. Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nX-Test: before"),
            checked((byte)invalidByte),
            .. Encoding.ASCII.GetBytes("after\r\n\r\n")
        ];
        await using var stream = new ScriptedDuplexStream(response);

        await Assert.ThrowsAsync<IOException>(() => ProxyTunnelProtocol.EstablishHttpConnectAsync(
            stream, "example.com", 443, CancellationToken.None));
    }

    [Fact]
    public async Task HttpConnectRejectsBytesAfterHeaderBoundary()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n\r\n"), 0x00]);

        await Assert.ThrowsAsync<IOException>(() => ProxyTunnelProtocol.EstablishHttpConnectAsync(
            stream, "example.com", 443, CancellationToken.None));
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
    public async Task HttpConnectBracketsIpv6Authority()
    {
        await using var stream = new ScriptedDuplexStream(
            Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"));

        await ProxyTunnelProtocol.EstablishHttpConnectAsync(
            stream, "2606:4700:4700::1111", 443, CancellationToken.None);

        Assert.StartsWith("CONNECT [2606:4700:4700::1111]:443 HTTP/1.1\r\n",
            Encoding.ASCII.GetString(stream.Written), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("example.com\r\nX-Injected: true")]
    [InlineData("")]
    public async Task HttpConnectRejectsUnsafeTarget(string host)
    {
        await using var stream = new ScriptedDuplexStream([]);

        await Assert.ThrowsAsync<IOException>(() => ProxyTunnelProtocol.EstablishHttpConnectAsync(
            stream, host, 443, CancellationToken.None));
        Assert.Empty(stream.Written);
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

    [Fact]
    public async Task Socks5RejectsEmptyBoundDomain()
    {
        await using var stream = new ScriptedDuplexStream([
            5, 0,
            5, 0, 0, 3, 0
        ]);

        await Assert.ThrowsAsync<IOException>(() => ProxyTunnelProtocol.EstablishSocks5Async(
            stream, "example.com", 443, CancellationToken.None));
    }

    /// <summary>Отдаёт заранее заданные байты сервера и отдельно записывает запрос клиента.</summary>
    private sealed class ScriptedDuplexStream(byte[] response, int maxReadSize = int.MaxValue) : Stream
    {
        private readonly MemoryStream _read = new(response, writable: false);
        private readonly MemoryStream _written = new();
        public byte[] Written => _written.ToArray();
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return _read.ReadAsync(buffer[..Math.Min(buffer.Length, maxReadSize)], cancellationToken);
        }
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
