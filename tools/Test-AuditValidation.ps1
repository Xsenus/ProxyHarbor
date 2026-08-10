$ErrorActionPreference = 'Stop'

# Контракт запускает настоящий Audit-Validation.ps1 через HTTP mock и доказывает,
# что weekly gate требует непустой и одинаковый ordered-set во всех форматах.
$auditScript = Join-Path $PSScriptRoot 'Audit-Validation.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-validation-audit-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-ValidationAuditMock([int]$Port, [ValidateSet('success', 'mismatch', 'zero-alive')][string]$Mode) {
    return Start-Job -ArgumentList $Port, $Mode -ScriptBlock {
        param($Port, $Mode)

        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        $handled = 0
        $expectedRequests = if ($Mode -eq 'zero-alive') { 1 } else { 6 }
        try {
            while ($handled -lt $expectedRequests) {
                $context = $listener.GetContext()
                $path = $context.Request.Url.AbsolutePath
                if ($path -eq '/ready') {
                    $contentType = 'application/json'
                    $body = '{}'
                } else {
                    $handled++
                    $contentType = 'application/json'
                    $body = switch ($path) {
                        '/api/v1/admin/validate' {
                            if ($Mode -eq 'zero-alive') {
                                '{"checked":2,"alive":0,"deferred":0}'
                            } else {
                                '{"checked":2,"alive":2,"deferred":0}'
                            }
                        }
                        '/api/v1/admin/diagnostics' {
                            '{"validationQueue":{"attemptsLastFiveMinutes":2,"lastAttemptAt":"2026-08-10T12:00:00Z"}}'
                        }
                        '/api/v1/export/json' {
                            '[{"url":"http://1.1.1.1:80"},{"url":"socks5://8.8.8.8:1080"}]'
                        }
                        '/api/v1/export/xml' {
                            $contentType = 'application/xml'
                            '<proxies><proxy><url>http://1.1.1.1:80</url></proxy><proxy><url>socks5://8.8.8.8:1080</url></proxy></proxies>'
                        }
                        '/api/v1/export/txt' {
                            $contentType = 'text/plain'
                            "http://1.1.1.1:80`nsocks5://8.8.8.8:1080`n"
                        }
                        '/api/v1/export/csv' {
                            $contentType = 'text/csv'
                            $second = if ($Mode -eq 'mismatch') { 'socks4://9.9.9.9:1080' } else { 'socks5://8.8.8.8:1080' }
                            "url`n`"http://1.1.1.1:80`"`n`"$second`"`n"
                        }
                        default { throw "Unexpected mock path: $path" }
                    }
                }

                $bytes = [Text.Encoding]::UTF8.GetBytes($body)
                $context.Response.StatusCode = 200
                $context.Response.ContentType = $contentType
                $context.Response.ContentLength64 = $bytes.Length
                $context.Response.OutputStream.Write($bytes)
                $context.Response.Close()
            }
        } finally {
            $listener.Stop()
            $listener.Close()
        }
    }
}

function Wait-ValidationAuditMock([int]$Port) {
    foreach ($attempt in 1..50) {
        try {
            Invoke-WebRequest -Uri "http://127.0.0.1:$Port/ready" -TimeoutSec 1 | Out-Null
            return
        } catch {
            if ($attempt -eq 50) { throw "Validation-audit mock на порту $Port не запустился." }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Invoke-ValidationAuditCase([string]$Mode, [bool]$ShouldSucceed) {
    $port = Get-FreeTcpPort
    $job = Start-ValidationAuditMock -Port $port -Mode $Mode
    $reportPath = Join-Path $testRoot "$Mode.json"
    try {
        Wait-ValidationAuditMock -Port $port
        $rejected = $false
        try {
            & $auditScript -ApiBaseUrl "http://127.0.0.1:$port" -AdminKey 'contract-key' -ReportPath $reportPath
        } catch {
            $rejected = $true
        }

        $completed = Wait-Job $job -Timeout 10
        if (-not $completed) { throw "Validation mock $Mode не завершил ожидаемые запросы." }
        Receive-Job $job -ErrorAction Stop | Out-Null
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "Validation audit $Mode не создал JSON-отчёт."
        }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json

        if ($ShouldSucceed) {
            if ($rejected -or -not $report.success -or $report.alive -ne 2 -or
                $report.jsonRows -ne 2 -or $report.xmlRows -ne 2 -or
                $report.txtRows -ne 2 -or $report.csvRows -ne 2 -or
                $report.publishedSetSha256 -notmatch '^[0-9a-f]{64}$' -or $report.error) {
                throw 'Положительный validation-audit контракт нарушен.'
            }
        } elseif (-not $rejected -or $report.success -or -not $report.error -or $report.publishedSetSha256) {
            throw "Отрицательный validation-audit контракт $Mode не был fail-closed отклонён."
        }
        return $report
    } finally {
        if ($job.State -eq 'Running') { Stop-Job $job }
        Remove-Job $job -Force
    }
}

try {
    $success = Invoke-ValidationAuditCase -Mode 'success' -ShouldSucceed $true
    $mismatch = Invoke-ValidationAuditCase -Mode 'mismatch' -ShouldSucceed $false
    $zeroAlive = Invoke-ValidationAuditCase -Mode 'zero-alive' -ShouldSucceed $false
    if ($mismatch.error -notmatch 'CSV.*JSON') {
        throw 'Mismatch-контракт не зафиксировал расхождение CSV с JSON.'
    }
    if ($zeroAlive.alive -ne 0 -or $zeroAlive.error -notmatch 'Alive') {
        throw 'Zero-Alive контракт не сохранил точную причину отказа.'
    }

    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $sourceWorkflow = Get-Content (Join-Path $repositoryRoot '.github/workflows/source-audit.yml') -Raw
    foreach ($fragment in @('futureEvidence', 'publishedSetSha256')) {
        if (-not $sourceWorkflow.Contains($fragment, [StringComparison]::Ordinal)) {
            throw "Source-audit summary не публикует обязательное поле $fragment."
        }
    }
    foreach ($workflowPath in @('.github/workflows/ci.yml', '.github/workflows/release.yml')) {
        $workflow = Get-Content (Join-Path $repositoryRoot $workflowPath) -Raw
        if (-not $workflow.Contains('./tools/Test-AuditValidation.ps1', [StringComparison]::Ordinal)) {
            throw "$workflowPath не запускает Test-AuditValidation.ps1."
        }
    }

    Write-Host 'Validation-audit contracts пройдены: non-empty ordered JSON/XML/TXT/CSV set, mismatch/zero-Alive rejection, summary и CI/release wiring.' -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
