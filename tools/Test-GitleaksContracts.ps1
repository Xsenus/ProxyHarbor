$ErrorActionPreference = 'Stop'

$runner = Get-Content -LiteralPath './tools/Invoke-Gitleaks.ps1' -Raw
$ciWorkflow = Get-Content -LiteralPath './.github/workflows/ci.yml' -Raw
$releaseWorkflow = Get-Content -LiteralPath './.github/workflows/release.yml' -Raw
$ignoreLines = Get-Content -LiteralPath './.gitleaksignore' |
    Where-Object { $_ -and -not $_.StartsWith('#', [StringComparison]::Ordinal) }

if ($runner -notmatch "Version = '8\.28\.0'" -or
    $runner -notmatch "ExpectedSha256 = '[0-9a-f]{64}'" -or
    $runner -notmatch 'Get-FileHash' -or
    $runner -notmatch '--gitleaks-ignore-path' -or
    $runner -notmatch '--redact' -or
    $runner -notmatch 'exit-code 1') {
    throw 'Gitleaks runner потерял version/hash pin, redaction либо fail-closed exit contract.'
}

if ($ciWorkflow -notmatch 'fetch-depth:\s*0' -or
    $ciWorkflow -notmatch 'Verify repository history contains no secrets' -or
    $ciWorkflow -notmatch '\./tools/Invoke-Gitleaks\.ps1') {
    throw 'CI не гарантирует full-history Gitleaks scan.'
}

if ($releaseWorkflow -notmatch 'fetch-depth:\s*0' -or
    $releaseWorkflow -notmatch 'Verify tagged repository history contains no secrets' -or
    $releaseWorkflow -notmatch '\./tools/Invoke-Gitleaks\.ps1' -or
    $releaseWorkflow -notmatch '\./tools/Test-GitleaksContracts\.ps1') {
    throw 'Release gate не гарантирует независимый full-history Gitleaks scan tagged commit.'
}

if ($ignoreLines.Count -ne 14 -or
    $ignoreLines.Where({ $_ -notmatch '^[0-9a-f]{40}:.+:(curl-auth-header|generic-api-key):[1-9][0-9]*$' }).Count -ne 0) {
    throw '.gitleaksignore должен содержать только 14 проверенных точечных fingerprint без broad allowlist.'
}

Write-Host 'Gitleaks contracts пройдены: pinned archive, redaction, fail-closed exit и full-history CI/release wiring.' -ForegroundColor Green
