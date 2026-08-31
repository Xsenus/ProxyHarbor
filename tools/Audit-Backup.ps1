[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://localhost:8080',
    [Parameter(Mandatory)][string]$AdminKey,
    [string]$ReportPath,
    [switch]$AllowLocalOnly
)

$ErrorActionPreference = 'Stop'
$requestedAt = [DateTimeOffset]::UtcNow
$report = [ordered]@{
    auditedAt = $requestedAt.ToString('O')
    apiBaseUrl = $ApiBaseUrl
    externalDeliveryRequired = -not $AllowLocalOnly
    success = $false
    created = $null
    sizeBytes = 0
    startedAt = $null
    finishedAt = $null
    telegramConfigured = $false
    sentToTelegram = $false
    objectStorageConfigured = $false
    sentToObjectStorage = $false
    objectStorageKey = $null
    error = $null
}

try {
    $headers = @{ 'X-Admin-Key' = $AdminKey }
    $trigger = Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl/api/v1/admin/backup" -Headers $headers
    $report.created = [string]$trigger.created
    $report.sentToTelegram = [bool]$trigger.sentToTelegram

    if ($report.created -notmatch '^proxyharbor-\d{8}-\d{6}-\d{4}\.phbackup$') {
        throw 'Backup endpoint не вернул каноническое имя опубликованного PHB3-файла.'
    }
    $diagnostics = Invoke-RestMethod -Method Get -Uri "$ApiBaseUrl/api/v1/admin/diagnostics" -Headers $headers
    $matching = @($diagnostics.recentBackups | Where-Object {
        [string]$_.fileName -ceq $report.created
    })
    if ($matching.Count -ne 1) {
        throw "Diagnostics не содержит ровно один audit созданного файла $($report.created)."
    }

    $audit = $matching[0]
    $report.sizeBytes = [long]$audit.sizeBytes
    $report.startedAt = [string]$audit.startedAt
    $report.finishedAt = [string]$audit.finishedAt
    $report.telegramConfigured = [bool]$audit.telegramConfigured
    $report.objectStorageConfigured = [bool]$audit.objectStorageConfigured
    $report.sentToObjectStorage = [bool]$audit.sentToObjectStorage
    $report.objectStorageKey = [string]$audit.objectStorageKey

    $startedAt = [DateTimeOffset]$audit.startedAt
    $finishedAt = [DateTimeOffset]$audit.finishedAt
    if ([string]$audit.status -cne 'completed' -or $report.sizeBytes -le 0 -or
        -not $audit.finishedAt -or $finishedAt -lt $startedAt -or
        $finishedAt -lt $requestedAt.AddMinutes(-1)) {
        throw 'Backup audit не доказывает завершённый непустой снимок текущего запуска.'
    }
    if ([bool]$audit.sentToTelegram -ne $report.sentToTelegram -or
        $report.telegramConfigured -ne $report.sentToTelegram) {
        throw 'Ответ backup endpoint расходится с persisted Telegram delivery audit.'
    }
    if ($report.sentToObjectStorage -and
        (-not $report.objectStorageConfigured -or [string]::IsNullOrWhiteSpace($report.objectStorageKey))) {
        throw 'Persisted S3 delivery audit не содержит configured/key evidence.'
    }
    if (-not $AllowLocalOnly -and -not $report.sentToTelegram -and -not $report.sentToObjectStorage) {
        throw 'Backup создан локально, но ни Telegram, ни S3-доставка не подтверждены.'
    }

    $report.success = $true
    Write-Host ("Backup-аудит пройден: {0}, {1} байт, Telegram={2}, S3={3}." -f
        $report.created, $report.sizeBytes, $report.sentToTelegram,
        $report.sentToObjectStorage) -ForegroundColor Green
}
catch {
    $report.error = $_.Exception.Message
    throw
}
finally {
    if ($ReportPath) {
        $absoluteReportPath = if ([IO.Path]::IsPathRooted($ReportPath)) {
            [IO.Path]::GetFullPath($ReportPath)
        } else {
            [IO.Path]::GetFullPath((Join-Path (Get-Location) $ReportPath))
        }
        $directory = [IO.Path]::GetDirectoryName($absoluteReportPath)
        if ($directory) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
        $temporaryReport = "$absoluteReportPath.partial"
        try {
            [IO.File]::WriteAllText(
                $temporaryReport,
                ($report | ConvertTo-Json -Depth 8),
                [Text.UTF8Encoding]::new($false))
            [IO.File]::Move($temporaryReport, $absoluteReportPath, $true)
            Write-Host "JSON-отчёт backup-аудита сохранён: $absoluteReportPath"
        }
        finally {
            if ([IO.File]::Exists($temporaryReport)) { [IO.File]::Delete($temporaryReport) }
        }
    }
}
