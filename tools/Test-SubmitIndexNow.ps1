$ErrorActionPreference = 'Stop'
$submitScript = Join-Path $PSScriptRoot 'Submit-IndexNow.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-indexnow-$([Guid]::NewGuid().ToString('N'))"
$successJob = $null
$failureJob = $null

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-IndexNowMock([int]$Port, [bool]$PublishCorrectKey) {
    Start-Job -ArgumentList $Port, $PublishCorrectKey -ScriptBlock {
        param($Port, $PublishCorrectKey)
        $expectedKey = 'proxyharbor-public-indexnow-key-2026'
        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        $requestLimit = if ($PublishCorrectKey) { 2 } else { 1 }
        try {
            foreach ($requestNumber in 1..$requestLimit) {
                $context = $listener.GetContext()
                $path = $context.Request.Url.AbsolutePath
                if ($path -eq '/indexnow-key.txt') {
                    $body = if ($PublishCorrectKey) { $expectedKey } else { 'wrong-indexnow-key' }
                    $context.Response.ContentType = 'text/plain'
                }
                elseif ($path -eq '/indexnow' -and $context.Request.HttpMethod -eq 'POST') {
                    $reader = [IO.StreamReader]::new($context.Request.InputStream, $context.Request.ContentEncoding)
                    try { $payload = $reader.ReadToEnd() | ConvertFrom-Json }
                    finally { $reader.Dispose() }
                    $private = @($payload.urlList | Where-Object { $_ -match '/(admin|account|login|register|reset-password)' })
                    if ($payload.key -ne $expectedKey -or $payload.urlList.Count -ne 12 -or $private.Count -ne 0) {
                        $context.Response.StatusCode = 422
                    }
                    else { $context.Response.StatusCode = 200 }
                    $body = ''
                }
                else {
                    $context.Response.StatusCode = 404
                    $body = 'not found'
                }
                $bytes = [Text.Encoding]::UTF8.GetBytes($body)
                $context.Response.ContentLength64 = $bytes.Length
                $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                $context.Response.Close()
            }
        }
        finally { $listener.Stop(); $listener.Close() }
    }
}

function Wait-IndexNowMock([int]$Port) {
    foreach ($attempt in 1..50) {
        try {
            $client = [Net.Sockets.TcpClient]::new()
            try { $client.Connect('127.0.0.1', $Port); return }
            finally { $client.Dispose() }
        }
        catch {
            if ($attempt -eq 50) { throw "IndexNow mock on port $Port did not start." }
            Start-Sleep -Milliseconds 100
        }
    }
}

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null

    $successPort = Get-FreeTcpPort
    $successJob = Start-IndexNowMock -Port $successPort -PublishCorrectKey $true
    Wait-IndexNowMock $successPort
    $successReport = Join-Path $fixtureRoot 'success.json'
    & $submitScript -BaseUrl "http://127.0.0.1:$successPort" -Endpoint "http://127.0.0.1:$successPort/indexnow" `
        -AllowHttp -ReportPath $successReport *> $null
    $completed = Wait-Job $successJob -Timeout 10
    if (-not $completed) { throw 'Successful IndexNow mock did not receive the expected requests.' }
    Receive-Job $successJob -ErrorAction Stop | Out-Null
    $report = Get-Content -LiteralPath $successReport -Raw | ConvertFrom-Json
    if (-not $report.submitted -or $report.statusCode -ne 200 -or $report.urlCount -ne 12) {
        throw 'Successful IndexNow submission report is invalid.'
    }

    $failurePort = Get-FreeTcpPort
    $failureJob = Start-IndexNowMock -Port $failurePort -PublishCorrectKey $false
    Wait-IndexNowMock $failurePort
    $rejected = $false
    try {
        & $submitScript -BaseUrl "http://127.0.0.1:$failurePort" -Endpoint "http://127.0.0.1:$failurePort/indexnow" `
            -AllowHttp -ReportPath (Join-Path $fixtureRoot 'failure.json') *> $null
    }
    catch { $rejected = $true }
    $completed = Wait-Job $failureJob -Timeout 10
    if (-not $completed) { throw 'Negative IndexNow mock did not receive the ownership request.' }
    Receive-Job $failureJob -ErrorAction Stop | Out-Null
    if (-not $rejected) { throw 'Mismatched published IndexNow key was accepted.' }

    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    foreach ($workflowPath in @('.github/workflows/ci.yml', '.github/workflows/release.yml')) {
        $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot $workflowPath) -Raw
        if (-not $workflow.Contains('./tools/Test-SubmitIndexNow.ps1', [StringComparison]::Ordinal)) {
            throw "$workflowPath does not run IndexNow submission contracts."
        }
    }

    Write-Host 'IndexNow submission contracts passed: ownership verification, canonical URL allowlist and fail-closed response handling.' -ForegroundColor Green
}
finally {
    foreach ($job in @($successJob, $failureJob)) {
        if ($null -ne $job) {
            if ($job.State -eq 'Running') { Stop-Job $job }
            Remove-Job $job -Force
        }
    }
    if ([IO.Directory]::Exists($fixtureRoot)) { [IO.Directory]::Delete($fixtureRoot, $true) }
}
