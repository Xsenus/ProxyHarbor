[CmdletBinding()]
param(
    [ValidateRange(1, 300)][int]$TimeoutSeconds = 25,
    [ValidateRange(1024, 1048576)][int]$MaxBodyBytes = 262144,
    [ValidateRange(1, 64)][int]$ThrottleLimit = 12,
    [ValidateRange(1, 10000)][int]$ExpectedFeeds = 298,
    [ValidateRange(1, 10000)][int]$ExpectedProviders = 75,
    [switch]$CatalogOnly,
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Каталог остаётся единственным source of truth. Regex намеренно фиксирует точную
# форму Feed(...), поэтому неожиданное изменение C# definition ломает аудит явно.
$catalogPath = Join-Path $PSScriptRoot '../src/ProxyHarbor.Infrastructure/BuiltInSourceCatalog.cs'
$catalogText = Get-Content -LiteralPath $catalogPath -Raw
$feedPattern = 'Feed\((?<rank>\d+),\s*"[^"]+",\s*"(?<provider>[^"]+)",\s*"(?<url>https://[^"]+)",\s*ProxyProtocol\.(?<protocol>\w+)\)'
$feeds = @([regex]::Matches($catalogText, $feedPattern) | ForEach-Object {
    $url = $_.Groups['url'].Value
    $uri = [Uri]::new($url, [UriKind]::Absolute)
    $providerIdentity = if ($uri.IdnHost.Equals('raw.githubusercontent.com', [StringComparison]::OrdinalIgnoreCase)) {
        $owner = $uri.AbsolutePath.Split('/', [StringSplitOptions]::RemoveEmptyEntries)[0]
        "github:$($owner.ToLowerInvariant())"
    }
    else {
        "host:$($uri.IdnHost.ToLowerInvariant())"
    }
    [pscustomobject]@{
        Rank = [int]$_.Groups['rank'].Value
        Provider = $_.Groups['provider'].Value
        ProviderIdentity = $providerIdentity
        Url = $url
        Protocol = $_.Groups['protocol'].Value
    }
})

if ($feeds.Count -ne $ExpectedFeeds) {
    throw "Каталог содержит $($feeds.Count) feed вместо ожидаемых $ExpectedFeeds."
}
if (@($feeds.ProviderIdentity | Sort-Object -Unique).Count -ne $ExpectedProviders) {
    throw "Каталог не содержит ожидаемые $ExpectedProviders независимых технических владельцев."
}
if (@($feeds.Url | Sort-Object -Unique).Count -ne $ExpectedFeeds) {
    throw 'Каталог содержит повторяющиеся URL.'
}
# Object[].Rank означает размерность массива (1), поэтому ранги feed'ов извлекаются
# через pipeline явно, без конфликтующей member-enumeration.
if ((@($feeds | ForEach-Object { $_.Rank } | Sort-Object) -join ',') -ne ((1..$ExpectedFeeds) -join ',')) {
    throw 'Ранги каталога не образуют непрерывную последовательность.'
}
if (@($feeds | Group-Object Provider | Where-Object {
    @($_.Group.ProviderIdentity | Sort-Object -Unique).Count -ne 1
}).Count -gt 0) {
    throw 'Одно отображаемое имя провайдера связано с несколькими техническими владельцами.'
}
if (@($feeds | Group-Object ProviderIdentity | Where-Object {
    @($_.Group.Provider | Sort-Object -Unique).Count -ne 1
}).Count -gt 0) {
    throw 'Один технический владелец замаскирован несколькими именами провайдера.'
}

if ($CatalogOnly) {
    $catalogReport = [ordered]@{
        auditedAt = [DateTimeOffset]::UtcNow.ToString('O')
        feeds = $feeds.Count
        providers = @($feeds.ProviderIdentity | Sort-Object -Unique).Count
        networkSkipped = $true
        failures = @()
    } | ConvertTo-Json -Depth 3
    Write-Output $catalogReport
    if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
        $absoluteReportPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
        $parent = Split-Path -Parent $absoluteReportPath
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
        Set-Content -LiteralPath $absoluteReportPath -Value $catalogReport -Encoding utf8NoBOM
    }
    return
}

# Каждый worker читает только bounded начало распакованного ответа. Системный proxy
# отключён, timeout охватывает connect, headers и body, финальный redirect обязан быть HTTPS.
$results = @($feeds | ForEach-Object -Parallel {
    $feed = $_
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $true
    $handler.MaxAutomaticRedirections = 5
    $handler.AutomaticDecompression = [Net.DecompressionMethods]::GZip -bor
        [Net.DecompressionMethods]::Deflate -bor [Net.DecompressionMethods]::Brotli
    $client = [Net.Http.HttpClient]::new($handler, $true)
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($using:TimeoutSeconds))
    $request = $null
    $response = $null
    $input = $null
    $body = $null
    try {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $feed.Url)
        $request.Headers.UserAgent.ParseAdd('ProxyHarbor-LiveAudit/1.0')
        $response = $client.SendAsync(
            $request,
            [Net.Http.HttpCompletionOption]::ResponseHeadersRead,
            $timeout.Token).GetAwaiter().GetResult()
        $finalUri = $response.RequestMessage.RequestUri
        if ($finalUri.Scheme -ne 'https') { throw "redirected to non-HTTPS $finalUri" }
        if (-not $response.IsSuccessStatusCode) { throw "HTTP $([int]$response.StatusCode)" }

        $input = $response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
        $body = [IO.MemoryStream]::new([Math]::Min($using:MaxBodyBytes, 8192))
        $buffer = [byte[]]::new(8192)
        while ($body.Length -lt $using:MaxBodyBytes) {
            $remaining = $using:MaxBodyBytes - [int]$body.Length
            $read = $input.ReadAsync(
                $buffer,
                0,
                [Math]::Min($buffer.Length, $remaining),
                $timeout.Token).GetAwaiter().GetResult()
            if ($read -eq 0) { break }
            $body.Write($buffer, 0, $read)
        }
        $text = [Text.Encoding]::UTF8.GetString($body.GetBuffer(), 0, [int]$body.Length)
        # Границы совпадают с ProxyParser: IP:port внутри hostname, credential или
        # дополнительных colon-полей не считается пригодным endpoint'ом.
        if (-not [regex]::IsMatch($text, '(?<![A-Za-z0-9.:_@-])(?:\d{1,3}\.){3}\d{1,3}:\d{1,5}(?![A-Za-z0-9.:_@-])')) {
            throw "no IP:port in first $($body.Length) decoded bytes"
        }

        [pscustomobject]@{
            Rank = $feed.Rank
            Provider = $feed.Provider
            ProviderIdentity = $feed.ProviderIdentity
            Protocol = $feed.Protocol
            Url = $feed.Url
            Ok = $true
            Status = [int]$response.StatusCode
            BytesInspected = [int]$body.Length
            DurationMs = $timer.ElapsedMilliseconds
            Error = $null
        }
    }
    catch {
        [pscustomobject]@{
            Rank = $feed.Rank
            Provider = $feed.Provider
            ProviderIdentity = $feed.ProviderIdentity
            Protocol = $feed.Protocol
            Url = $feed.Url
            Ok = $false
            Status = $null
            BytesInspected = if ($null -eq $body) { 0 } else { [int]$body.Length }
            DurationMs = $timer.ElapsedMilliseconds
            Error = $_.Exception.Message
        }
    }
    finally {
        if ($null -ne $body) { $body.Dispose() }
        if ($null -ne $input) { $input.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $request) { $request.Dispose() }
        $timeout.Dispose()
        $client.Dispose()
    }
} -ThrottleLimit $ThrottleLimit)

$ordered = @($results | Sort-Object Rank)
$failed = @($ordered | Where-Object { -not $_.Ok })
$report = [ordered]@{
    auditedAt = [DateTimeOffset]::UtcNow.ToString('O')
    feeds = $ordered.Count
    providers = @($ordered.ProviderIdentity | Sort-Object -Unique).Count
    succeeded = @($ordered | Where-Object { $_.Ok }).Count
    failed = $failed.Count
    maxDurationMs = ($ordered.DurationMs | Measure-Object -Maximum).Maximum
    failures = $failed
}
$json = $report | ConvertTo-Json -Depth 5
Write-Output $json

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $absoluteReportPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
    $parent = Split-Path -Parent $absoluteReportPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Set-Content -LiteralPath $absoluteReportPath -Value $json -Encoding utf8NoBOM
}
if ($failed.Count -gt 0) { exit 2 }
