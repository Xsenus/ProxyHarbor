param(
    [string]$ApiBaseUrl = 'http://localhost:8080',
    [Parameter(Mandatory)][string]$AdminKey,
    [string]$ReportPath,
    [ValidateRange(1, 10000)][int]$ExpectedBuiltInSources = 81,
    [ValidateRange(1, 10000)][int]$ExpectedProviders = 50,
    [switch]$SkipCollection
)

$ErrorActionPreference = 'Stop'
$report = [ordered]@{
    auditedAt = [DateTimeOffset]::UtcNow.ToString('O')
    apiBaseUrl = $ApiBaseUrl
    collectionTriggered = -not [bool]$SkipCollection
    success = $false
    collectionId = $null
    collectionStartedAt = $null
    collectionFinishedAt = $null
    collectionDurationMs = $null
    collectionStatus = $null
    collectionCountersMatch = $null
    sourcesProcessed = $null
    sourcesSkipped = $null
    candidatesFound = $null
    newProxies = $null
    total = 0
    enabled = 0
    succeeded = 0
    failed = 0
    staleEvidence = 0
    truncated = 0
    candidateLimitReached = $null
    parsedItems = 0
    expectedBuiltInSources = $ExpectedBuiltInSources
    builtInSources = 0
    enabledBuiltInSources = 0
    expectedProviders = $ExpectedProviders
    providers = 0
    catalogComplete = $false
    catalogErrors = @()
    error = $null
    sources = @()
}

try {
    # Полный сбор обновляет LastItemCount/LastError каждого feed'а тем же кодом,
    # который работает в production. Любая HTTP/JSON-ошибка немедленно прерывает аудит.
    $headers = @{ 'X-Admin-Key' = $AdminKey }
    $collection = $null
    if (-not $SkipCollection) {
        $collection = Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl/api/v1/admin/collect" -Headers $headers
        if (-not $collection -or $collection.Status -ne 'completed' -or -not $collection.StartedAt -or -not $collection.FinishedAt) {
            throw 'Admin collection не вернул завершённый аудит с временными границами.'
        }
        $report.collectionId = $collection.Id
        $report.collectionStartedAt = ([DateTimeOffset]$collection.StartedAt).ToString('O')
        $report.collectionFinishedAt = ([DateTimeOffset]$collection.FinishedAt).ToString('O')
        $report.collectionDurationMs = [long]([DateTimeOffset]$collection.FinishedAt -
            [DateTimeOffset]$collection.StartedAt).TotalMilliseconds
        $report.collectionStatus = $collection.Status
        $report.sourcesProcessed = [int]$collection.SourcesProcessed
        $report.sourcesSkipped = [int]$collection.SourcesSkipped
        $report.candidatesFound = [int]$collection.CandidatesFound
        $report.newProxies = [int]$collection.NewProxies
        $report.candidateLimitReached = [bool]$collection.CandidateLimitReached
    }

    $sources = Invoke-RestMethod -Method Get -Uri "$ApiBaseUrl/api/v1/admin/sources" -Headers $headers
    $ordered = @($sources | Sort-Object Priority)
    $enabled = @($ordered | Where-Object Enabled)
    $failed = @($enabled | Where-Object { $_.LastItemCount -le 0 -or $_.LastError })
    $truncated = @($enabled | Where-Object LastResultTruncated)
    $builtIn = @($ordered | Where-Object IsBuiltIn)
    $enabledBuiltIn = @($builtIn | Where-Object Enabled)
    # Display-name не доказывает независимость: completeness считается по канонической
    # identity владельца (GitHub owner либо отдельный DNS hostname), отданной API.
    $providers = @($builtIn | ForEach-Object ProviderIdentity | Where-Object { $_ } | Sort-Object -Unique)
    $staleEvidence = @()
    if ($collection) {
        $collectionStartedAt = [DateTimeOffset]$collection.StartedAt
        $staleEvidence = @($enabled | Where-Object {
            -not $_.LastFetchedAt -or [DateTimeOffset]$_.LastFetchedAt -lt $collectionStartedAt
        })
    }

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

    $countersMatch = $null
    if ($collection) {
        $countersMatch = [int]$collection.SourcesProcessed -eq $enabled.Count -and
            [int]$collection.SourcesSucceeded -eq ($enabled.Count - $failed.Count) -and
            [int]$collection.SourcesFailed -eq $failed.Count -and
            [int]$collection.SourcesTruncated -eq $truncated.Count -and
            [int]$collection.SourcesSkipped -eq 0 -and
            [int]$collection.CandidatesFound -gt 0 -and
            [int]$collection.NewProxies -ge 0 -and
            [int]$collection.NewProxies -le [int]$collection.CandidatesFound -and
            $report.collectionDurationMs -ge 0
    }

    $report.collectionCountersMatch = $countersMatch
    $report.total = $ordered.Count
    $report.enabled = $enabled.Count
    $report.succeeded = $enabled.Count - $failed.Count
    $report.failed = $failed.Count
    $report.staleEvidence = $staleEvidence.Count
    $report.truncated = $truncated.Count
    $report.parsedItems = ($enabled | Measure-Object -Property LastItemCount -Sum).Sum
    $report.builtInSources = $builtIn.Count
    $report.enabledBuiltInSources = $enabledBuiltIn.Count
    $report.providers = $providers.Count
    $report.catalogComplete = $catalogErrors.Count -eq 0
    $report.catalogErrors = @($catalogErrors)
    $report.sources = $ordered

    $ordered | Select-Object Name, Provider, ProviderIdentity, IsBuiltIn, DefaultProtocol, LastItemCount, LastResultTruncated, ConsecutiveFailures, LastFetchedAt, NextFetchAt, LastError | Format-Table -AutoSize

    if ($failed.Count -gt 0 -or $staleEvidence.Count -gt 0 -or $truncated.Count -gt 0 -or
        $report.candidateLimitReached -eq $true -or $catalogErrors.Count -gt 0 -or $countersMatch -eq $false) {
        throw "Source-аудит недостоверен или неполон: failed=$($failed.Count), stale=$($staleEvidence.Count), truncated=$($truncated.Count), skipped=$($report.sourcesSkipped), candidates=$($report.candidatesFound), candidateLimit=$($report.candidateLimitReached), countersMatch=$countersMatch, catalogErrors=$($catalogErrors.Count)."
    }

    $report.success = $true
    $collectionSummary = if ($collection) {
        ", $($report.candidatesFound) уникальных кандидатов за $($report.collectionDurationMs) мс"
    } else { '' }
    Write-Host "Аудит пройден без усечения: $($report.succeeded)/$($report.enabled) активных источников, $($report.builtInSources) встроенных feed'ов от $($report.providers) провайдеров вернули $($report.parsedItems) распознанных записей$collectionSummary." -ForegroundColor Green
} catch {
    $report.error = $_.Exception.Message
    throw
} finally {
    if ($ReportPath) {
        $parent = Split-Path -Parent $ReportPath
        if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM
        Write-Host "JSON-отчёт source-аудита сохранён: $ReportPath"
    }
}
