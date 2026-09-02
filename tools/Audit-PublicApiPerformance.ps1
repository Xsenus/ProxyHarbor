[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://localhost:8080',
    [ValidateSet('Paid', 'Anonymous')][string]$AccessMode = 'Paid',
    [string]$BearerTokenFile,
    [ValidateRange(5, 30)][int]$SamplesPerRoute = 20,
    [ValidateRange(1, 10000)][int]$MaxHotP95Ms = 250,
    [ValidateRange(1, 30000)][int]$MaxColdP95Ms = 1500,
    [ValidateRange(0, 49970)][int]$ColdPageBase = 0,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$resolvedColdPageBase = if ($ColdPageBase -eq 0) { [Random]::Shared.Next(1, 49971) } else { $ColdPageBase }
$report = [ordered]@{
    schemaVersion = 2
    auditedAt = [DateTimeOffset]::UtcNow.ToString('O')
    apiBaseUrl = $ApiBaseUrl
    accessMode = $AccessMode
    samplesPerRoute = $SamplesPerRoute
    maxHotP95Ms = $MaxHotP95Ms
    maxColdP95Ms = $MaxColdP95Ms
    coldPageBase = $resolvedColdPageBase
    transportSession = 'reused'
    success = $false
    totalRequests = 0
    routes = @()
    error = $null
}
$requestHeaders = @{ 'User-Agent' = 'ProxyHarbor-Performance-Audit/1.2' }
$requestSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
function Invoke-TimedJsonRequest([string]$Path) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-WebRequest -Uri "$ApiBaseUrl$Path" -TimeoutSec 30 -NoProxy `
            -Headers $requestHeaders -WebSession $requestSession
        $timer.Stop()
        if ($response.StatusCode -ne 200) {
            throw "GET $Path вернул HTTP $($response.StatusCode)."
        }
        $contentType = [string]$response.Headers.'Content-Type'
        if ($contentType -notlike 'application/json*') {
            throw "GET $Path вернул Content-Type $contentType вместо application/json."
        }
        try { $response.Content | ConvertFrom-Json | Out-Null }
        catch { throw "GET $Path вернул некорректный JSON: $($_.Exception.Message)" }
        return [pscustomobject]@{
            elapsedMs = [Math]::Max(0.01, $timer.Elapsed.TotalMilliseconds)
            bytes = [Text.Encoding]::UTF8.GetByteCount([string]$response.Content)
        }
    }
    catch {
        $timer.Stop()
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            throw "GET $Path вернул HTTP $([int]$_.Exception.Response.StatusCode)."
        }
        throw
    }
}

function Get-Percentile([double[]]$Values, [double]$Percentile) {
    $ordered = @($Values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Ceiling($Percentile * $ordered.Count) - 1)
    return [double]$ordered[$index]
}

function New-RouteMetric([string]$Name, [object[]]$Samples, [int]$ThresholdMs) {
    $latencies = [double[]]@($Samples | ForEach-Object elapsedMs)
    return [ordered]@{
        name = $Name
        samples = $Samples.Count
        p50Ms = [Math]::Round((Get-Percentile $latencies 0.50), 2)
        p95Ms = [Math]::Round((Get-Percentile $latencies 0.95), 2)
        maxMs = [Math]::Round(($latencies | Measure-Object -Maximum).Maximum, 2)
        averageMs = [Math]::Round(($latencies | Measure-Object -Average).Average, 2)
        sampleLatenciesMs = [double[]]@($latencies | ForEach-Object { [Math]::Round($_, 2) })
        responseBytes = [long](($Samples | Measure-Object -Property bytes -Sum).Sum)
        thresholdMs = $ThresholdMs
    }
}

try {
    if ($BearerTokenFile) {
        $absoluteTokenPath = [IO.Path]::GetFullPath($BearerTokenFile)
        if (-not [IO.File]::Exists($absoluteTokenPath)) {
            throw "Файл Bearer-токена не найден: $absoluteTokenPath"
        }
        $bearerToken = [IO.File]::ReadAllText($absoluteTokenPath).Trim()
        if ([string]::IsNullOrWhiteSpace($bearerToken) -or $bearerToken.Length -gt 4096 -or
            $bearerToken.IndexOfAny([char[]]@(0, 10, 13)) -ge 0) {
            throw 'Файл Bearer-токена пуст, слишком длинен или содержит управляющие символы.'
        }
        $requestHeaders.Authorization = "Bearer $bearerToken"
    }

    # Каждый измеряемый hot-маршрут прогревается отдельно: общий JIT/connection pool
    # не заменяет собственный output-cache key VPN/Proxy/Stats. Warm-up учитывается
    # в totalRequests, но не искажает измеряемые выборки.
    Invoke-TimedJsonRequest '/api/v1/stats' | Out-Null
    $warmupRequests = 1
    if ($AccessMode -eq 'Paid') {
        Invoke-TimedJsonRequest '/api/v1/proxies/seek?pageSize=100' | Out-Null
        $warmupRequests++
    } else {
        Invoke-TimedJsonRequest '/api/v1/proxies?page=1&pageSize=10' | Out-Null
        Invoke-TimedJsonRequest '/api/v1/vpn?page=1&pageSize=10' | Out-Null
        $warmupRequests += 2
    }
    $report.totalRequests += $warmupRequests

    $hotStats = @(1..$SamplesPerRoute | ForEach-Object {
        Invoke-TimedJsonRequest '/api/v1/stats'
    })
    if ($AccessMode -eq 'Paid') {
        $coldList = @()
        foreach ($offset in 0..($SamplesPerRoute - 1)) {
            # Случайная безопасная база почти исключает повторное использование cache keys
            # прошлого аудита и измеряет bounded deep legacy OFFSET + точный COUNT.
            $page = $resolvedColdPageBase + $offset
            $coldList += Invoke-TimedJsonRequest "/api/v1/proxies?page=$page&pageSize=100"
        }
        $hotSeek = @(1..$SamplesPerRoute | ForEach-Object {
            Invoke-TimedJsonRequest '/api/v1/proxies/seek?pageSize=100'
        })
        $report.routes = @(
            New-RouteMetric 'legacy-list-cold' $coldList $MaxColdP95Ms
            New-RouteMetric 'seek-first-page-hot' $hotSeek $MaxHotP95Ms
            New-RouteMetric 'stats-hot' $hotStats $MaxHotP95Ms
        )
    } else {
        # Анонимный production-аудит измеряет только реально доступные маршруты.
        # Он не подменяет paid-проверку глубокого OFFSET/keyset, а дополняет её.
        $hotProxyCatalog = @(1..$SamplesPerRoute | ForEach-Object {
            Invoke-TimedJsonRequest '/api/v1/proxies?page=1&pageSize=10'
        })
        $hotVpnCatalog = @(1..$SamplesPerRoute | ForEach-Object {
            Invoke-TimedJsonRequest '/api/v1/vpn?page=1&pageSize=10'
        })
        $report.routes = @(
            New-RouteMetric 'proxy-catalog-anonymous-hot' $hotProxyCatalog $MaxHotP95Ms
            New-RouteMetric 'vpn-catalog-anonymous-hot' $hotVpnCatalog $MaxHotP95Ms
            New-RouteMetric 'stats-hot' $hotStats $MaxHotP95Ms
        )
    }
    $report.totalRequests += 3 * $SamplesPerRoute
    $violations = @($report.routes | Where-Object { $_.p95Ms -gt $_.thresholdMs })
    if ($violations.Count -gt 0) {
        $details = $violations | ForEach-Object { "$($_.name) p95=$($_.p95Ms)ms > $($_.thresholdMs)ms" }
        throw "Public API performance SLO нарушен: $($details -join '; ')."
    }

    $report.success = $true
    Write-Host ("Public API performance-аудит пройден: {0} запросов; {1}." -f
        $report.totalRequests,
        (($report.routes | ForEach-Object { "$($_.name) p95=$($_.p95Ms)ms" }) -join ', ')) -ForegroundColor Green
}
catch {
    $report.error = $_.Exception.Message
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
            [IO.File]::WriteAllText($temporaryReport, ($report | ConvertTo-Json -Depth 8),
                [Text.UTF8Encoding]::new($false))
            [IO.File]::Move($temporaryReport, $absoluteReportPath, $true)
            Write-Host "JSON-отчёт performance-аудита сохранён: $absoluteReportPath"
        }
        finally {
            if ([IO.File]::Exists($temporaryReport)) { [IO.File]::Delete($temporaryReport) }
        }
    }
}
