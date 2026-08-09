$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot 'Test-Changelog.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-changelog-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Assert-Case([string]$Name, [string]$Content, [bool]$ShouldPass, [string]$Version = '') {
    $path = Join-Path $testRoot "$Name.md"
    Set-Content -LiteralPath $path -Value $Content -Encoding utf8NoBOM
    $failed = $false
    try { & $validator -Path $path -Version $Version | Out-Null } catch { $failed = $true }
    if ($ShouldPass -eq $failed) { throw "Changelog contract-case $Name вернул неожиданный результат." }
}

try {
    $valid = @'
# Changelog

## [Unreleased]

### Changed
- pending

## [1.2.3] - 2026-08-10

### Added
- release
'@
    Assert-Case 'valid' $valid $true '1.2.3'
    Assert-Case 'valid-prerelease' ($valid + "`n## [1.3.0-rc.1+build.5] - 2026-08-10`n`n### Fixed`n- candidate") $true '1.3.0-rc.1+build.5'
    Assert-Case 'missing-version' $valid $false '1.2.4'
    Assert-Case 'duplicate' ($valid + "`n## [1.2.3] - 2026-08-10`n`n### Fixed`n- duplicate") $false
    Assert-Case 'unreleased-not-first' ($valid.Replace('## [Unreleased]', '## [2.0.0] - 2026-08-10')) $false
    Assert-Case 'bad-date' ($valid.Replace('2026-08-10', '10.08.2026')) $false
    Assert-Case 'leading-zero-prerelease' ($valid.Replace('[1.2.3]', '[1.2.3-01]')) $false
    Assert-Case 'hidden-malformed-heading' ($valid + "`n## [1.2.4-01] - 2026-08-10`n`n### Fixed`n- invalid") $false
    & $validator -Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md') -Version '1.0.0' |
        Out-Null
    Write-Host 'Changelog contracts пройдены: stable/prerelease, missing version, duplicate, order, date и strict SemVer.' -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
