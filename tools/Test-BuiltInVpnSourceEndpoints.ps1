[CmdletBinding()]
param(
    [ValidateRange(1, 300)][int]$TimeoutSeconds = 25,
    [ValidateRange(1024, 33554432)][int]$MaxBodyBytes = 33554432,
    [ValidateRange(1, 64)][int]$ThrottleLimit = 12,
    [ValidateRange(1, 10000)][int]$ExpectedFeeds = 149,
    [ValidateRange(1, 10000)][int]$ExpectedProviders = 23,
    [switch]$CatalogOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$catalogPath = Join-Path $PSScriptRoot '../src/ProxyHarbor.Infrastructure/BuiltInVpnSourceCatalog.cs'
$catalogText = Get-Content -LiteralPath $catalogPath -Raw
$pattern = 'new\("(?<name>[^"]+)",\s*"(?<provider>[^"]+)",\s*"(?<url>https://[^"]+)",\s*VpnProtocol\.(?<protocol>\w+),\s*"(?<license>[^"]+)"\)'
$feeds = @([regex]::Matches($catalogText, $pattern) | ForEach-Object {
    [pscustomobject]@{
        Name = $_.Groups['name'].Value
        Provider = $_.Groups['provider'].Value
        Url = $_.Groups['url'].Value
        Protocol = $_.Groups['protocol'].Value
        License = $_.Groups['license'].Value
    }
})

if ($feeds.Count -ne $ExpectedFeeds) { throw "VPN-каталог содержит $($feeds.Count) feed вместо $ExpectedFeeds." }
if (@($feeds.Provider | Sort-Object -Unique).Count -ne $ExpectedProviders) {
    throw "VPN-каталог содержит не $ExpectedProviders ожидаемых провайдеров."
}
if (@($feeds.Url | Sort-Object -Unique).Count -ne $ExpectedFeeds) { throw 'VPN-каталог содержит повторяющиеся URL.' }
if (@($feeds | Where-Object { [string]::IsNullOrWhiteSpace($_.License) }).Count -gt 0) {
    throw 'VPN-каталог содержит источник без лицензии или условий публикации.'
}

if ($CatalogOnly) {
    [ordered]@{
        auditedAt = [DateTimeOffset]::UtcNow.ToString('O')
        feeds = $feeds.Count
        providers = @($feeds.Provider | Sort-Object -Unique).Count
        networkSkipped = $true
        failures = @()
    } | ConvertTo-Json -Depth 3
    return
}

$results = @($feeds | ForEach-Object -Parallel {
    $feed = $_
    try {
        $response = Invoke-WebRequest -Uri $feed.Url -TimeoutSec $using:TimeoutSeconds -MaximumRedirection 5
        if ($response.BaseResponse.RequestMessage.RequestUri.Scheme -ne 'https') {
            throw 'redirected to non-HTTPS endpoint'
        }
        $content = [string]$response.Content
        if ([Text.Encoding]::UTF8.GetByteCount($content) -gt $using:MaxBodyBytes) { throw 'body exceeds audit limit' }

        $parseable = $content -match '(?im)(vless|vmess|trojan|ss|hysteria2|hy2|tuic|wireguard|wg)://'
        if (-not $parseable -and $feed.Protocol -eq 'OpenVpn') {
            $parseable = $content -match '(?im)^remote\s+[^\s]+\s+\d{1,5}' -or
                $content -match 'OpenVPN_ConfigData_Base64'
        }
        if (-not $parseable -and $content.Length -gt 0) {
            try {
                $decoded = [Text.Encoding]::UTF8.GetString(
                    [Convert]::FromBase64String(($content -replace '\s', '')))
                $parseable = $decoded -match '(?im)(vless|vmess|trojan|ss|hysteria2|hy2|tuic|wireguard|wg)://' -or
                    $decoded -match '(?im)^remote\s+[^\s]+\s+\d{1,5}'
            }
            catch [FormatException] { }
        }
        if (-not $parseable) { throw 'response has no supported VPN configuration' }
        [pscustomobject]@{ Url = $feed.Url; Success = $true; Error = $null }
    }
    catch {
        [pscustomobject]@{ Url = $feed.Url; Success = $false; Error = $_.Exception.Message }
    }
} -ThrottleLimit $ThrottleLimit)

$failures = @($results | Where-Object { -not $_.Success })
[ordered]@{
    auditedAt = [DateTimeOffset]::UtcNow.ToString('O')
    feeds = $feeds.Count
    providers = @($feeds.Provider | Sort-Object -Unique).Count
    succeeded = $results.Count - $failures.Count
    failures = $failures
} | ConvertTo-Json -Depth 4
if ($failures.Count -gt 0) { throw "VPN live-аудит завершился с $($failures.Count) ошибками." }
