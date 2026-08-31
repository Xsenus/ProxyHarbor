[CmdletBinding()]
param(
    [string]$BaseUrl = 'https://proxy.blagodaty.ru',
    [ValidateRange(3, 120)][int]$TimeoutSec = 20,
    [ValidateRange(1, 365)][int]$MinimumTlsDays = 14,
    [string]$ReportPath,
    [switch]$AllowHttp
)

$ErrorActionPreference = 'Stop'
$baseUri = $null
if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$baseUri) -or
    $baseUri.Scheme -notin @('http', 'https') -or $baseUri.Query -or $baseUri.Fragment) {
    throw 'BaseUrl должен быть абсолютным HTTP(S) URL без query и fragment.'
}
if ($baseUri.Scheme -ne 'https' -and -not $AllowHttp) {
    throw 'Публичный launch-аудит требует HTTPS. -AllowHttp предназначен только для локального contract-теста.'
}
$base = $baseUri.AbsoluteUri.TrimEnd('/')
$publicRoutes = @(
    '/', '/pricing', '/service', '/legal', '/offer', '/privacy',
    '/personal-data-consent', '/marketing-consent', '/acceptable-use',
    '/cookies', '/refunds', '/requisites'
)
$privateRoutes = @('/admin', '/account', '/login', '/register', '/forgot-password', '/reset-password')
$checks = [Collections.Generic.List[object]]::new()
$report = [ordered]@{
    auditedAt = [DateTimeOffset]::UtcNow.ToString('O')
    baseUrl = $base
    success = $false
    checks = $checks
    summary = $null
}

function Add-Check([string]$Name, [bool]$Success, [string]$Details) {
    $checks.Add([ordered]@{ name = $Name; success = $Success; details = $Details })
}

function Invoke-AuditRequest([string]$Path) {
    try {
        return Invoke-WebRequest -Uri "$base$Path" -TimeoutSec $TimeoutSec -NoProxy `
            -SkipHttpErrorCheck -Headers @{ 'User-Agent' = 'ProxyHarbor-Public-Launch-Audit/1.0' }
    }
    catch {
        Add-Check "HTTP $Path" $false $_.Exception.Message
        return $null
    }
}

function Get-Header([object]$Response, [string]$Name) {
    if (-not $Response) { return '' }
    return [string]$Response.Headers[$Name]
}

function Test-Contains([string]$Content, [string]$Literal) {
    return $Content.Contains($Literal, [StringComparison]::OrdinalIgnoreCase)
}

function Test-TlsCertificate {
    if ($baseUri.Scheme -ne 'https') {
        Add-Check 'TLS certificate' $true 'Пропущено для явно разрешённого локального HTTP contract-теста.'
        return
    }
    $port = if ($baseUri.IsDefaultPort) { 443 } else { $baseUri.Port }
    $tcp = [Net.Sockets.TcpClient]::new()
    $ssl = $null
    try {
        $connect = $tcp.ConnectAsync($baseUri.DnsSafeHost, $port)
        if (-not $connect.Wait([TimeSpan]::FromSeconds($TimeoutSec))) { throw 'Истёк timeout TCP-соединения.' }
        $ssl = [Net.Security.SslStream]::new($tcp.GetStream(), $false)
        $auth = $ssl.AuthenticateAsClientAsync($baseUri.DnsSafeHost)
        if (-not $auth.Wait([TimeSpan]::FromSeconds($TimeoutSec))) { throw 'Истёк timeout TLS handshake.' }
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($ssl.RemoteCertificate)
        $remaining = $certificate.NotAfter.ToUniversalTime() - [DateTime]::UtcNow
        Add-Check 'TLS certificate' ($remaining.TotalDays -ge $MinimumTlsDays) `
            ("Истекает {0:u}; осталось {1:N1} дней, минимум {2}." -f $certificate.NotAfter.ToUniversalTime(), $remaining.TotalDays, $MinimumTlsDays)
    }
    catch { Add-Check 'TLS certificate' $false $_.Exception.Message }
    finally {
        if ($ssl) { $ssl.Dispose() }
        $tcp.Dispose()
    }
}

try {
    Test-TlsCertificate

    $rootResponse = $null
    foreach ($path in $publicRoutes) {
        $response = Invoke-AuditRequest $path
        if (-not $response) { continue }
        if ($path -eq '/') { $rootResponse = $response }
        $content = [string]$response.Content
        $canonical = if ($path -eq '/') { "$base/" } else { "$base$path" }
        $isHtml = (Get-Header $response 'Content-Type') -like 'text/html*'
        $metadataOk = $isHtml -and
            (Test-Contains $content '<title>') -and
            (Test-Contains $content 'name="description"') -and
            (Test-Contains $content 'name="robots" content="index, follow') -and
            (Test-Contains $content "rel=`"canonical`" href=`"$canonical`"") -and
            (Test-Contains $content 'type="application/ld+json"')
        Add-Check "Public route $path" ($response.StatusCode -eq 200 -and $metadataOk) `
            "HTTP $($response.StatusCode); Content-Type=$(Get-Header $response 'Content-Type'); canonical=$canonical."
    }

    if ($rootResponse) {
        $requiredHeaders = [ordered]@{
            'Strict-Transport-Security' = 'max-age='
            'Content-Security-Policy' = "default-src 'self'"
            'X-Content-Type-Options' = 'nosniff'
            'X-Frame-Options' = 'DENY'
            'Referrer-Policy' = 'strict-origin-when-cross-origin'
            'Permissions-Policy' = 'camera=()'
        }
        foreach ($entry in $requiredHeaders.GetEnumerator()) {
            $value = Get-Header $rootResponse $entry.Key
            $required = $entry.Value
            Add-Check "Security header $($entry.Key)" ($value.Contains($required, [StringComparison]::OrdinalIgnoreCase)) $value
        }
    }

    $robots = Invoke-AuditRequest '/robots.txt'
    if ($robots) {
        $robotsBody = [string]$robots.Content
        $robotsOk = $robots.StatusCode -eq 200 -and
            (Test-Contains $robotsBody "Sitemap: $base/sitemap.xml") -and
            @('/admin', '/account', '/login', '/register', '/forgot-password', '/reset-password').Where({
                -not (Test-Contains $robotsBody "Disallow: $_")
            }).Count -eq 0
        Add-Check 'robots.txt' $robotsOk "HTTP $($robots.StatusCode); private routes и sitemap должны быть объявлены."
    }

    $sitemap = Invoke-AuditRequest '/sitemap.xml'
    if ($sitemap) {
        try {
            [xml]$xml = [string]$sitemap.Content
            $locations = @($xml.urlset.url | ForEach-Object { [string]$_.loc })
            $expected = @($publicRoutes | ForEach-Object { if ($_ -eq '/') { "$base/" } else { "$base$_" } })
            $missing = @($expected | Where-Object { $_ -notin $locations })
            $unexpected = @($locations | Where-Object { $_ -notin $expected })
            Add-Check 'sitemap.xml' ($sitemap.StatusCode -eq 200 -and $missing.Count -eq 0 -and $unexpected.Count -eq 0) `
                "HTTP $($sitemap.StatusCode); URL=$($locations.Count); missing=$($missing -join ','); unexpected=$($unexpected -join ',')."
        }
        catch { Add-Check 'sitemap.xml' $false "Некорректный XML: $($_.Exception.Message)" }
    }

    $indexNowKeyPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/proxyharbor-web/public/indexnow-key.txt'
    if (Test-Path -LiteralPath $indexNowKeyPath) {
        $expectedIndexNowKey = (Get-Content -LiteralPath $indexNowKeyPath -Raw).Trim()
        $indexNowKey = Invoke-AuditRequest '/indexnow-key.txt'
        if ($indexNowKey) {
            $publishedIndexNowKey = ([string]$indexNowKey.Content).Trim()
            Add-Check 'IndexNow ownership key' `
                ($indexNowKey.StatusCode -eq 200 -and $expectedIndexNowKey -match '^[A-Za-z0-9-]{8,128}$' -and
                    $publishedIndexNowKey -ceq $expectedIndexNowKey) `
                "HTTP $($indexNowKey.StatusCode); published key must exactly match the repository key."
        }
    }
    else { Add-Check 'IndexNow ownership key' $false "Repository key file is missing: $indexNowKeyPath" }

    foreach ($path in $privateRoutes) {
        $response = Invoke-AuditRequest "$path`?launch-audit=1"
        if (-not $response) { continue }
        $robotsHeader = Get-Header $response 'X-Robots-Tag'
        $statusOk = $response.StatusCode -ge 200 -and $response.StatusCode -lt 400
        $noIndex = $robotsHeader -match '(?i)noindex' -and $robotsHeader -match '(?i)nofollow' -and $robotsHeader -match '(?i)noarchive'
        Add-Check "Private noindex $path" ($statusOk -and $noIndex) "HTTP $($response.StatusCode); X-Robots-Tag=$robotsHeader."
    }

    $missingPath = "/public-launch-audit-not-found-$([Guid]::NewGuid().ToString('N'))"
    $notFound = Invoke-AuditRequest $missingPath
    if ($notFound) { Add-Check 'Unknown route returns 404' ($notFound.StatusCode -eq 404) "HTTP $($notFound.StatusCode)." }

    $health = Invoke-AuditRequest '/health/ready'
    if ($health) {
        try {
            $healthJson = ([string]$health.Content) | ConvertFrom-Json
            Add-Check 'Production readiness' ($health.StatusCode -eq 200 -and $healthJson.status -eq 'healthy') `
                "HTTP $($health.StatusCode); status=$($healthJson.status)."
        }
        catch { Add-Check 'Production readiness' $false "Некорректный JSON: $($_.Exception.Message)" }
    }

    $catalog = Invoke-AuditRequest '/api/v1/payments/catalog'
    if ($catalog) {
        try {
            $catalogJson = ([string]$catalog.Content) | ConvertFrom-Json
            $products = @($catalogJson.products)
            $availableProviders = @($catalogJson.providers | Where-Object available)
            $invalidProducts = @($products | Where-Object {
                -not $_.code -or -not $_.name -or $_.amountMinor -le 0 -or $_.currency -ne 'RUB' -or $_.durationDays -le 0
            })
            $paymentOk = $catalog.StatusCode -eq 200 -and $catalogJson.enabled -eq $true -and
                $products.Count -gt 0 -and $invalidProducts.Count -eq 0 -and $availableProviders.Count -gt 0
            Add-Check 'Payment catalog' $paymentOk `
                "HTTP $($catalog.StatusCode); products=$($products.Count); available=$($availableProviders.code -join ',')."
        }
        catch { Add-Check 'Payment catalog' $false "Некорректный JSON: $($_.Exception.Message)" }
    }

    $failed = @($checks | Where-Object { -not $_.success })
    $report.success = $failed.Count -eq 0
    $report.summary = [ordered]@{ total = $checks.Count; passed = $checks.Count - $failed.Count; failed = $failed.Count }
    if ($failed.Count -gt 0) {
        throw "Public launch-аудит не пройден: $($failed.name -join '; ')."
    }
    Write-Host "Public launch-аудит пройден: $($checks.Count) проверок, SEO/legal/TLS/security/readiness/payments." -ForegroundColor Green
}
catch {
    if (-not $report.summary) {
        $failed = @($checks | Where-Object { -not $_.success })
        $report.summary = [ordered]@{ total = $checks.Count; passed = $checks.Count - $failed.Count; failed = $failed.Count }
    }
    throw
}
finally {
    if ($ReportPath) {
        $absoluteReportPath = if ([IO.Path]::IsPathRooted($ReportPath)) {
            [IO.Path]::GetFullPath($ReportPath)
        } else { [IO.Path]::GetFullPath((Join-Path (Get-Location) $ReportPath)) }
        $directory = [IO.Path]::GetDirectoryName($absoluteReportPath)
        if ($directory) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
        $temporaryReport = "$absoluteReportPath.partial"
        try {
            [IO.File]::WriteAllText($temporaryReport, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
            [IO.File]::Move($temporaryReport, $absoluteReportPath, $true)
            Write-Host "JSON-отчёт launch-аудита сохранён: $absoluteReportPath"
        }
        finally { if ([IO.File]::Exists($temporaryReport)) { [IO.File]::Delete($temporaryReport) } }
    }
}
