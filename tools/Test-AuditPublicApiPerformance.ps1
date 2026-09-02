$ErrorActionPreference = 'Stop'
$auditScript = Join-Path $PSScriptRoot 'Audit-PublicApiPerformance.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-performance-audit-$([Guid]::NewGuid().ToString('N'))"

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-PerformanceMock(
    [int]$Port,
    [ValidateSet('success', 'slow', 'failure', 'rate-limit')][string]$Mode,
    [ValidateSet('Paid', 'Anonymous')][string]$AccessMode) {
    # Paid: stats+seek warm-up; Anonymous: stats+proxy+VPN warm-up.
    $expected = if ($Mode -in 'failure', 'rate-limit') { 1 } elseif ($AccessMode -eq 'Anonymous') { 18 } else { 17 }
    Start-Job -ArgumentList $Port, $Mode, $expected -ScriptBlock {
        param($Port, $Mode, $Expected)
        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        $handled = 0
        try {
            while ($handled -lt $Expected) {
                $context = $listener.GetContext()
                if ($context.Request.Url.AbsolutePath -eq '/ready') {
                    $json = '{}'
                } else {
                    $handled++
                    if ($Mode -eq 'slow') { Start-Sleep -Milliseconds 30 }
                    if ($Mode -eq 'failure') { $context.Response.StatusCode = 503 }
                    if ($Mode -eq 'rate-limit') { $context.Response.StatusCode = 429 }
                    $json = if ($context.Request.Url.AbsolutePath -eq '/api/v1/stats') {
                        '{"total":1,"alive":1}'
                    } else {
                        '{"items":[],"pageSize":100,"hasMore":false,"nextCursor":null}'
                    }
                }
                $bytes = [Text.Encoding]::UTF8.GetBytes($json)
                $context.Response.ContentType = 'application/json'
                $context.Response.ContentLength64 = $bytes.Length
                $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                $context.Response.Close()
            }
        }
        finally { $listener.Stop(); $listener.Close() }
    }
}

function Wait-PerformanceMock(
    [int]$Port,
    [Management.Automation.Job]$Job,
    [TimeSpan]$Timeout = [TimeSpan]::FromSeconds(20)) {
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed -lt $Timeout) {
        if ($Job.State -in 'Failed', 'Stopped', 'Completed') {
            $reason = @($Job.ChildJobs | ForEach-Object { $_.JobStateInfo.Reason.Message } |
                Where-Object { $_ }) -join '; '
            if (-not $reason) { $reason = 'причина не сообщена' }
            throw "Performance mock на порту $Port завершился до readiness " +
                "(state=$($Job.State)): $reason."
        }
        try {
            Invoke-WebRequest -Uri "http://127.0.0.1:$Port/ready" -TimeoutSec 1 | Out-Null
            return
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    throw "Performance mock на порту $Port не перешёл в readiness за " +
        "$([Math]::Round($Timeout.TotalSeconds, 1)) с (state=$($Job.State))."
}

function Invoke-PerformanceCase(
    [string]$Mode,
    [bool]$ShouldSucceed,
    [ValidateSet('Paid', 'Anonymous')][string]$AccessMode = 'Paid') {
    $port = Get-FreeTcpPort
    $job = Start-PerformanceMock -Port $port -Mode $Mode -AccessMode $AccessMode
    $reportPath = Join-Path $fixtureRoot "$Mode.json"
    try {
        Wait-PerformanceMock -Port $port -Job $job
        $rejected = $false
        try {
            $hotLimit = if ($Mode -eq 'slow') { 5 } else { 1000 }
            & $auditScript -ApiBaseUrl "http://127.0.0.1:$port" -SamplesPerRoute 5 `
                -AccessMode $AccessMode -MaxHotP95Ms $hotLimit -MaxColdP95Ms $hotLimit `
                -ReportPath $reportPath *> $null
        }
        catch { $rejected = $true }
        $completed = Wait-Job $job -Timeout 10
        if (-not $completed) { throw "Performance mock $Mode не завершил ожидаемые запросы." }
        Receive-Job $job -ErrorAction Stop | Out-Null
        if (-not [IO.File]::Exists($reportPath)) { throw "Performance case $Mode не сохранил отчёт." }
        $report = Get-Content $reportPath -Raw | ConvertFrom-Json
        $expectedRequests = if ($AccessMode -eq 'Anonymous') { 18 } else { 17 }
        if ($ShouldSucceed -and ($rejected -or -not $report.success -or
            $report.totalRequests -ne $expectedRequests -or $report.routes.Count -ne 3 -or
            $report.coldPageBase -lt 1 -or $report.coldPageBase -gt 49970 -or
            $report.schemaVersion -ne 2 -or $report.transportSession -ne 'reused' -or $report.error)) {
            throw 'Положительный performance contract нарушен.'
        }
        if ($ShouldSucceed -and @($report.routes | Where-Object {
            @($_.sampleLatenciesMs).Count -ne $_.samples
        }).Count -gt 0) {
            throw 'Performance report не сохранил все исходные latency samples.'
        }
        if ($ShouldSucceed -and $report.accessMode -ne $AccessMode) {
            throw "Performance report потерял режим доступа $AccessMode."
        }
        if (-not $ShouldSucceed -and (-not $rejected -or $report.success -or -not $report.error)) {
            throw "Отрицательный performance contract $Mode был принят."
        }
        return $report
    }
    finally {
        if ($job.State -eq 'Running') { Stop-Job $job }
        Remove-Job $job -Force
    }
}

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $success = Invoke-PerformanceCase success $true
    $anonymous = Invoke-PerformanceCase success $true Anonymous
    $slow = Invoke-PerformanceCase slow $false
    $failure = Invoke-PerformanceCase failure $false
    $rateLimit = Invoke-PerformanceCase rate-limit $false
    if ($slow.error -notmatch 'SLO' -or $failure.error -notmatch '503' -or
        $rateLimit.error -notmatch '429') {
        throw 'Performance negative contracts не сохранили точную причину отказа.'
    }
    $anonymousNames = @($anonymous.routes | ForEach-Object name)
    if ($anonymousNames -notcontains 'proxy-catalog-anonymous-hot' -or
        $anonymousNames -notcontains 'vpn-catalog-anonymous-hot' -or
        $anonymousNames -notcontains 'stats-hot') {
        throw 'Anonymous performance contract не проверяет фактические публичные каталоги.'
    }
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    foreach ($workflowPath in @('.github/workflows/ci.yml', '.github/workflows/release.yml')) {
        $workflow = Get-Content (Join-Path $repositoryRoot $workflowPath) -Raw
        if (-not $workflow.Contains('./tools/Test-AuditPublicApiPerformance.ps1', [StringComparison]::Ordinal)) {
            throw "$workflowPath не запускает performance audit contracts."
        }
    }
    Write-Host 'Public API performance contracts пройдены: metrics/report, configurable p95 SLO, HTTP/SLO rejection и CI/release wiring.' -ForegroundColor Green
}
finally {
    if ([IO.Directory]::Exists($fixtureRoot)) {
        foreach ($file in [IO.Directory]::EnumerateFiles($fixtureRoot, '*', [IO.SearchOption]::AllDirectories)) {
            [IO.File]::SetAttributes($file, [IO.FileAttributes]::Normal)
        }
        [IO.Directory]::Delete($fixtureRoot, $true)
    }
}
