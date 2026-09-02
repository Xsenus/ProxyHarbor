param(
    [string]$ApiBaseUrl = 'http://localhost:8080',
    [Parameter(Mandatory)][string]$AdminKey,
    [string]$ReportPath,
    [ValidateRange(1, 50000)][int]$ExportLimit = 1000,
    [switch]$RequirePublishedRowsMatchBatch
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
    requirePublishedRowsMatchBatch = [bool]$RequirePublishedRowsMatchBatch
    publishedSetSha256 = $null
    error = $null
}

function Assert-OrderedProxyUrls([string]$Format, [object[]]$Expected, [object[]]$Actual) {
    if ($Actual.Count -ne $Expected.Count) {
        throw "Формат $Format вернул $($Actual.Count) строк вместо $($Expected.Count) Alive-прокси."
    }
    foreach ($index in 0..($Expected.Count - 1)) {
        if ([string]$Actual[$index] -cne [string]$Expected[$index]) {
            throw "Формат $Format расходится с JSON в строке $($index + 1)."
        }
    }
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
    if ($report.alive -eq 0) {
        throw 'Validation-аудит не нашёл ни одного публикуемого Alive-прокси.'
    }

    $diagnostics = Invoke-RestMethod -Method Get -Uri "$ApiBaseUrl/api/v1/admin/diagnostics" -Headers $headers
    $report.attemptsLastFiveMinutes = [int]$diagnostics.validationQueue.attemptsLastFiveMinutes
    if ($report.attemptsLastFiveMinutes -lt $report.attempts -or
        -not $diagnostics.validationQueue.lastAttemptAt) {
        throw 'Persisted validation telemetry не отражает только что завершённую партию.'
    }

    # Один и тот же текущий published-set должен без потерь разбираться во всех
    # форматах. Только чистая weekly-audit БД требует равенство размеру этой партии:
    # production уже содержит Alive-прокси из предыдущих validation run.
    $jsonResponse = Invoke-WebRequest -Uri "$ApiBaseUrl/api/v1/export/json?limit=$ExportLimit" -Headers $headers
    $jsonRows = @($jsonResponse.Content | ConvertFrom-Json)
    $report.jsonRows = $jsonRows.Count
    if ($jsonResponse.Headers.'Content-Type' -notlike 'application/json*') {
        throw 'JSON export вернул некорректный Content-Type.'
    }
    $jsonUrls = @($jsonRows | ForEach-Object { [string]$_.url })
    if (@($jsonUrls | Where-Object { $_ -notmatch '^(http|socks4|socks5)://.+:\d+$' }).Count -gt 0 -or
        @($jsonUrls | Sort-Object -Unique).Count -ne $jsonUrls.Count) {
        throw 'JSON export содержит пустые, некорректные или дублированные proxy URL.'
    }

    $xmlResponse = Invoke-WebRequest -Uri "$ApiBaseUrl/api/v1/export/xml?limit=$ExportLimit" -Headers $headers
    [xml]$xml = $xmlResponse.Content
    $xmlRows = @($xml.proxies.proxy | Where-Object { $null -ne $_ })
    $xmlUrls = @($xmlRows | ForEach-Object { [string]$_.url })
    $report.xmlRows = $xmlRows.Count
    if ($xml.DocumentElement.Name -ne 'proxies' -or $xmlResponse.Headers.'Content-Type' -notlike 'application/xml*') {
        throw 'XML export не является корректным документом proxies.'
    }

    $txtResponse = Invoke-WebRequest -Uri "$ApiBaseUrl/api/v1/export/txt?limit=$ExportLimit" -Headers $headers
    $txtRows = @($txtResponse.Content -split '\r?\n' | Where-Object { $_ })
    $report.txtRows = $txtRows.Count
    if ($txtResponse.Headers.'Content-Type' -notlike 'text/plain*' -or
        @($txtRows | Where-Object { $_ -notmatch '^(http|socks4|socks5)://.+:\d+$' }).Count -gt 0) {
        throw 'TXT export содержит некорректный Content-Type или proxy URL.'
    }

    $csvResponse = Invoke-WebRequest -Uri "$ApiBaseUrl/api/v1/export/csv?limit=$ExportLimit" -Headers $headers
    $csvRows = @($csvResponse.Content | ConvertFrom-Csv)
    $csvUrls = @($csvRows | ForEach-Object { [string]$_.url })
    $report.csvRows = $csvRows.Count
    if ($csvResponse.Headers.'Content-Type' -notlike 'text/csv*') {
        throw 'CSV export вернул некорректный Content-Type.'
    }

    if ($RequirePublishedRowsMatchBatch -and $jsonUrls.Count -ne $report.alive) {
        throw "JSON вернул $($jsonUrls.Count) строк вместо $($report.alive) Alive-прокси."
    }
    Assert-OrderedProxyUrls -Format 'XML' -Expected $jsonUrls -Actual $xmlUrls
    Assert-OrderedProxyUrls -Format 'TXT' -Expected $jsonUrls -Actual $txtRows
    Assert-OrderedProxyUrls -Format 'CSV' -Expected $jsonUrls -Actual $csvUrls
    $publishedBytes = [Text.Encoding]::UTF8.GetBytes(($jsonUrls -join "`n"))
    $report.publishedSetSha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($publishedBytes)).ToLowerInvariant()

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
