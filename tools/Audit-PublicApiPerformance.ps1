[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://localhost:8080',
    [ValidateRange(5, 30)][int]$SamplesPerRoute = 10,
    [ValidateRange(1, 10000)][int]$MaxHotP95Ms = 250,
    [ValidateRange(1, 30000)][int]$MaxColdP95Ms = 1500,
    [ValidateRange(0, 49970)][int]$ColdPageBase = 0,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$resolvedColdPageBase = if ($ColdPageBase -eq 0) { [Random]::Shared.Next(1, 49971) } else { $ColdPageBase }
$report = [ordered]@{
    auditedAt = [DateTimeOffset]::UtcNow.ToString('O')
    apiBaseUrl = $ApiBaseUrl
    samplesPerRoute = $SamplesPerRoute
    maxHotP95Ms = $MaxHotP95Ms
    maxColdP95Ms = $MaxColdP95Ms
    coldPageBase = $resolvedColdPageBase
    success = $false
    totalRequests = 0
    routes = @()
    error = $null
}
function Invoke-TimedJsonRequest([string]$Path) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-WebRequest -Uri "$ApiBaseUrl$Path" -TimeoutSec 30 -NoProxy `
            -Headers @{ 'User-Agent' = 'ProxyHarbor-Performance-Audit/1.0' }
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
        responseBytes = [long](($Samples | Measure-Object -Property bytes -Sum).Sum)
        thresholdMs = $ThresholdMs
    }
}

try {
    # Два warm-up запроса прогревают JIT, connection pool и bounded output cache.
    # Они учитываются в totalRequests, но не искажают измеряемые выборки.
    Invoke-TimedJsonRequest '/api/v1/stats' | Out-Null
    Invoke-TimedJsonRequest '/api/v1/proxies/seek?pageSize=100' | Out-Null
    $report.totalRequests += 2

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
    $hotStats = @(1..$SamplesPerRoute | ForEach-Object {
        Invoke-TimedJsonRequest '/api/v1/stats'
    })
    $report.totalRequests += 3 * $SamplesPerRoute

    $report.routes = @(
        New-RouteMetric 'legacy-list-cold' $coldList $MaxColdP95Ms
        New-RouteMetric 'seek-first-page-hot' $hotSeek $MaxHotP95Ms
        New-RouteMetric 'stats-hot' $hotStats $MaxHotP95Ms
    )
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
