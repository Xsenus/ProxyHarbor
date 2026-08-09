using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Не позволяет публичному API снова выдавать прокси с устаревшей проверкой.</summary>
public sealed class ProxyPublicationTests
{
    [Fact]
    public void HttpsCategoryUsesHttpConnectTransportUri()
    {
        var endpoint = Endpoint("8.8.4.4", ProxyStatus.Alive, DateTimeOffset.UtcNow);
        endpoint.Protocol = ProxyProtocol.Https;

        var dto = ProxyDto.From(endpoint);

        Assert.Equal(ProxyProtocol.Https, dto.Protocol);
        Assert.Equal("http://8.8.4.4:8080", dto.Url);
    }

    [Fact]
    public async Task PublicListAndExportContainOnlyFreshAliveProxies()
    {
        var root = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"publication-{Guid.NewGuid():N}", root)
            .Options;
        var now = DateTimeOffset.UtcNow;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.AddRange(
                Endpoint("8.8.8.8", ProxyStatus.Alive, now.AddMinutes(-1)),
                Endpoint("1.1.1.1", ProxyStatus.Alive, now.AddHours(-2)),
                Endpoint("9.9.9.9", ProxyStatus.Dead, now));
            await seed.SaveChangesAsync();
        }

        var controller = new ProxiesController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));
        var listAction = await controller.Get(null, null, null, 1, 100, CancellationToken.None);
        var page = Assert.IsType<PagedResult<ProxyDto>>(Assert.IsType<OkObjectResult>(listAction.Result).Value);
        Assert.Single(page.Items);
        Assert.Equal("8.8.8.8", page.Items[0].Host);

        await using var output = new AsyncOnlyMemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = output } }
        };
        Assert.IsType<EmptyResult>(await controller.Export("txt", null, null, null, CancellationToken.None));
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("8.8.8.8", text);
        Assert.DoesNotContain("1.1.1.1", text);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task JsonExportStreamsCamelCaseContractWithStringProtocol()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"json-export-{Guid.NewGuid():N}")
            .Options;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.Add(Endpoint("2001:4860:4860::8888", ProxyStatus.Alive, DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }
        var controller = new ProxiesController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));
        await using var output = new AsyncOnlyMemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = output } }
        };

        Assert.IsType<EmptyResult>(await controller.Export(
            "json", ProxyProtocol.Http, null, null, CancellationToken.None));

        using var json = JsonDocument.Parse(output.ToArray());
        var item = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("Http", item.GetProperty("protocol").GetString());
        Assert.Equal("http://[2001:4860:4860::8888]:8080", item.GetProperty("url").GetString());
        Assert.Contains("proxies-http.json", controller.Response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task XmlAndCsvExportsContainTheFullStableContract()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"structured-export-{Guid.NewGuid():N}")
            .Options;
        var checkedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var endpoint = Endpoint("2001:4860:4860::8888", ProxyStatus.Alive, checkedAt);
        endpoint.Protocol = ProxyProtocol.Socks5;
        endpoint.LatencyMs = 321;
        endpoint.SuccessfulChecks = 4;
        endpoint.FailedChecks = 1;
        endpoint.ExitIp = "8.8.8.8";
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.Add(endpoint);
            await seed.SaveChangesAsync();
        }

        var xmlController = Controller(options, out var xmlOutput);
        Assert.IsType<EmptyResult>(await xmlController.Export(
            "xml", ProxyProtocol.Socks5, 500, 80, CancellationToken.None));
        var xml = XDocument.Parse(Encoding.UTF8.GetString(xmlOutput.ToArray()));
        var proxy = Assert.Single(xml.Root!.Elements("proxy"));
        Assert.Equal("Socks5", proxy.Element("protocol")?.Value);
        Assert.Equal("2001:4860:4860::8888", proxy.Element("host")?.Value);
        Assert.Equal("8080", proxy.Element("port")?.Value);
        Assert.Equal("321", proxy.Element("latencyMs")?.Value);
        Assert.Equal("80", proxy.Element("successRate")?.Value);
        Assert.Equal(checkedAt.ToString("O"), proxy.Element("lastCheckedAt")?.Value);
        Assert.Equal("socks5://[2001:4860:4860::8888]:8080", proxy.Element("url")?.Value);
        Assert.Equal("8.8.8.8", proxy.Element("exitIp")?.Value);

        var csvController = Controller(options, out var csvOutput);
        Assert.IsType<EmptyResult>(await csvController.Export(
            "csv", ProxyProtocol.Socks5, 500, 80, CancellationToken.None));
        var csvLines = Encoding.UTF8.GetString(csvOutput.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("protocol,host,port,latencyMs,successRate,lastCheckedAt,url,exitIp", csvLines[0].TrimEnd('\r'));
        Assert.Equal(
            $"\"Socks5\",\"2001:4860:4860::8888\",8080,321,80,\"{checkedAt:O}\",\"socks5://[2001:4860:4860::8888]:8080\",\"8.8.8.8\"",
            csvLines[1].TrimEnd('\r'));
    }

    [Fact]
    public async Task CsvExportNeutralizesSpreadsheetFormulaPrefixes()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"csv-safety-{Guid.NewGuid():N}")
            .Options;
        var endpoint = Endpoint("=2+3", ProxyStatus.Alive, DateTimeOffset.UtcNow);
        endpoint.ExitIp = "@malicious";
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.Add(endpoint);
            await seed.SaveChangesAsync();
        }

        var controller = Controller(options, out var output);
        Assert.IsType<EmptyResult>(await controller.Export("csv", null, null, null, CancellationToken.None));
        var csv = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("\"'=2+3\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"'@malicious\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicListAndTextExportApplyTheSameQualityFilters()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"filter-parity-{Guid.NewGuid():N}")
            .Options;
        var now = DateTimeOffset.UtcNow;
        var accepted = Endpoint("8.8.8.8", ProxyStatus.Alive, now);
        accepted.LatencyMs = 200;
        accepted.SuccessfulChecks = 9;
        accepted.FailedChecks = 1;
        var slow = Endpoint("1.1.1.1", ProxyStatus.Alive, now);
        slow.LatencyMs = 900;
        slow.SuccessfulChecks = 9;
        slow.FailedChecks = 1;
        var unreliable = Endpoint("9.9.9.9", ProxyStatus.Alive, now);
        unreliable.LatencyMs = 100;
        unreliable.SuccessfulChecks = 1;
        unreliable.FailedChecks = 1;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.AddRange(accepted, slow, unreliable);
            await seed.SaveChangesAsync();
        }

        var listController = new ProxiesController(
            new TestDbFactory(options), Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));
        var listAction = await listController.Get(ProxyProtocol.Http, 500, 80, 1, 100, CancellationToken.None);
        var page = Assert.IsType<PagedResult<ProxyDto>>(Assert.IsType<OkObjectResult>(listAction.Result).Value);
        Assert.Equal("8.8.8.8", Assert.Single(page.Items).Host);

        var exportController = Controller(options, out var output);
        Assert.IsType<EmptyResult>(await exportController.Export(
            "txt", ProxyProtocol.Http, 500, 80, CancellationToken.None));
        Assert.Equal("http://8.8.8.8:8080", Encoding.UTF8.GetString(output.ToArray()).Trim());
    }

    [Fact]
    public async Task ExportContinuationMakesTheHardResponseLimitExplicitAndRetrievable()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"export-continuation-{Guid.NewGuid():N}")
            .Options;
        var now = DateTimeOffset.UtcNow;
        var fastest = Endpoint("1.1.1.1", ProxyStatus.Alive, now);
        fastest.LatencyMs = 100;
        var middle = Endpoint("8.8.8.8", ProxyStatus.Alive, now);
        middle.LatencyMs = 200;
        var slowest = Endpoint("9.9.9.9", ProxyStatus.Alive, now);
        slowest.LatencyMs = 300;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.AddRange(slowest, fastest, middle);
            await seed.SaveChangesAsync();
        }

        var firstController = Controller(options, out var firstOutput);
        Assert.IsType<EmptyResult>(await firstController.Export(
            "txt", null, null, null, CancellationToken.None, limit: 2, offset: 0));
        var firstPage = Encoding.UTF8.GetString(firstOutput.ToArray())
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["http://1.1.1.1:8080", "http://8.8.8.8:8080"], firstPage);
        Assert.Equal("2", firstController.Response.Headers["X-Export-Limit"]);
        Assert.Equal("0", firstController.Response.Headers["X-Export-Offset"]);
        Assert.Equal("true", firstController.Response.Headers["X-Export-Truncated"]);
        Assert.Equal("2", firstController.Response.Headers["X-Next-Offset"]);

        var secondController = Controller(options, out var secondOutput);
        Assert.IsType<EmptyResult>(await secondController.Export(
            "txt", null, null, null, CancellationToken.None, limit: 2, offset: 2));
        Assert.Equal("http://9.9.9.9:8080", Encoding.UTF8.GetString(secondOutput.ToArray()).Trim());
        Assert.Equal("false", secondController.Response.Headers["X-Export-Truncated"]);
        Assert.False(secondController.Response.Headers.ContainsKey("X-Next-Offset"));
        Assert.Contains("proxies-all-offset-2.txt",
            secondController.Response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);

        var terminalController = Controller(options, out var terminalOutput);
        Assert.IsType<EmptyResult>(await terminalController.Export(
            "txt", null, null, null, CancellationToken.None, limit: 50_000, offset: int.MaxValue));
        Assert.Empty(terminalOutput.ToArray());
        Assert.Equal("false", terminalController.Response.Headers["X-Export-Truncated"]);
        Assert.False(terminalController.Response.Headers.ContainsKey("X-Next-Offset"));
    }

    [Fact]
    public async Task ListAndExportUseTheSameDeterministicTieBreakerAcrossPages()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"stable-pagination-{Guid.NewGuid():N}")
            .Options;
        var now = DateTimeOffset.UtcNow;
        var first = Endpoint("1.1.1.1", ProxyStatus.Alive, now);
        first.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        first.LatencyMs = 100;
        first.SuccessfulChecks = 5;
        var second = Endpoint("8.8.8.8", ProxyStatus.Alive, now);
        second.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        second.LatencyMs = 100;
        second.SuccessfulChecks = 5;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            // Обратный insertion order не должен влиять на публичный контракт.
            seed.Proxies.AddRange(second, first);
            await seed.SaveChangesAsync();
        }

        var listController = new ProxiesController(
            new TestDbFactory(options), Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));
        var firstAction = await listController.Get(
            null, null, null, page: 1, pageSize: 1, cancellationToken: CancellationToken.None);
        var firstPage = Assert.IsType<PagedResult<ProxyDto>>(
            Assert.IsType<OkObjectResult>(firstAction.Result).Value);
        var secondAction = await listController.Get(
            null, null, null, page: 2, pageSize: 1, cancellationToken: CancellationToken.None);
        var secondPage = Assert.IsType<PagedResult<ProxyDto>>(
            Assert.IsType<OkObjectResult>(secondAction.Result).Value);
        Assert.Equal("1.1.1.1", Assert.Single(firstPage.Items).Host);
        Assert.Equal("8.8.8.8", Assert.Single(secondPage.Items).Host);

        var exportController = Controller(options, out var output);
        Assert.IsType<EmptyResult>(await exportController.Export(
            "txt", null, null, null, CancellationToken.None, limit: 1, offset: 1));
        Assert.Equal("http://8.8.8.8:8080", Encoding.UTF8.GetString(output.ToArray()).Trim());
    }

    [Fact]
    public async Task SeekListTraversesTiesWithoutDuplicatesOrExactCount()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"seek-pagination-{Guid.NewGuid():N}")
            .Options;
        var now = DateTimeOffset.UtcNow;
        var first = Endpoint("1.1.1.1", ProxyStatus.Alive, now);
        first.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        first.LatencyMs = 100;
        first.SuccessfulChecks = 5;
        var second = Endpoint("8.8.8.8", ProxyStatus.Alive, now);
        second.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        second.LatencyMs = 100;
        second.SuccessfulChecks = 5;
        var third = Endpoint("9.9.9.9", ProxyStatus.Alive, now);
        third.Id = Guid.Parse("00000000-0000-0000-0000-000000000003");
        third.LatencyMs = 200;
        third.SuccessfulChecks = 9;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.AddRange(third, second, first);
            await seed.SaveChangesAsync();
        }

        var controller = new ProxiesController(
            new TestDbFactory(options), Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));
        var firstAction = await controller.Seek(
            null, null, null, after: null, pageSize: 2, cancellationToken: CancellationToken.None);
        var firstPage = Assert.IsType<CursorPagedResult<ProxyDto>>(
            Assert.IsType<OkObjectResult>(firstAction.Result).Value);
        Assert.Equal(["1.1.1.1", "8.8.8.8"], firstPage.Items.Select(x => x.Host));
        Assert.True(firstPage.HasMore);
        Assert.NotNull(firstPage.NextCursor);

        var secondAction = await controller.Seek(
            null, null, null, firstPage.NextCursor, pageSize: 2, CancellationToken.None);
        var secondPage = Assert.IsType<CursorPagedResult<ProxyDto>>(
            Assert.IsType<OkObjectResult>(secondAction.Result).Value);
        Assert.Equal("9.9.9.9", Assert.Single(secondPage.Items).Host);
        Assert.False(secondPage.HasMore);
        Assert.Null(secondPage.NextCursor);
    }

    [Fact]
    public async Task SeekCursorIsRejectedWhenDamagedOrFiltersChange()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"seek-filter-{Guid.NewGuid():N}")
            .Options;
        var endpoint = Endpoint("1.1.1.1", ProxyStatus.Alive, DateTimeOffset.UtcNow);
        endpoint.LatencyMs = 100;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.Add(endpoint);
            await seed.SaveChangesAsync();
        }
        var controller = new ProxiesController(
            new TestDbFactory(options), Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));

        var malformed = await controller.Seek(
            null, null, null, "not-a-cursor", 1, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(malformed.Result);

        var fingerprint = PublicationCursor.FilterFingerprint(ProxyProtocol.Http, 500, null);
        var cursor = PublicationCursor.Encode(
            new PublicationPosition(100, 1, endpoint.Id), fingerprint);
        var changedFilters = await controller.Seek(
            ProxyProtocol.Http, 501, null, cursor, 1, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(changedFilters.Result);
    }

    [Fact]
    public async Task SeekExportReturnsOpaqueContinuationHeaders()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"seek-export-{Guid.NewGuid():N}")
            .Options;
        var now = DateTimeOffset.UtcNow;
        var first = Endpoint("1.1.1.1", ProxyStatus.Alive, now);
        first.LatencyMs = 100;
        var second = Endpoint("8.8.8.8", ProxyStatus.Alive, now);
        second.LatencyMs = 200;
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Proxies.AddRange(second, first);
            await seed.SaveChangesAsync();
        }

        var firstController = Controller(options, out var firstOutput);
        Assert.IsType<EmptyResult>(await firstController.ExportSeek(
            "txt", null, null, null, CancellationToken.None, limit: 1));
        Assert.Equal("http://1.1.1.1:8080", Encoding.UTF8.GetString(firstOutput.ToArray()).Trim());
        Assert.Equal("start", firstController.Response.Headers["X-Export-Cursor"]);
        Assert.Equal("true", firstController.Response.Headers["X-Export-Truncated"]);
        var cursor = firstController.Response.Headers["X-Next-Cursor"].ToString();
        Assert.Equal(PublicationCursor.EncodedLength, cursor.Length);

        var secondController = Controller(options, out var secondOutput);
        Assert.IsType<EmptyResult>(await secondController.ExportSeek(
            "txt", null, null, null, CancellationToken.None, limit: 1, after: cursor));
        Assert.Equal("http://8.8.8.8:8080", Encoding.UTF8.GetString(secondOutput.ToArray()).Trim());
        Assert.Equal(cursor, secondController.Response.Headers["X-Export-Cursor"]);
        Assert.Equal("false", secondController.Response.Headers["X-Export-Truncated"]);
        Assert.False(secondController.Response.Headers.ContainsKey("X-Next-Cursor"));
    }

    [Fact]
    public async Task ExtremePageNumberIsRejectedBeforeDatabaseOffsetOverflows()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"pagination-{Guid.NewGuid():N}")
            .Options;
        var controller = new ProxiesController(
            new TestDbFactory(options),
            Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));

        var action = await controller.Get(null, null, null, int.MaxValue, 1000, CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<BadRequestObjectResult>(action.Result).Value);
        Assert.Equal(400, problem.Status);
    }

    private static ProxyEndpoint Endpoint(string host, ProxyStatus status, DateTimeOffset checkedAt) => new()
    {
        Host = host,
        Port = 8080,
        Protocol = ProxyProtocol.Http,
        Status = status,
        LastCheckedAt = checkedAt,
        NextCheckAt = checkedAt.AddMinutes(5),
        SuccessfulChecks = status == ProxyStatus.Alive ? 1 : 0,
        FailedChecks = status == ProxyStatus.Dead ? 1 : 0
    };

    private static ProxiesController Controller(
        DbContextOptions<ProxyHarborDbContext> options,
        out AsyncOnlyMemoryStream output)
    {
        var controller = new ProxiesController(
            new TestDbFactory(options), Options.Create(new CollectorOptions { PublicFreshnessMinutes = 15 }));
        output = new AsyncOnlyMemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = output } }
        };
        return controller;
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    /// <summary>Имитирует Kestrel с AllowSynchronousIO=false.</summary>
    private sealed class AsyncOnlyMemoryStream : MemoryStream
    {
        public override void Flush() => throw new InvalidOperationException("Synchronous flush is forbidden.");
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous write is forbidden.");
        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new InvalidOperationException("Synchronous write is forbidden.");
        public override void WriteByte(byte value) =>
            throw new InvalidOperationException("Synchronous write is forbidden.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            base.Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = buffer.ToArray();
            base.Write(copy, 0, copy.Length);
            return ValueTask.CompletedTask;
        }
    }
}
