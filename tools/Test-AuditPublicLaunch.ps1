$ErrorActionPreference = 'Stop'
$auditScript = Join-Path $PSScriptRoot 'Audit-PublicLaunch.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-launch-audit-$([Guid]::NewGuid().ToString('N'))"

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-LaunchMock([int]$Port, [ValidateSet('success', 'failure')][string]$Mode) {
    Start-Job -ArgumentList $Port, $Mode -ScriptBlock {
        param($Port, $Mode)
        $base = "http://127.0.0.1:$Port"
        $public = @('/', '/pricing', '/service', '/legal', '/offer', '/privacy', '/personal-data-consent',
            '/marketing-consent', '/acceptable-use', '/cookies', '/refunds', '/requisites')
        $private = @('/admin', '/account', '/login', '/register', '/forgot-password', '/reset-password')
        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("$base/")
        $listener.Start()
        $handled = 0
        try {
            while ($handled -lt 24) {
                $context = $listener.GetContext()
                if ($context.Request.Url.AbsolutePath -eq '/mock-ready') {
                    $body = '{}'
                } else {
                    $handled++
                    $path = $context.Request.Url.AbsolutePath
                    $context.Response.Headers['Strict-Transport-Security'] = 'max-age=31536000'
                    $context.Response.Headers['Content-Security-Policy'] = "default-src 'self'"
                    $context.Response.Headers['X-Content-Type-Options'] = 'nosniff'
                    $context.Response.Headers['X-Frame-Options'] = 'DENY'
                    $context.Response.Headers['Referrer-Policy'] = 'strict-origin-when-cross-origin'
                    $context.Response.Headers['Permissions-Policy'] = 'camera=()'
                    if ($path -in $public) {
                        $canonical = if ($path -eq '/') { "$base/" } else { "$base$path" }
                        $body = "<html><head><title>Page</title><meta name=`"description`" content=`"Description`"><meta name=`"robots`" content=`"index, follow`"><link rel=`"canonical`" href=`"$canonical`"><script type=`"application/ld+json`">{}</script></head></html>"
                        $context.Response.ContentType = 'text/html'
                    } elseif ($path -eq '/robots.txt') {
                        $body = "User-agent: *`nDisallow: /admin`nDisallow: /account`nDisallow: /login`nDisallow: /register`nDisallow: /forgot-password`nDisallow: /reset-password`nSitemap: $base/sitemap.xml"
                        $context.Response.ContentType = 'text/plain'
                    } elseif ($path -eq '/sitemap.xml') {
                        $urls = $public | ForEach-Object { if ($_ -eq '/') { "$base/" } else { "$base$_" } }
                        $body = '<?xml version="1.0"?><urlset>' + (($urls | ForEach-Object { "<url><loc>$_</loc></url>" }) -join '') + '</urlset>'
                        $context.Response.ContentType = 'application/xml'
                    } elseif ($path -eq '/indexnow-key.txt') {
                        $body = 'proxyharbor-public-indexnow-key-2026'
                        $context.Response.ContentType = 'text/plain'
                    } elseif ($path -in $private) {
                        $body = '<html>private</html>'
                        $context.Response.ContentType = 'text/html'
                        if ($Mode -eq 'success') { $context.Response.Headers['X-Robots-Tag'] = 'noindex, nofollow, noarchive' }
                    } elseif ($path -eq '/health/ready') {
                        $body = '{"status":"healthy"}'
                        $context.Response.ContentType = 'application/json'
                    } elseif ($path -eq '/api/v1/payments/catalog') {
                        $available = if ($Mode -eq 'success') { 'true' } else { 'false' }
                        $body = "{`"enabled`":true,`"products`":[{`"code`":`"day`",`"name`":`"Day`",`"amountMinor`":9900,`"currency`":`"RUB`",`"durationDays`":1}],`"providers`":[{`"code`":`"test`",`"available`":$available}]}"
                        $context.Response.ContentType = 'application/json'
                    } else {
                        $context.Response.StatusCode = 404
                        $body = 'not found'
                    }
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

function Wait-LaunchMock([int]$Port) {
    foreach ($attempt in 1..50) {
        try { Invoke-WebRequest -Uri "http://127.0.0.1:$Port/mock-ready" -TimeoutSec 1 | Out-Null; return }
        catch {
            if ($attempt -eq 50) { throw "Launch mock на порту $Port не запустился." }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Invoke-LaunchCase([string]$Mode, [bool]$ShouldSucceed) {
    $port = Get-FreeTcpPort
    $job = Start-LaunchMock -Port $port -Mode $Mode
    $reportPath = Join-Path $fixtureRoot "$Mode.json"
    try {
        Wait-LaunchMock $port
        $rejected = $false
        try {
            & $auditScript -BaseUrl "http://127.0.0.1:$port" -AllowHttp -ReportPath $reportPath *> $null
        }
        catch { $rejected = $true }
        $completed = Wait-Job $job -Timeout 15
        if (-not $completed) { throw "Launch mock $Mode не завершил ожидаемые запросы." }
        Receive-Job $job -ErrorAction Stop | Out-Null
        $report = Get-Content $reportPath -Raw | ConvertFrom-Json
        if ($ShouldSucceed -and ($rejected -or -not $report.success -or $report.summary.failed -ne 0 -or $report.summary.total -lt 31)) {
            throw 'Положительный launch-аудит contract нарушен.'
        }
        if (-not $ShouldSucceed -and (-not $rejected -or $report.success -or $report.summary.failed -lt 2)) {
            throw 'Отрицательный launch-аудит contract был принят.'
        }
    }
    finally {
        if ($job.State -eq 'Running') { Stop-Job $job }
        Remove-Job $job -Force
    }
}

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    Invoke-LaunchCase success $true
    Invoke-LaunchCase failure $false
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    foreach ($workflowPath in @('.github/workflows/ci.yml', '.github/workflows/release.yml')) {
        $workflow = Get-Content (Join-Path $repositoryRoot $workflowPath) -Raw
        if (-not $workflow.Contains('./tools/Test-AuditPublicLaunch.ps1', [StringComparison]::Ordinal)) {
            throw "$workflowPath не запускает public launch audit contracts."
        }
    }
    Write-Host 'Public launch audit contracts пройдены: SEO, private noindex, security headers, sitemap, IndexNow, readiness и payment availability.' -ForegroundColor Green
}
finally {
    if ([IO.Directory]::Exists($fixtureRoot)) {
        [IO.Directory]::Delete($fixtureRoot, $true)
    }
}
