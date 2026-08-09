param(
    [string]$ApiBaseUrl = 'http://localhost:8080',
    [Parameter(Mandatory)][string]$AdminKey,
    [string]$ReportPath
)

# Полный сбор обновляет LastItemCount/LastError каждого feed'а тем же кодом, который работает в production.
$headers = @{ 'X-Admin-Key' = $AdminKey }
Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl/api/v1/admin/collect" -Headers $headers | Out-Null
$sources = Invoke-RestMethod -Method Get -Uri "$ApiBaseUrl/api/v1/admin/sources" -Headers $headers
$ordered = @($sources | Sort-Object Priority)
$ordered | Select-Object Name, DefaultProtocol, LastItemCount, ConsecutiveFailures, LastFetchedAt, NextFetchAt, LastError | Format-Table -AutoSize

$failed = @($ordered | Where-Object { $_.Enabled -and ($_.LastItemCount -le 0 -or $_.LastError) })
$report = [ordered]@{
    auditedAt = [DateTimeOffset]::UtcNow.ToString('O')
    apiBaseUrl = $ApiBaseUrl
    total = $ordered.Count
    enabled = @($ordered | Where-Object Enabled).Count
    succeeded = @($ordered | Where-Object { $_.Enabled -and $_.LastItemCount -gt 0 -and -not $_.LastError }).Count
    failed = $failed.Count
    parsedItems = ($ordered | Where-Object Enabled | Measure-Object -Property LastItemCount -Sum).Sum
    sources = $ordered
}
if ($ReportPath) {
    $parent = Split-Path -Parent $ReportPath
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM
    Write-Host "JSON-отчёт сохранён: $ReportPath"
}
if ($failed.Count -gt 0) {
    Write-Error "$($failed.Count) активных источников не прошли аудит. Подробности показаны выше."
    exit 1
}

Write-Host "Аудит пройден: $($report.succeeded)/$($report.enabled) активных источников вернули $($report.parsedItems) распознанных записей." -ForegroundColor Green
