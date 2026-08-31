$ErrorActionPreference = 'Stop'
$auditScript = Join-Path $PSScriptRoot 'Audit-Backup.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-backup-audit-$([Guid]::NewGuid().ToString('N'))"

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-BackupAuditMock(
    [int]$Port,
    [ValidateSet('telegram', 'object-storage', 'local', 'mismatch', 'http-failure')][string]$Mode,
    [int]$ExpectedRequests) {
    Start-Job -ArgumentList $Port, $Mode, $ExpectedRequests -ScriptBlock {
        param($Port, $Mode, $ExpectedRequests)
        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        $handled = 0
        try {
            while ($handled -lt $ExpectedRequests) {
                $context = $listener.GetContext()
                if ($context.Request.Url.AbsolutePath -eq '/ready') {
                    $json = '{}'
                } elseif ($context.Request.Headers['X-Admin-Key'] -cne 'contract-admin-key') {
                    throw 'Backup-аудит не передал точный admin header.'
                } elseif ($Mode -eq 'http-failure') {
                    $handled++
                    $context.Response.StatusCode = 500
                    $json = '{"title":"simulated backup failure"}'
                } elseif ($context.Request.HttpMethod -eq 'POST' -and
                    $context.Request.Url.AbsolutePath -eq '/api/v1/admin/backup') {
                    $handled++
                    $sent = $Mode -in @('telegram', 'mismatch')
                    $json = '{"created":"proxyharbor-20260810-120000-0001.phbackup","sentToTelegram":' +
                        $sent.ToString().ToLowerInvariant() + '}'
                } else {
                    $handled++
                    $configured = $Mode -in @('telegram', 'mismatch')
                    $persistedSent = $Mode -eq 'telegram'
                    $objectConfigured = $Mode -eq 'object-storage'
                    $objectSent = $Mode -eq 'object-storage'
                    $objectKey = if ($objectSent) { 'production/backups/proxyharbor-20260810-120000-0001.phbackup' } else { $null }
                    $startedAt = [DateTimeOffset]::UtcNow.AddSeconds(-2).ToString('O')
                    $finishedAt = [DateTimeOffset]::UtcNow.ToString('O')
                    $json = '{"recentBackups":[{"startedAt":"' + $startedAt + '",' +
                        '"finishedAt":"' + $finishedAt + '","status":"completed",' +
                        '"fileName":"proxyharbor-20260810-120000-0001.phbackup","sizeBytes":4096,' +
                        '"telegramConfigured":' + $configured.ToString().ToLowerInvariant() +
                        ',"sentToTelegram":' + $persistedSent.ToString().ToLowerInvariant() +
                        ',"objectStorageConfigured":' + $objectConfigured.ToString().ToLowerInvariant() +
                        ',"sentToObjectStorage":' + $objectSent.ToString().ToLowerInvariant() +
                        ',"objectStorageKey":' + $(if ($objectKey) { '"' + $objectKey + '"' } else { 'null' }) + '}]}'
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

function Wait-BackupAuditMock([int]$Port) {
    foreach ($attempt in 1..50) {
        try {
            Invoke-WebRequest -Uri "http://127.0.0.1:$Port/ready" -TimeoutSec 1 | Out-Null
            return
        }
        catch {
            if ($attempt -eq 50) { throw "Backup-audit mock на порту $Port не запустился." }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Invoke-BackupAuditCase(
    [string]$Name,
    [string]$Mode,
    [bool]$ShouldSucceed,
    [switch]$AllowLocalOnly) {
    $port = Get-FreeTcpPort
    $expectedRequests = if ($Mode -eq 'http-failure') { 1 } else { 2 }
    $job = Start-BackupAuditMock -Port $port -Mode $Mode -ExpectedRequests $expectedRequests
    $reportPath = Join-Path $fixtureRoot "$Name.json"
    try {
        Wait-BackupAuditMock -Port $port
        $rejected = $false
        try {
            & $auditScript -ApiBaseUrl "http://127.0.0.1:$port" -AdminKey 'contract-admin-key' `
                -ReportPath $reportPath -AllowLocalOnly:$AllowLocalOnly *> $null
        }
        catch { $rejected = $true }
        $completed = Wait-Job -Job $job -Timeout 10
        if (-not $completed) { throw "Backup mock $Name не завершил ожидаемые запросы." }
        Receive-Job -Job $job -ErrorAction Stop | Out-Null
        if (-not [IO.File]::Exists($reportPath)) { throw "Case $Name не сохранил JSON-отчёт." }
        $reportJson = Get-Content -LiteralPath $reportPath -Raw
        if ($reportJson.Contains('contract-admin-key', [StringComparison]::Ordinal)) {
            throw "Case $Name сохранил admin secret в audit artifact."
        }
        $report = $reportJson | ConvertFrom-Json
        if ($ShouldSucceed -and ($rejected -or -not $report.success -or $report.error)) {
            throw "Положительный backup-аудит $Name был отклонён."
        }
        if (-not $ShouldSucceed -and (-not $rejected -or $report.success -or -not $report.error)) {
            throw "Отрицательный backup-аудит $Name был принят."
        }
        return $report
    }
    finally {
        if ($job.State -eq 'Running') { Stop-Job -Job $job }
        Remove-Job -Job $job -Force
    }
}

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $telegram = Invoke-BackupAuditCase -Name telegram -Mode telegram -ShouldSucceed $true
    $objectStorage = Invoke-BackupAuditCase -Name object-storage -Mode object-storage -ShouldSucceed $true
    $local = Invoke-BackupAuditCase -Name local -Mode local -ShouldSucceed $true -AllowLocalOnly
    $required = Invoke-BackupAuditCase -Name required -Mode local -ShouldSucceed $false
    $mismatch = Invoke-BackupAuditCase -Name mismatch -Mode mismatch -ShouldSucceed $false
    $failure = Invoke-BackupAuditCase -Name http-failure -Mode http-failure -ShouldSucceed $false
    if (-not $telegram.sentToTelegram -or $telegram.sizeBytes -ne 4096) {
        throw 'Telegram success contract потерял delivery/size evidence.'
    }
    if (-not $objectStorage.sentToObjectStorage -or -not $objectStorage.objectStorageKey) {
        throw 'S3 success contract потерял delivery/key evidence.'
    }
    if ($local.sentToTelegram -or $local.sentToObjectStorage -or $local.externalDeliveryRequired) {
        throw 'Local-only contract не отразил явное ослабление требования внешней доставки.'
    }
    if ($required.error -notmatch 'Telegram' -or $mismatch.error -notmatch 'persisted' -or
        $failure.error -notmatch '500') {
        throw 'Отрицательные backup-контракты не сохранили точную безопасную причину отказа.'
    }
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    foreach ($workflowPath in @('.github/workflows/ci.yml', '.github/workflows/release.yml')) {
        $workflow = Get-Content (Join-Path $repositoryRoot $workflowPath) -Raw
        if (-not $workflow.Contains('./tools/Test-AuditBackup.ps1', [StringComparison]::Ordinal)) {
            throw "$workflowPath не запускает Test-AuditBackup.ps1."
        }
    }
    Write-Host 'Backup-audit contracts пройдены: Telegram/S3/local success, secret-free reports, CI/release wiring и rejection missing delivery, audit mismatch, HTTP failure.' -ForegroundColor Green
}
finally {
    if ([IO.Directory]::Exists($fixtureRoot)) {
        foreach ($file in [IO.Directory]::EnumerateFiles($fixtureRoot, '*', [IO.SearchOption]::AllDirectories)) {
            [IO.File]::SetAttributes($file, [IO.FileAttributes]::Normal)
        }
        [IO.Directory]::Delete($fixtureRoot, $true)
    }
}
