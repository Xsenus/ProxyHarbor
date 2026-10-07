using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ProxyHarbor.Infrastructure;

/// <summary>Durable whole-list API traversal with shared provider pacing and cooldown.</summary>
internal sealed class FreeProxyDbSourceApiFetcher(IDbContextFactory<ProxyHarborDbContext> dbFactory)
{
    internal async Task<SourceApiFetchResult> FetchAsync(SourceApiCaptureOwner owner,
        Func<string, CancellationToken, Task<SourceFetchResult>> fetchPage, CancellationToken token,
        int? maximumNetworkPages = null)
    {
        owner.EnsureSupported();
        var pageBudget = maximumNetworkPages ?? (ProxiwarePublicApi.Supports(owner.Url) || RoundProxiesPublicApi.Supports(owner.Url) ? 20 : 4);
        var store = new SourceApiCaptureStore(dbFactory);
        var checkpoint = await store.LoadAsync(owner, token);
        var capture = checkpoint?.Capture ?? new FreeProxyDbPageCapture();
        if (capture.Inspect(owner.MaximumBytes).Complete)
            return await AssembleAsync(networkObserved: false);
        var gate = new SourceApiOriginGate(dbFactory, owner.Url);
        await using var lease = await gate.TryAcquireAsync(token);
        if (lease is null) throw new SourceApiDeferredException(DateTimeOffset.UtcNow.AddMinutes(1));
        // Re-load under the distributed lease; another worker may have committed before acquisition.
        checkpoint = await store.LoadAsync(owner, token);
        capture = checkpoint?.Capture ?? new FreeProxyDbPageCapture();
        if (capture.Inspect(owner.MaximumBytes).Complete)
            return await AssembleAsync(networkObserved: false);
        var deadline = await gate.ReadDeadlineAsync(token);
        if (deadline > DateTimeOffset.UtcNow) throw new SourceApiDeferredException(deadline.Value);
        var networkObserved = false;
        var advanced = await AdvanceGuardedAsync(pageBudget, async (url, pageToken) =>
            {
                deadline = await gate.ReadDeadlineAsync(pageToken);
                var now = DateTimeOffset.UtcNow;
                if (deadline > now)
                {
                    // Short pacing can finish in this bounded cycle; quota pauses return immediately.
                    if (deadline.Value - now > gate.RequestInterval)
                        throw new SourceApiDeferredException(deadline.Value);
                    await Task.Delay(deadline.Value - now, pageToken);
                }
                await gate.ReserveRequestAsync(DateTimeOffset.UtcNow, pageToken);
                networkObserved = true;
                try { return await fetchPage(url, pageToken); }
                catch (SourceRateLimitException exception)
                {
                    await gate.ExtendDeadlineAsync(exception.RetryNotBefore, pageToken);
                    throw;
                }
            }, async (next, pageToken) =>
            {
                checkpoint = await store.SaveAsync(owner, checkpoint, next, pageToken) ??
                    throw new SourceApiDeferredException(DateTimeOffset.UtcNow.AddMinutes(1));
            });
        if (advanced.Content is null)
            throw new SourceApiDeferredException(DateTimeOffset.UtcNow.AddMinutes(1));
        return await ResultAsync(advanced, networkObserved);

        async Task<SourceApiFetchResult> AssembleAsync(bool networkObserved)
        {
            var result = await AdvanceGuardedAsync(1,
                (_, _) => throw new InvalidOperationException("Completed API capture requested HTTP."),
                (_, _) => throw new InvalidOperationException("Completed API capture changed."));
            return await ResultAsync(result, networkObserved);
        }

        async Task<FreeProxyDbCaptureAdvance> AdvanceGuardedAsync(int budget,
            Func<string, CancellationToken, Task<SourceFetchResult>> request,
            Func<FreeProxyDbPageCapture, CancellationToken, Task> commit)
        {
            try { return await FreeProxyDbPageCapture.AdvanceAsync(capture, owner.Url, owner.MaximumBytes, budget, request, commit, token); }
            catch (InvalidDataException)
            {
                if (checkpoint?.Capture.Inspect(owner.MaximumBytes).Complete == true)
                    await store.DiscardAsync(checkpoint, token);
                throw;
            }
        }

        async Task<SourceApiFetchResult> ResultAsync(FreeProxyDbCaptureAdvance result, bool networkObserved)
        {
            try
            {
                return new(new SourceFetchResult(owner.Vpn ? VpnText(result.Content!) : result.Content, false, null, null),
                    checkpoint ?? throw new InvalidDataException("Completed API capture has no durable checkpoint."),
                    result.ObservedAt ?? throw new InvalidDataException("Completed API capture has no observation time."), networkObserved);
            }
            catch (InvalidDataException)
            {
                if (checkpoint is not null) await store.DiscardAsync(checkpoint, token);
                throw;
            }
        }
    }

    internal static string VpnText(string content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
        var output = new StringBuilder();
        foreach (var row in document.RootElement.GetProperty("data").EnumerateArray())
        {
            if (!row.TryGetProperty("connect_string", out var link) || link.ValueKind != JsonValueKind.String)
                continue;
            var uri = link.GetString();
            if (uri is null || uri.Length > 16_384 || uri.Any(char.IsControl)) continue;
            output.AppendLine(uri);
            if (output.Length > FreeProxyDbPageCapture.MaximumCaptureBytes)
                throw new InvalidDataException("VPN API connections exceed source size limit.");
        }
        if (Encoding.UTF8.GetByteCount(output.ToString()) > FreeProxyDbPageCapture.MaximumCaptureBytes)
            throw new InvalidDataException("VPN API connections exceed source size limit.");
        return output.ToString();
    }
}

internal sealed record SourceApiFetchResult(SourceFetchResult Fetch, SourceApiCaptureCheckpoint Checkpoint,
    DateTimeOffset ObservedAt, bool NetworkObserved)
{
    internal DateTimeOffset NextRefreshAt => ProxiwarePublicApi.Supports(Checkpoint.Owner.Url)
        ? Checkpoint.Capture.Pages[^1].CapturedAt.AddMinutes(10)
        : RoundProxiesPublicApi.Supports(Checkpoint.Owner.Url) ? Checkpoint.Capture.Pages[^1].CapturedAt.AddMinutes(5)
        : Checkpoint.Capture.Pages[^1].CapturedAt.AddHours(6);
}

internal sealed class SourceApiDeferredException(DateTimeOffset notBefore) : Exception("API collection deferred.")
{
    internal DateTimeOffset NotBefore { get; } = notBefore;
}
