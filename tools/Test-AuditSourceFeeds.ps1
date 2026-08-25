$ErrorActionPreference = 'Stop'

# Контрактный тест запускает настоящий Audit-SourceFeeds.ps1 против локального
# HTTP mock и не зависит от Docker, PostgreSQL или внешних proxy-feed'ов.
$auditScript = Join-Path $PSScriptRoot 'Audit-SourceFeeds.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-source-audit-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-SourceAuditMock([int]$Port, [ValidateSet('success', 'saved-state', 'stale', 'future', 'missing-content', 'identity-missing', 'zero-candidates', 'skipped', 'http-failure')][string]$Mode) {
    # Start-Job используется намеренно: вызываемый audit остаётся отдельным обычным
    # PowerShell script и проходит тот же HTTP/JSON путь, что в GitHub Actions.
    return Start-Job -ArgumentList $Port, $Mode -ScriptBlock {
        param($Port, $Mode)

        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        $handled = 0
        $expectedRequests = if ($Mode -in @('http-failure', 'saved-state')) { 1 } else { 2 }
        try {
            while ($handled -lt $expectedRequests) {
                $context = $listener.GetContext()
                if ($context.Request.Url.AbsolutePath -eq '/ready') {
                    $status = 200
                    $json = '{}'
                } elseif ($Mode -eq 'http-failure') {
                    $handled++
                    $status = 500
                    $json = '{"error":"collection failed"}'
                } elseif ($context.Request.HttpMethod -eq 'POST') {
                    $handled++
                    $status = 200
                    $candidatesFound = if ($Mode -eq 'zero-candidates') { 0 } else { 7 }
                    $newProxies = if ($Mode -eq 'zero-candidates') { 0 } else { 5 }
                    $sourcesSkipped = if ($Mode -eq 'skipped') { 1 } else { 0 }
                    $json = '{"id":"11111111-1111-1111-1111-111111111111","startedAt":"2026-08-09T12:00:00Z","finishedAt":"2026-08-09T12:00:05Z","sourcesProcessed":1,"sourcesSucceeded":1,"sourcesFailed":0,"sourcesSkipped":' + $sourcesSkipped + ',"sourcesTruncated":0,"candidatesFound":' + $candidatesFound + ',"newProxies":' + $newProxies + ',"candidateLimitReached":false,"status":"completed"}'
                } else {
                    $handled++
                    $status = 200
                    $fetchedAt = if ($Mode -eq 'stale') {
                        '2026-08-09T11:59:59Z'
                    } elseif ($Mode -eq 'future') {
                        '2026-08-09T12:00:06Z'
                    } else {
                        '2026-08-09T12:00:01Z'
                    }
                    $contentFetchedAt = if ($Mode -eq 'missing-content') {
                        '2026-08-09T11:00:00Z'
                    } else { $fetchedAt }
                    $identity = if ($Mode -eq 'identity-missing') { '' } else { ',"providerIdentity":"host:mock.example"' }
                    $item = '{"id":"22222222-2222-2222-2222-222222222222","name":"Mock feed","provider":"Mock provider"' + $identity + ',"isBuiltIn":true,"enabled":true,"priority":1,"defaultProtocol":"Http","lastItemCount":10,"lastResultTruncated":false,"consecutiveFailures":0,"lastFetchedAt":"' + $fetchedAt + '","lastContentFetchedAt":"' + $contentFetchedAt + '","lastError":null}'
                    $json = '{"items":[' + $item + '],"page":1,"pageSize":100,"total":1}'
                }

                $bytes = [Text.Encoding]::UTF8.GetBytes($json)
                $context.Response.StatusCode = $status
                $context.Response.ContentType = 'application/json'
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

function Wait-SourceAuditMock([int]$Port) {
    foreach ($attempt in 1..50) {
        try {
            Invoke-WebRequest -Uri "http://127.0.0.1:$Port/ready" -TimeoutSec 1 | Out-Null
            return
        } catch {
            if ($attempt -eq 50) { throw "Source-audit mock на порту $Port не запустился." }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Invoke-SourceAuditCase(
    [string]$Name,
    [string]$Mode,
    [bool]$ShouldSucceed,
    [bool]$SkipCollection = $false
) {
    $port = Get-FreeTcpPort
    $job = Start-SourceAuditMock -Port $port -Mode $Mode
    $reportPath = Join-Path $testRoot "$Name.json"
    try {
        Wait-SourceAuditMock -Port $port
        $rejected = $false
        try {
            $auditParameters = @{
                ApiBaseUrl = "http://127.0.0.1:$port"
                AdminKey = 'contract-test-key'
                ExpectedBuiltInSources = 1
                ExpectedProviders = 1
                ReportPath = $reportPath
                SkipCollection = $SkipCollection
            }
            & $auditScript @auditParameters
        } catch {
            $rejected = $true
        }

        $completed = Wait-Job $job -Timeout 10
        if (-not $completed) { throw "Mock $Name не завершил ожидаемые HTTP-запросы." }
        Receive-Job $job -ErrorAction Stop | Out-Null
        if (-not (Test-Path -LiteralPath $reportPath)) { throw "Audit $Name не создал обязательный JSON-отчёт." }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json

        if ($ShouldSucceed) {
            $collectionEvidenceValid = if ($SkipCollection) {
                -not $report.collectionTriggered -and -not $report.collectionId -and
                    $null -eq $report.collectionCountersMatch
            } else {
                $report.collectionTriggered -and $report.collectionId -and $report.collectionCountersMatch -and
                    $report.collectionDurationMs -eq 5000 -and $report.sourcesProcessed -eq 1 -and
                    $report.sourcesSkipped -eq 0 -and $report.candidatesFound -eq 7 -and
                    $report.newProxies -eq 5
            }
            if ($rejected -or -not $report.success -or -not $collectionEvidenceValid -or
                $report.staleEvidence -ne 0 -or $report.futureEvidence -ne 0 -or
                (-not $SkipCollection -and $report.contentEvidenceErrors -ne 0) -or $report.error) {
                throw "Положительный контракт source-аудита $Name нарушен."
            }
        } elseif (-not $rejected -or $report.success -or -not $report.error) {
            throw "Отрицательный контракт source-аудита $Name не был отклонён."
        }

        return $report
    } finally {
        if ($job.State -eq 'Running') { Stop-Job $job }
        Remove-Job $job -Force
    }
}

try {
    $success = Invoke-SourceAuditCase -Name 'success' -Mode 'success' -ShouldSucceed $true
    $savedState = Invoke-SourceAuditCase -Name 'saved-state' -Mode 'saved-state' -ShouldSucceed $true -SkipCollection $true
    $stale = Invoke-SourceAuditCase -Name 'stale' -Mode 'stale' -ShouldSucceed $false
    $future = Invoke-SourceAuditCase -Name 'future' -Mode 'future' -ShouldSucceed $false
    $missingContent = Invoke-SourceAuditCase -Name 'missing-content' -Mode 'missing-content' -ShouldSucceed $false
    $identityMissing = Invoke-SourceAuditCase -Name 'identity-missing' -Mode 'identity-missing' -ShouldSucceed $false
    $zeroCandidates = Invoke-SourceAuditCase -Name 'zero-candidates' -Mode 'zero-candidates' -ShouldSucceed $false
    $skipped = Invoke-SourceAuditCase -Name 'skipped' -Mode 'skipped' -ShouldSucceed $false
    $httpFailure = Invoke-SourceAuditCase -Name 'http-failure' -Mode 'http-failure' -ShouldSucceed $false
    if ($stale.staleEvidence -ne 1) { throw 'Stale-контракт не указал ровно один устаревший feed.' }
    if ($future.futureEvidence -ne 1) { throw 'Future-контракт не указал ровно один feed за границей run window.' }
    if ($missingContent.contentEvidenceErrors -ne 1) {
        throw 'Forced-аудит не отклонил feed без свежего body/parser evidence.'
    }
    if ($identityMissing.providers -ne 0 -or $identityMissing.catalogComplete) {
        throw 'Отсутствующая origin identity не должна засчитываться как независимый provider.'
    }
    if ($httpFailure.collectionId) { throw 'Ранний HTTP-сбой не должен выглядеть как запущенный collection-run.' }
    if ($zeroCandidates.candidatesFound -ne 0 -or $zeroCandidates.collectionCountersMatch) {
        throw 'Пустой набор уникальных кандидатов не должен проходить полный source-аудит.'
    }
    if ($skipped.sourcesSkipped -ne 1 -or $skipped.collectionCountersMatch) {
        throw 'Forced source-аудит не должен принимать пропущенный feed.'
    }
    Write-Host 'Source-audit contracts пройдены: counters/fetch+content window, saved-state, stale/future/304/identity/empty/skip rejection и HTTP failure report.' -ForegroundColor Green
} finally {
    # Каталог создаётся под системным temp с непредсказуемым GUID и удаляется
    # только по точному пути, сформированному этим тестом.
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
