param(
    [string]$BaseUrl = 'https://proxy.blagodaty.ru',
    [string]$Endpoint = 'https://yandex.com/indexnow',
    [string]$KeyFile = '',
    [string]$ReportPath = 'artifacts/indexnow-submission.json',
    [switch]$AllowHttp,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($KeyFile)) {
    $KeyFile = Join-Path $repositoryRoot 'src/proxyharbor-web/public/indexnow-key.txt'
}

$base = $BaseUrl.TrimEnd('/')
$site = [Uri]$base
if ($site.Scheme -ne 'https' -and -not $AllowHttp -and -not $WhatIf) {
    throw 'IndexNow publication requires an HTTPS BaseUrl.'
}

$key = (Get-Content -LiteralPath $KeyFile -Raw).Trim()
if ($key -notmatch '^[A-Za-z0-9-]{8,128}$') {
    throw 'IndexNow key must contain 8-128 ASCII letters, digits, or hyphens.'
}

$metadataPath = Join-Path $repositoryRoot 'src/proxyharbor-web/src/seoMetadata.json'
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$urls = @($metadata.pages.PSObject.Properties.Name | ForEach-Object {
    if ($_ -eq '/') { "$base/" } else { "$base$_" }
})
if ($urls.Count -eq 0 -or $urls.Count -gt 10000) {
    throw "IndexNow URL list has an invalid size: $($urls.Count)."
}

$keyLocation = "$base/indexnow-key.txt"
$payload = [ordered]@{
    host = $site.Host
    key = $key
    keyLocation = $keyLocation
    urlList = $urls
}

$result = [ordered]@{
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    endpoint = $Endpoint
    host = $site.Host
    keyLocation = $keyLocation
    urlCount = $urls.Count
    urls = $urls
    submitted = -not $WhatIf
    statusCode = $null
}

if (-not $WhatIf) {
    $publishedKey = (Invoke-WebRequest -Uri $keyLocation -TimeoutSec 15 -MaximumRedirection 0).Content.Trim()
    if ($publishedKey -ne $key) {
        throw "Published IndexNow key at $keyLocation does not match the local key."
    }
    try {
        $response = Invoke-WebRequest -Method Post -Uri $Endpoint -ContentType 'application/json; charset=utf-8' `
            -Body ($payload | ConvertTo-Json -Depth 3 -Compress) -TimeoutSec 30 -MaximumRedirection 0
        $result.statusCode = [int]$response.StatusCode
    }
    catch {
        if ($null -ne $_.Exception.Response) {
            $result.statusCode = [int]$_.Exception.Response.StatusCode
        }
        throw
    }
    if ($result.statusCode -notin @(200, 202)) {
        throw "IndexNow endpoint returned HTTP $($result.statusCode)."
    }
}

$resolvedReport = if ([IO.Path]::IsPathRooted($ReportPath)) { $ReportPath } else { Join-Path $repositoryRoot $ReportPath }
$reportDirectory = Split-Path -Parent $resolvedReport
if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
    [IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
}
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resolvedReport -Encoding utf8

if ($WhatIf) {
    Write-Host "IndexNow dry run: $($urls.Count) URLs; key location $keyLocation." -ForegroundColor Yellow
} else {
    Write-Host "IndexNow accepted $($urls.Count) URLs with HTTP $($result.statusCode)." -ForegroundColor Green
}
Write-Host "Report: $resolvedReport"
