param(
    [string]$ApiBaseUrl = 'http://localhost:8080',
    [Parameter(Mandatory)][string]$AdminKey,
    [string]$ReportPath,
    [ValidateRange(1, 10000)][int]$ExpectedBuiltInSources = 81,
    [ValidateRange(1, 10000)][int]$ExpectedProviders = 50,
    [switch]$SkipCollection
)

# Полный сбор обновляет LastItemCount/LastError каждого feed'а тем же кодом, который работает в production.
$headers = @{ 'X-Admin-Key' = $AdminKey }
$collection = $null
if (-not $SkipCollection) {
    $collection = Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl/api/v1/admin/collect" -Headers $headers
}
$sources = Invoke-RestMethod -Method Get -Uri "$ApiBaseUrl/api/v1/admin/sources" -Headers $headers
$ordered = @($sources | Sort-Object Priority)
$ordered | Select-Object Name, Provider, IsBuiltIn, DefaultProtocol, LastItemCount, LastResultTruncated, ConsecutiveFailures, LastFetchedAt, NextFetchAt, LastError | Format-Table -AutoSize

$failed = @($ordered | Where-Object { $_.Enabled -and ($_.LastItemCount -le 0 -or $_.LastError) })
$truncated = @($ordered | Where-Object { $_.Enabled -and $_.LastResultTruncated })
$candidateLimitReached = if ($null -eq $collection) { $null } else { [bool]$collection.CandidateLimitReached }
$builtIn = @($ordered | Where-Object IsBuiltIn)
$enabledBuiltIn = @($builtIn | Where-Object Enabled)
$providers = @($builtIn | ForEach-Object Provider | Where-Object { $_ } | Sort-Object -Unique)
$catalogErrors = [System.Collections.Generic.List[string]]::new()
if ($builtIn.Count -ne $ExpectedBuiltInSources) {
    $catalogErrors.Add("ожидалось $ExpectedBuiltInSources встроенных feed'ов, API вернул $($builtIn.Count)")
}
if ($enabledBuiltIn.Count -ne $ExpectedBuiltInSources) {
    $catalogErrors.Add("включено $($enabledBuiltIn.Count) из $ExpectedBuiltInSources встроенных feed'ов")
}
if ($providers.Count -ne $ExpectedProviders) {
    $catalogErrors.Add("ожидалось $ExpectedProviders независимых провайдеров, API вернул $($providers.Count)")
}
$report = [ordered]@{
    auditedAt = [DateTimeOffset]::UtcNow.ToString('O')
    apiBaseUrl = $ApiBaseUrl
    collectionTriggered = -not [bool]$SkipCollection
    total = $ordered.Count
    enabled = @($ordered | Where-Object Enabled).Count
    succeeded = @($ordered | Where-Object { $_.Enabled -and $_.LastItemCount -gt 0 -and -not $_.LastError }).Count
    failed = $failed.Count
    truncated = $truncated.Count
    candidateLimitReached = $candidateLimitReached
    parsedItems = ($ordered | Where-Object Enabled | Measure-Object -Property LastItemCount -Sum).Sum
    expectedBuiltInSources = $ExpectedBuiltInSources
    builtInSources = $builtIn.Count
    enabledBuiltInSources = $enabledBuiltIn.Count
    expectedProviders = $ExpectedProviders
    providers = $providers.Count
    catalogComplete = $catalogErrors.Count -eq 0
    catalogErrors = @($catalogErrors)
    sources = $ordered
}
if ($ReportPath) {
    $parent = Split-Path -Parent $ReportPath
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM
    Write-Host "JSON-отчёт сохранён: $ReportPath"
}
if ($failed.Count -gt 0 -or $truncated.Count -gt 0 -or $candidateLimitReached -eq $true -or $catalogErrors.Count -gt 0) {
    $catalogDetail = if ($catalogErrors.Count -gt 0) { " Нарушения каталога: $($catalogErrors -join '; ')." } else { '' }
    $limitDetail = if ($truncated.Count -gt 0 -or $candidateLimitReached) {
        " Достигнуты защитные лимиты: усечено feed'ов $($truncated.Count), общий лимит кандидатов: $candidateLimitReached."
    } else { '' }
    Write-Error "$($failed.Count) активных источников не прошли аудит.$limitDetail$catalogDetail Подробности показаны выше."
    exit 1
}

Write-Host "Аудит пройден без усечения: $($report.succeeded)/$($report.enabled) активных источников, $($report.builtInSources) встроенных feed'ов от $($report.providers) провайдеров вернули $($report.parsedItems) распознанных записей." -ForegroundColor Green
exit 0
