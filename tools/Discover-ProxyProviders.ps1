param(
    [string]$OutputPath = (Join-Path ([System.IO.Path]::GetTempPath()) 'proxyharbor-provider-candidates.json'),
    [int]$MinimumEndpoints = 10,
    [int]$MaximumProviders = 250,
    [ValidateSet('Primary', 'Extended', 'Paginated', 'PaginatedFree', 'PaginatedWorking', 'PaginatedFresh', 'PaginatedTopic', 'PaginatedProxiesTopic', 'PaginatedSocks5Topic', 'PaginatedHttpTopic', 'PaginatedScraper')]
    [string]$SearchWave = 'Primary'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$catalogPath = Join-Path $PSScriptRoot '../src/ProxyHarbor.Infrastructure/BuiltInSourceCatalog.cs'
$catalog = Get-Content -Raw -LiteralPath $catalogPath
$existingOwners = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[regex]::Matches($catalog, 'raw\.githubusercontent\.com/(?<owner>[^/]+)/') | ForEach-Object {
    [void]$existingOwners.Add($_.Groups['owner'].Value)
}

$queries = if ($SearchWave -eq 'Primary') {
    @(
        '"free proxy" in:name,description fork:false',
        '"proxy list" in:name,description fork:false',
        '"proxies list" in:name,description fork:false',
        '"proxy scraper" in:name,description fork:false',
        'topic:proxy-list fork:false',
        'topic:free-proxy fork:false',
        'topic:proxies fork:false',
        'topic:socks5-proxy fork:false',
        'topic:http-proxy fork:false',
        'topic:proxy-scraper fork:false'
    )
}
elseif ($SearchWave -eq 'Extended') {
    @(
        '"fresh proxy" in:name,description fork:false',
        '"working proxies" in:name,description fork:false',
        '"proxy pool" in:name,description fork:false',
        '"proxy database" in:name,description fork:false',
        '"proxy feed" in:name,description fork:false',
        'topic:socks4 fork:false',
        'topic:socks5 fork:false',
        'topic:free-proxies fork:false',
        'topic:proxy-checker fork:false',
        '代理池 in:name,description fork:false'
    )
}
elseif ($SearchWave -eq 'Paginated') {
    @('"proxy list" in:name,description fork:false')
}
elseif ($SearchWave -eq 'PaginatedFree') {
    @('"free proxy" in:name,description fork:false')
}
elseif ($SearchWave -eq 'PaginatedWorking') {
    @('"working proxies" in:name,description fork:false')
}
elseif ($SearchWave -eq 'PaginatedFresh') {
    @('"fresh proxy" in:name,description fork:false')
}
elseif ($SearchWave -eq 'PaginatedTopic') {
    @('topic:proxy-list fork:false')
}
elseif ($SearchWave -eq 'PaginatedProxiesTopic') {
    @('topic:proxies fork:false')
}
elseif ($SearchWave -eq 'PaginatedSocks5Topic') {
    @('topic:socks5 fork:false')
}
elseif ($SearchWave -eq 'PaginatedHttpTopic') {
    @('topic:http-proxy fork:false')
}
else {
    @('"proxy scraper" in:name,description fork:false')
}
$pages = if ($SearchWave -in @('Paginated', 'PaginatedFree', 'PaginatedWorking', 'PaginatedFresh', 'PaginatedTopic', 'PaginatedProxiesTopic', 'PaginatedSocks5Topic', 'PaginatedHttpTopic', 'PaginatedScraper')) { 2..10 } else { @(1) }
$headers = @{
    'Accept' = 'application/vnd.github+json'
    'User-Agent' = 'ProxyHarbor-source-audit'
}
$repositories = @{}
$searchCachePath = "$OutputPath.repositories.json"
if ((Test-Path -LiteralPath $searchCachePath) -and
    (Get-Item -LiteralPath $searchCachePath).LastWriteTimeUtc -gt [DateTime]::UtcNow.AddHours(-1)) {
    foreach ($repository in (Get-Content -Raw -LiteralPath $searchCachePath | ConvertFrom-Json)) {
        $repositories[$repository.full_name] = $repository
    }
}
else {
    foreach ($query in $queries) {
        foreach ($page in $pages) {
            $uri = 'https://api.github.com/search/repositories?q=' +
                [uri]::EscapeDataString($query) + "&sort=updated&order=desc&per_page=100&page=$page"
            try {
                $response = Invoke-RestMethod -Headers $headers -Uri $uri -TimeoutSec 30
                foreach ($repository in $response.items) {
                    $repositories[$repository.full_name] = $repository
                }
            }
            catch {
                Write-Warning "GitHub search failed for '$query' page ${page}: $($_.Exception.Message)"
            }
            Start-Sleep -Seconds 7
        }
    }
    @($repositories.Values) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $searchCachePath -Encoding utf8
}

$paths = @(
    @{ Path = 'http.txt'; Protocol = 'Http' },
    @{ Path = 'https.txt'; Protocol = 'Https' },
    @{ Path = 'socks4.txt'; Protocol = 'Socks4' },
    @{ Path = 'socks5.txt'; Protocol = 'Socks5' },
    @{ Path = 'proxies.txt'; Protocol = 'Http' },
    @{ Path = 'proxy.txt'; Protocol = 'Http' },
    @{ Path = 'proxy-list.txt'; Protocol = 'Http' },
    @{ Path = 'working_proxies.txt'; Protocol = 'Http' },
    @{ Path = 'all.txt'; Protocol = 'Http' },
    @{ Path = 'list.txt'; Protocol = 'Http' },
    @{ Path = 'HTTP.txt'; Protocol = 'Http' },
    @{ Path = 'HTTPS.txt'; Protocol = 'Https' },
    @{ Path = 'SOCKS4.txt'; Protocol = 'Socks4' },
    @{ Path = 'SOCKS5.txt'; Protocol = 'Socks5' },
    @{ Path = 'proxies/http.txt'; Protocol = 'Http' },
    @{ Path = 'proxies/https.txt'; Protocol = 'Https' },
    @{ Path = 'proxies/socks4.txt'; Protocol = 'Socks4' },
    @{ Path = 'proxies/socks5.txt'; Protocol = 'Socks5' },
    @{ Path = 'proxy/http.txt'; Protocol = 'Http' },
    @{ Path = 'proxy/https.txt'; Protocol = 'Https' },
    @{ Path = 'proxy/socks4.txt'; Protocol = 'Socks4' },
    @{ Path = 'proxy/socks5.txt'; Protocol = 'Socks5' },
    @{ Path = 'output/http.txt'; Protocol = 'Http' },
    @{ Path = 'output/https.txt'; Protocol = 'Https' },
    @{ Path = 'output/socks4.txt'; Protocol = 'Socks4' },
    @{ Path = 'output/socks5.txt'; Protocol = 'Socks5' },
    @{ Path = 'results/http.txt'; Protocol = 'Http' },
    @{ Path = 'results/https.txt'; Protocol = 'Https' },
    @{ Path = 'results/socks4.txt'; Protocol = 'Socks4' },
    @{ Path = 'results/socks5.txt'; Protocol = 'Socks5' },
    @{ Path = 'data/http.txt'; Protocol = 'Http' },
    @{ Path = 'data/https.txt'; Protocol = 'Https' },
    @{ Path = 'data/socks4.txt'; Protocol = 'Socks4' },
    @{ Path = 'data/socks5.txt'; Protocol = 'Socks5' },
    @{ Path = 'scraped_proxies/http.txt'; Protocol = 'Http' },
    @{ Path = 'checked_proxies/http.txt'; Protocol = 'Http' }
)

$candidates = @($repositories.Values | Where-Object {
    -not $_.archived -and -not $_.fork -and -not $existingOwners.Contains($_.owner.login)
} | Sort-Object pushed_at -Descending)

$results = $candidates | ForEach-Object -Parallel {
    $repository = $_
    $pathCandidates = $using:paths
    $minimum = $using:MinimumEndpoints
    $endpointPattern = '(?<![\w.:-])(?:\d{1,3}\.){3}\d{1,3}:\d{1,5}(?![\w.:-])'
    foreach ($candidatePath in $pathCandidates) {
        $escapedPath = ($candidatePath.Path.Split('/') | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'
        $url = "https://raw.githubusercontent.com/$($repository.full_name)/$($repository.default_branch)/$escapedPath"
        try {
            $content = (& curl.exe --location --silent --fail --range 0-524287 --connect-timeout 2 --max-time 5 $url 2>$null) -join "`n"
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($content)) { continue }
            $count = [regex]::Matches($content, $endpointPattern).Count
            if ($count -lt $minimum) { continue }
            [pscustomobject]@{
                Provider = $repository.owner.login
                Repository = $repository.full_name
                Url = $url
                Protocol = $candidatePath.Protocol
                EndpointCount = $count
                License = if ($repository.license) { $repository.license.spdx_id } else { $null }
                PushedAt = $repository.pushed_at
                Stars = $repository.stargazers_count
            }
            break
        }
        catch { }
    }
} -ThrottleLimit 48

$sortProperties = @(
    @{ Expression = { $_.License -notin @($null, 'NOASSERTION', 'OTHER') }; Descending = $true }
    @{ Expression = 'EndpointCount'; Descending = $true }
    @{ Expression = 'PushedAt'; Descending = $true }
)
$bestPerOwner = @($results | Group-Object Provider | ForEach-Object {
    $_.Group | Sort-Object -Property $sortProperties | Select-Object -First 1
})
$ordered = @($bestPerOwner | Sort-Object -Property $sortProperties | Select-Object -First $MaximumProviders)
ConvertTo-Json -InputObject $ordered -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8

$licensed = @($ordered | Where-Object { $_.License -and $_.License -notin @('NOASSERTION', 'OTHER') })
[pscustomobject]@{
    SearchRepositories = $repositories.Count
    NewOwnersProbed = $candidates.Count
    ValidProviders = $ordered.Count
    LicensedProviders = $licensed.Count
    OutputPath = (Resolve-Path -LiteralPath $OutputPath).Path
} | Format-List
