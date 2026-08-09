param(
    [string]$ApiBaseUrl = 'http://localhost:8080',
    [Parameter(Mandatory)][string]$AdminKey,
    [string]$ReportPath,
    [ValidateRange(1, 50000)][int]$ExportLimit = 1000
)

$ErrorActionPreference = 'Stop'
$report = [ordered]@{
    auditedAt = [DateTimeOffset]::UtcNow.ToString('O')
    apiBaseUrl = $ApiBaseUrl
    success = $false
    attempts = 0
    checked = 0
    alive = 0
    deferred = 0
    attemptsLastFiveMinutes = 0
    jsonRows = 0
    xmlRows = 0
    txtRows = 0
    csvRows = 0
    error = $null
}

try {
    # Ручной endpoint использует тот же lease/probe/persistence pipeline, что и
    # background worker, но позволяет аудиту дождаться точного результата партии.
    $headers = @{ 'X-Admin-Key' = $AdminKey }
    $validation = Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl/api/v1/admin/validate" -Headers $headers
    $report.checked = [int]$validation.checked
    $report.alive = [int]$validation.alive
    $report.deferred = [int]$validation.deferred
    $report.attempts = $report.checked + $report.deferred

    if ($report.attempts -le 0) { throw 'Validator не сохранил ни одной попытки.' }
    if ($report.alive -lt 0 -or $report.deferred -lt 0 -or
        $report.alive -gt $report.checked) {
        throw "Некорректные validation counters: checked=$($report.checked), alive=$($report.alive), deferred=$($report.deferred)."
    }

    $diagnostics = Invoke-RestMethod -Method Get -Uri "$ApiBaseUrl/api/v1/admin/diagnostics" -Headers $headers
    $report.attemptsLastFiveMinutes = [int]$diagnostics.validationQueue.attemptsLastFiveMinutes
    if ($report.attemptsLastFiveMinutes -lt $report.attempts -or
        -not $diagnostics.validationQueue.lastAttemptAt) {
        throw 'Persisted validation telemetry не отражает только что завершённую партию.'
    }

    # На чистой weekly-audit БД все опубликованные Alive появились в этой партии.
    # Один и тот же набор должен без потерь разбираться во всех публичных форматах.
    $jsonResponse = Invoke-WebRequest -Uri "$ApiBaseUrl/api/v1/export/json?limit=$ExportLimit"
    $jsonRows = @($jsonResponse.Content | ConvertFrom-Json)
    $report.jsonRows = $jsonRows.Count
    if ($jsonResponse.Headers.'Content-Type' -notlike 'application/json*') {
        throw 'JSON export вернул некорректный Content-Type.'
    }

    $xmlResponse = Invoke-WebRequest -Uri "$ApiBaseUrl/api/v1/export/xml?limit=$ExportLimit"
    [xml]$xml = $xmlResponse.Content
    $xmlRows = @($xml.proxies.proxy | Where-Object { $null -ne $_ })
    $report.xmlRows = $xmlRows.Count
    if ($xml.DocumentElement.Name -ne 'proxies' -or $xmlResponse.Headers.'Content-Type' -notlike 'application/xml*') {
        throw 'XML export не является корректным документом proxies.'
    }

    $txtResponse = Invoke-WebRequest -Uri "$ApiBaseUrl/api/v1/export/txt?limit=$ExportLimit"
    $txtRows = @($txtResponse.Content -split '\r?\n' | Where-Object { $_ })
    $report.txtRows = $txtRows.Count
    if ($txtResponse.Headers.'Content-Type' -notlike 'text/plain*' -or
        @($txtRows | Where-Object { $_ -notmatch '^(http|socks4|socks5)://.+:\d+$' }).Count -gt 0) {
        throw 'TXT export содержит некорректный Content-Type или proxy URL.'
    }

    $csvResponse = Invoke-WebRequest -Uri "$ApiBaseUrl/api/v1/export/csv?limit=$ExportLimit"
    $csvRows = @($csvResponse.Content | ConvertFrom-Csv)
    $report.csvRows = $csvRows.Count
    if ($csvResponse.Headers.'Content-Type' -notlike 'text/csv*') {
        throw 'CSV export вернул некорректный Content-Type.'
    }

    $expectedPublished = $report.alive
    foreach ($entry in @{
        json = $report.jsonRows
        xml = $report.xmlRows
        txt = $report.txtRows
        csv = $report.csvRows
    }.GetEnumerator()) {
        if ($entry.Value -ne $expectedPublished) {
            throw "Формат $($entry.Key) вернул $($entry.Value) строк вместо $expectedPublished Alive-прокси."
        }
    }

    $report.success = $true
    Write-Host "Validation-аудит пройден: $($report.attempts) попыток, $($report.checked) объективных результатов, $($report.alive) Alive, $($report.deferred) Deferred; JSON/XML/TXT/CSV согласованы." -ForegroundColor Green
} catch {
    $report.error = $_.Exception.Message
    throw
} finally {
    if ($ReportPath) {
        $parent = Split-Path -Parent $ReportPath
        if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM
        Write-Host "JSON-отчёт validation-аудита сохранён: $ReportPath"
    }
}
