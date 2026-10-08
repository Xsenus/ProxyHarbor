using System.Runtime.ExceptionServices;

namespace ProxyHarbor.Infrastructure;

/// <summary>Shares at most one conditional and one full CSV fetch within a collection phase.</summary>
internal sealed class HProxyCsvFetchScope : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SourceFetchResult? _first;
    private SourceFetchResult? _full;
    private HProxyCsvFeedAdapter.Document? _document;
    private string? _firstETag;
    private DateTimeOffset? _firstLastModified;
    private ExceptionDispatchInfo? _failure;

    internal async Task<SourceFetchResult> FetchAsync(
        string url, string? etag, DateTimeOffset? lastModified,
        Func<string?, DateTimeOffset?, CancellationToken, Task<SourceFetchResult>> fetch,
        CancellationToken token)
    {
        if (!HProxyCsvFeedAdapter.TryGetCountry(url, out var country)) throw new InvalidDataException("Неверный URL CSV HProxy.");
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            _failure?.Throw();
            if (_first is null)
            {
                _firstETag = etag;
                _firstLastModified = lastModified;
                _first = await fetch(etag, lastModified, token);
                if (!_first.NotModified) SetFull(_first);
            }
            // A 304 authorizes only sources with exactly the validators sent upstream.
            if (_first.NotModified && etag == _firstETag && lastModified == _firstLastModified) return _first;
            if (_full is null) SetFull(await fetch(null, null, token));
            return _full! with { Content = _document!.Project(country) };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A country without rows is a projection error, not a publisher-wide failure.
            if (_document is null) _failure = ExceptionDispatchInfo.Capture(exception);
            throw;
        }
        finally { _gate.Release(); }
    }

    private void SetFull(SourceFetchResult result)
    {
        if (result.NotModified || result.Content is null) throw new InvalidDataException("Источник HProxy вернул 304 без полного CSV.");
        _document = HProxyCsvFeedAdapter.Parse(result.Content);
        _full = result;
    }

    public void Dispose() => _gate.Dispose();
}
