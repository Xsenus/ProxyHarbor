using System.Text;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет HTTP framing и JSON-контракт контрольного origin.</summary>
public sealed class ProxyOriginResponseTests
{
    [Fact]
    public async Task FramedContentLengthResponseDoesNotWaitForConnectionClose()
    {
        const string body = "{\"ip\":\"8.8.8.8\"}";
        var response = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}";
        await using var stream = new ThrowAfterContentStream(Encoding.ASCII.GetBytes(response), maxChunkSize: 7);

        var received = await ProxyOriginResponse.ReadAsync(stream, 64 * 1024, CancellationToken.None);

        Assert.Equal("8.8.8.8", ProxyOriginResponse.ParseExitIp(received));
        Assert.True(stream.ReadCount > 1);
    }

    [Fact]
    public async Task FramedChunkedResponseDoesNotWaitForConnectionClose()
    {
        const string response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n10\r\n{\"ip\":\"1.1.1.1\"}\r\n0\r\n\r\n";
        await using var stream = new ThrowAfterContentStream(Encoding.ASCII.GetBytes(response), maxChunkSize: 3);

        var received = await ProxyOriginResponse.ReadAsync(stream, 64 * 1024, CancellationToken.None);

        Assert.Equal("1.1.1.1", ProxyOriginResponse.ParseExitIp(received));
        Assert.True(stream.ReadCount > 1);
    }

    [Fact]
    public async Task ReaderRejectsAmbiguousOrDeclaredOversizedFraming()
    {
        await using var ambiguous = new ThrowAfterContentStream(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 10\r\nTransfer-Encoding: chunked\r\n\r\n"));
        await Assert.ThrowsAsync<ProbeControlResponseException>(() =>
            ProxyOriginResponse.ReadAsync(ambiguous, 1024, CancellationToken.None));

        await using var oversized = new ThrowAfterContentStream(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 10000\r\n\r\n"));
        await Assert.ThrowsAsync<ProbeControlResponseException>(() =>
            ProxyOriginResponse.ReadAsync(oversized, 1024, CancellationToken.None));
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 20\r\n\r\n{\"ip\":\"8.8.8.8\"}")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n10\r\n{\"ip\":\"1.1.1.1\"}")]
    public async Task ReaderRejectsPrematureEndOfFramedResponse(string response)
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(response));

        await Assert.ThrowsAsync<ProbeControlResponseException>(() =>
            ProxyOriginResponse.ReadAsync(stream, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task ReaderRejectsInvalidUtf8WithoutReplacementCharacters()
    {
        var prefix = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 11\r\n\r\n{\"ip\":\"");
        byte[] response = [.. prefix, 0xC3, 0x28, (byte)'"', (byte)'}'];
        await using var stream = new MemoryStream(response);

        var received = await ProxyOriginResponse.ReadAsync(stream, 1024, CancellationToken.None);

        Assert.Throws<ProbeControlResponseException>(() => ProxyOriginResponse.ParseExitIp(received));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x0A)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    public async Task ReaderRejectsControlOrNonAsciiHeaderBytes(int invalidByte)
    {
        byte[] response =
        [
            .. Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nX-Test: before"),
            checked((byte)invalidByte),
            .. Encoding.ASCII.GetBytes("after\r\nContent-Length: 0\r\n\r\n")
        ];
        await using var stream = new MemoryStream(response);

        await Assert.ThrowsAsync<ProbeControlResponseException>(() =>
            ProxyOriginResponse.ReadAsync(stream, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task ChunkDecoderUsesByteLengthsAndSupportsSplitUtf8CodePoint()
    {
        // UTF-8 `é` намеренно разделён между двумя chunks. HTTP framing оперирует
        // байтами, поэтому декодировать Unicode можно только после dechunking.
        byte[] firstBodyPart = [.. Encoding.UTF8.GetBytes("{\"note\":\""), 0xC3];
        byte[] secondBodyPart = [0xA9, .. Encoding.UTF8.GetBytes("\",\"ip\":\"8.8.4.4\"}")];
        byte[] response =
        [
            .. Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n"),
            .. Encoding.ASCII.GetBytes(firstBodyPart.Length.ToString("X", System.Globalization.CultureInfo.InvariantCulture)),
            (byte)'\r', (byte)'\n',
            .. firstBodyPart,
            (byte)'\r', (byte)'\n',
            .. Encoding.ASCII.GetBytes(secondBodyPart.Length.ToString("X", System.Globalization.CultureInfo.InvariantCulture)),
            (byte)'\r', (byte)'\n',
            .. secondBodyPart,
            (byte)'\r', (byte)'\n',
            (byte)'0', (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n'
        ];
        await using var stream = new ThrowAfterContentStream(response, maxChunkSize: 5);

        var received = await ProxyOriginResponse.ReadAsync(stream, 1024, CancellationToken.None);

        Assert.Equal("8.8.4.4", ProxyOriginResponse.ParseExitIp(received));
    }

    [Fact]
    public void ParsesContentLengthResponse()
    {
        const string body = "{\"ip\":\"8.8.8.8\"}";
        var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}";

        Assert.Equal("8.8.8.8", ProxyOriginResponse.ParseExitIp(response));
    }

    [Fact]
    public void CanonicalizesEquivalentIpv6ExitAddress()
    {
        const string body = "{\"ip\":\"2606:4700:4700:0:0:0:0:1111\"}";
        var response = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}";

        Assert.Equal("2606:4700:4700::1111", ProxyOriginResponse.ParseExitIp(response));
    }

    [Fact]
    public void DecodesChunkedResponseWithExtensions()
    {
        const string response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n8;test=1\r\n{\"ip\":\"8\r\n8\r\n.8.4.4\"}\r\n0\r\n\r\n";

        Assert.Equal("8.8.4.4", ProxyOriginResponse.ParseExitIp(response));
    }

    [Fact]
    public void RejectsNonSuccessStatusAndMalformedJson()
    {
        Assert.Throws<ProbeControlResponseException>(() => ProxyOriginResponse.ParseExitIp(
            "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"));
        Assert.Throws<ProbeControlResponseException>(() => ProxyOriginResponse.ParseExitIp(
            "HTTP/1.1 200 OK\r\nContent-Length: 1\r\n\r\n{"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"8.8.8.8\"")]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("{\"ip\":123}")]
    public void RejectsInvalidJsonShapeAsControlFailure(string body)
    {
        var response = $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";

        Assert.Throws<ProbeControlResponseException>(() => ProxyOriginResponse.ParseExitIp(response));
    }

    [Fact]
    public void RejectsChunkedBodyWithoutFinalTerminator()
    {
        const string response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n";

        Assert.Throws<ProbeControlResponseException>(() => ProxyOriginResponse.ParseExitIp(response));
    }

    /// <summary>Имитирует keep-alive: любая попытка читать после полного response является ошибкой теста.</summary>
    private sealed class ThrowAfterContentStream(byte[] content, int maxChunkSize = int.MaxValue) : Stream
    {
        private int _position;
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Тест использует только async read.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (_position >= content.Length)
                throw new InvalidOperationException("Reader попытался ждать EOF после полного framed response.");
            var count = Math.Min(maxChunkSize, Math.Min(buffer.Length, content.Length - _position));
            content.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
