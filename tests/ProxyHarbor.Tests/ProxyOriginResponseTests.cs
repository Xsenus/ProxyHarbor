using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Проверяет HTTP framing и JSON-контракт контрольного origin.</summary>
public sealed class ProxyOriginResponseTests
{
    [Fact]
    public void ParsesContentLengthResponse()
    {
        const string body = "{\"ip\":\"8.8.8.8\"}";
        var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}";

        Assert.Equal("8.8.8.8", ProxyOriginResponse.ParseExitIp(response));
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
}
