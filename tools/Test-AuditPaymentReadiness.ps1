$ErrorActionPreference = 'Stop'
$auditScript = Join-Path $PSScriptRoot 'Audit-PaymentReadiness.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-payment-readiness-$([Guid]::NewGuid().ToString('N'))"
$jobs = [Collections.Generic.List[object]]::new()

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-PaymentMock([int]$Port, [bool]$Healthy) {
    $job = Start-Job -ArgumentList $Port, $Healthy -ScriptBlock {
        param($Port, $Healthy)
        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        try {
            $context = $listener.GetContext()
            if ($context.Request.Url.AbsolutePath -ne '/api/v1/admin/payments' -or
                $context.Request.Headers['X-Admin-Key'] -ne 'contract-admin-key-at-least-24-chars') {
                $context.Response.StatusCode = 401
                $body = '{}'
            }
            else {
                $state = if ($Healthy) { 'healthy' } else { 'webhook_attention' }
                $paid = if ($Healthy) { 1 } else { 0 }
                $canceled = if ($Healthy) { 0 } else { 4 }
                $body = @{
                    enabled = $true
                    products = @(@{ code = 'unlimited-day'; enabled = $true })
                    providers = @(@{
                        code = 'yoomoney'; name = 'ЮMoney'; enabled = $true; ready = $true; testMode = $false
                        webhookUrl = "http://127.0.0.1:$Port/api/v1/payments/webhooks/yoomoney"
                        operational = @{
                            state = $state; totalOrders = 4; pendingOrders = 0; paidOrders = $paid
                            paidAfterConfigurationUpdate = $paid
                            failedOrders = 0; canceledOrders = $canceled; refundedOrders = 0
                            lastOrderAt = '2026-09-01T00:00:00Z'; lastPaidAt = if ($Healthy) { '2026-09-01T00:01:00Z' } else { $null }
                            directReconciliationSupported = $false
                        }
                    })
                } | ConvertTo-Json -Depth 8 -Compress
                $context.Response.StatusCode = 200
                $context.Response.ContentType = 'application/json; charset=utf-8'
            }
            $bytes = [Text.Encoding]::UTF8.GetBytes($body)
            $context.Response.ContentLength64 = $bytes.Length
            $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            $context.Response.Close()
        }
        finally { $listener.Stop(); $listener.Close() }
    }
    $jobs.Add($job)
    return $job
}

function Wait-PaymentMock([int]$Port) {
    foreach ($attempt in 1..50) {
        try {
            $client = [Net.Sockets.TcpClient]::new()
            try { $client.Connect('127.0.0.1', $Port); return }
            finally { $client.Dispose() }
        }
        catch {
            if ($attempt -eq 50) { throw "Payment mock on port $Port did not start." }
            Start-Sleep -Milliseconds 100
        }
    }
}

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $keyFile = Join-Path $fixtureRoot 'admin-key'
    [IO.File]::WriteAllText($keyFile, 'contract-admin-key-at-least-24-chars', [Text.UTF8Encoding]::new($false))

    $healthyPort = Get-FreeTcpPort
    $healthyJob = Start-PaymentMock -Port $healthyPort -Healthy $true
    Wait-PaymentMock $healthyPort
    $healthyReport = Join-Path $fixtureRoot 'healthy.json'
    & $auditScript -ApiBaseUrl "http://127.0.0.1:$healthyPort" -AdminKeyFile $keyFile -AllowHttp -ReportPath $healthyReport *> $null
    if (-not (Wait-Job $healthyJob -Timeout 10)) { throw 'Healthy payment mock did not receive the request.' }
    Receive-Job $healthyJob -ErrorAction Stop | Out-Null
    $success = Get-Content -LiteralPath $healthyReport -Raw | ConvertFrom-Json
    if (-not $success.success -or $success.enabledProviders -ne 1 -or
        $success.providers[0].paidAfterConfigurationUpdate -ne 1) {
        throw 'Healthy payment readiness report is invalid.'
    }
    if ((Get-Content -LiteralPath $healthyReport -Raw).Contains('contract-admin-key', [StringComparison]::Ordinal)) {
        throw 'Payment readiness report leaked the admin key.'
    }

    $attentionPort = Get-FreeTcpPort
    $attentionJob = Start-PaymentMock -Port $attentionPort -Healthy $false
    Wait-PaymentMock $attentionPort
    $attentionReport = Join-Path $fixtureRoot 'attention.json'
    $rejected = $false
    try {
        & $auditScript -ApiBaseUrl "http://127.0.0.1:$attentionPort" -AdminKeyFile $keyFile -AllowHttp -ReportPath $attentionReport *> $null
    }
    catch { $rejected = $true }
    if (-not (Wait-Job $attentionJob -Timeout 10)) { throw 'Attention payment mock did not receive the request.' }
    Receive-Job $attentionJob -ErrorAction Stop | Out-Null
    $attention = Get-Content -LiteralPath $attentionReport -Raw | ConvertFrom-Json
    if (-not $rejected -or $attention.success -or $attention.providers[0].state -ne 'webhook_attention') {
        throw 'Webhook attention was not rejected fail-closed.'
    }

    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    foreach ($workflowPath in @('.github/workflows/ci.yml', '.github/workflows/release.yml')) {
        $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot $workflowPath) -Raw
        if (-not $workflow.Contains('./tools/Test-AuditPaymentReadiness.ps1', [StringComparison]::Ordinal)) {
            throw "$workflowPath does not run payment readiness contracts."
        }
    }
    Write-Host 'Payment readiness contracts passed: paid production gateway accepted, webhook attention rejected and reports remain secret-free.' -ForegroundColor Green
}
finally {
    foreach ($job in $jobs) {
        if ($job.State -eq 'Running') { Stop-Job $job }
        Remove-Job $job -Force
    }
    if ([IO.Directory]::Exists($fixtureRoot)) { [IO.Directory]::Delete($fixtureRoot, $true) }
}
