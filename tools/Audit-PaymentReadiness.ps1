[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'https://proxy.blagodaty.ru',
    [Parameter(Mandatory)][string]$AdminKeyFile,
    [ValidateRange(3, 120)][int]$TimeoutSec = 20,
    [ValidateRange(0, 10000)][int]$MaxPendingOrders = 0,
    [string]$ReportPath,
    [switch]$AllowHttp,
    [switch]$AllowAwaitingFirstPayment
)

$ErrorActionPreference = 'Stop'
$apiUri = $null
if (-not [Uri]::TryCreate($ApiBaseUrl, [UriKind]::Absolute, [ref]$apiUri) -or
    $apiUri.Scheme -notin @('http', 'https') -or $apiUri.Query -or $apiUri.Fragment) {
    throw 'ApiBaseUrl должен быть абсолютным HTTP(S) URL без query и fragment.'
}
if ($apiUri.Scheme -ne 'https' -and -not $AllowHttp) {
    throw 'Production payment-аудит требует HTTPS. -AllowHttp предназначен только для локального contract-теста.'
}
if (-not [IO.File]::Exists($AdminKeyFile)) {
    throw "Файл административного ключа не найден: $AdminKeyFile"
}
$adminKey = (Get-Content -LiteralPath $AdminKeyFile -Raw).Trim()
$hasControlCharacter = $false
for ($index = 0; $index -lt $adminKey.Length; $index++) {
    if ([char]::IsControl($adminKey[$index])) { $hasControlCharacter = $true; break }
}
if ($adminKey.Length -lt 24 -or $adminKey.Length -gt 4096 -or $hasControlCharacter) {
    throw 'Административный ключ должен содержать 24-4096 печатных символов.'
}

$base = $apiUri.AbsoluteUri.TrimEnd('/')
$report = [ordered]@{
    auditedAt = [DateTimeOffset]::UtcNow.ToString('O')
    apiBaseUrl = $base
    success = $false
    globalPaymentsEnabled = $false
    enabledProducts = 0
    enabledProviders = 0
    maxPendingOrders = $MaxPendingOrders
    allowAwaitingFirstPayment = [bool]$AllowAwaitingFirstPayment
    providers = @()
    failures = @()
    error = $null
}

try {
    $response = Invoke-WebRequest -Uri "$base/api/v1/admin/payments" -TimeoutSec $TimeoutSec -NoProxy `
        -Headers @{ 'X-Admin-Key' = $adminKey; 'User-Agent' = 'ProxyHarbor-Payment-Readiness-Audit/1.0' }
    if ($response.StatusCode -ne 200 -or [string]$response.Headers.'Content-Type' -notlike 'application/json*') {
        throw "Payment settings endpoint вернул HTTP $($response.StatusCode) / $($response.Headers.'Content-Type')."
    }
    try { $settings = [string]$response.Content | ConvertFrom-Json }
    catch { throw "Payment settings endpoint вернул некорректный JSON: $($_.Exception.Message)" }

    $report.globalPaymentsEnabled = [bool]$settings.enabled
    $products = @($settings.products | Where-Object enabled)
    $providers = @($settings.providers | Where-Object enabled)
    $report.enabledProducts = $products.Count
    $report.enabledProviders = $providers.Count
    $failures = [Collections.Generic.List[string]]::new()
    if (-not $report.globalPaymentsEnabled) { $failures.Add('Новые платежи глобально выключены.') }
    if ($products.Count -eq 0) { $failures.Add('Нет включённых тарифов.') }
    if ($providers.Count -eq 0) { $failures.Add('Нет включённых внешних платёжных шлюзов.') }

    $providerReports = foreach ($provider in $providers) {
        $issues = [Collections.Generic.List[string]]::new()
        if (-not [bool]$provider.ready) { $issues.Add('обязательные реквизиты не заполнены') }
        if ([bool]$provider.testMode) { $issues.Add('включён тестовый режим') }

        $webhook = $null
        if (-not [Uri]::TryCreate([string]$provider.webhookUrl, [UriKind]::Absolute, [ref]$webhook) -or
            ($webhook.Scheme -ne 'https' -and -not $AllowHttp) -or
            -not [string]::Equals($webhook.Host, $apiUri.Host, [StringComparison]::OrdinalIgnoreCase)) {
            $issues.Add('webhook URL не является HTTPS URL текущего production-host')
        }

        $operational = $provider.operational
        $state = [string]$operational.state
        $allowedStates = if ($AllowAwaitingFirstPayment) { @('healthy', 'awaiting_first_payment') } else { @('healthy') }
        if ($state -notin $allowedStates) { $issues.Add("эксплуатационное состояние: $state") }
        $pending = [int]$operational.pendingOrders
        if ($pending -gt $MaxPendingOrders) { $issues.Add("pending-заказов $pending, разрешено $MaxPendingOrders") }
        if ($issues.Count -gt 0) { $failures.Add("$($provider.code): $($issues -join '; ')") }

        [ordered]@{
            code = [string]$provider.code
            name = [string]$provider.name
            ready = [bool]$provider.ready
            testMode = [bool]$provider.testMode
            webhookUrl = [string]$provider.webhookUrl
            state = $state
            totalOrders = [int]$operational.totalOrders
            pendingOrders = $pending
            paidOrders = [int]$operational.paidOrders
            paidAfterConfigurationUpdate = [int]$operational.paidAfterConfigurationUpdate
            failedOrders = [int]$operational.failedOrders
            canceledOrders = [int]$operational.canceledOrders
            refundedOrders = [int]$operational.refundedOrders
            lastOrderAt = $operational.lastOrderAt
            lastPaidAt = $operational.lastPaidAt
            directReconciliationSupported = [bool]$operational.directReconciliationSupported
            success = $issues.Count -eq 0
            issues = @($issues)
        }
    }

    $report.providers = @($providerReports)
    $report.failures = @($failures)
    $report.success = $failures.Count -eq 0
    if (-not $report.success) {
        throw "Payment readiness-аудит не пройден: $($failures -join ' | ')"
    }
    Write-Host "Payment readiness-аудит пройден: тарифов $($products.Count), шлюзов $($providers.Count), каждый шлюз подтверждён оплатой после последнего сохранения настроек." -ForegroundColor Green
}
catch {
    $report.error = $_.Exception.Message
    throw
}
finally {
    $adminKey = $null
    $hasControlCharacter = $null
    if ($ReportPath) {
        $absoluteReportPath = if ([IO.Path]::IsPathRooted($ReportPath)) {
            [IO.Path]::GetFullPath($ReportPath)
        } else { [IO.Path]::GetFullPath((Join-Path (Get-Location) $ReportPath)) }
        $directory = [IO.Path]::GetDirectoryName($absoluteReportPath)
        if ($directory) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
        $temporaryReport = "$absoluteReportPath.partial"
        try {
            [IO.File]::WriteAllText($temporaryReport, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
            [IO.File]::Move($temporaryReport, $absoluteReportPath, $true)
            Write-Host "JSON-отчёт payment readiness-аудита сохранён: $absoluteReportPath"
        }
        finally { if ([IO.File]::Exists($temporaryReport)) { [IO.File]::Delete($temporaryReport) } }
    }
}
