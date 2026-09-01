$ErrorActionPreference = 'Stop'

$gate = Get-Content -LiteralPath './tools/Test-PublicationReadiness.ps1' -Raw
$ci = Get-Content -LiteralPath './.github/workflows/ci.yml' -Raw
$release = Get-Content -LiteralPath './.github/workflows/release.yml' -Raw
$guide = Get-Content -LiteralPath './docs/GITHUB_SETUP.md' -Raw
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-publication-$([Guid]::NewGuid().ToString('N'))"

foreach ($signal in 'docs/GITHUB_SETUP.md', 'caseCollisions', '10MB', 'forbiddenPath', 'RequireCleanWorktree', 'security.txt', 'Expires') {
    if (-not $gate.Contains($signal, [StringComparison]::Ordinal)) {
        throw "Publication gate потерял обязательный contract: $signal"
    }
}

if ($ci -notmatch 'Test-PublicationReadiness\.ps1 -RequireCleanWorktree' -or
    $ci -notmatch 'Test-PublicationReadinessContracts\.ps1' -or
    $release -notmatch 'Test-PublicationReadiness\.ps1 -RequireCleanWorktree' -or
    $release -notmatch 'Test-PublicationReadinessContracts\.ps1') {
    throw 'CI/release не выполняют publication gate и его contract-тест.'
}

foreach ($requiredCheck in 'verify', 'Analyze csharp', 'Analyze javascript-typescript') {
    $formattedCheck = [string]::Concat('`', $requiredCheck, '`')
    if (-not $guide.Contains($formattedCheck, [StringComparison]::Ordinal)) {
        throw "GitHub setup guide не фиксирует required check: $requiredCheck"
    }
}

function Invoke-Git([Parameter(ValueFromRemainingArguments)][string[]]$Arguments) {
    & git -C $fixtureRoot @Arguments *> $null
    if ($LASTEXITCODE -ne 0) { throw "Fixture git command failed: git $($Arguments -join ' ')" }
}

function Assert-PublicationRejected([string]$ExpectedMessage, [switch]$RequireClean) {
    $rejected = $false
    try {
        & ./tools/Test-PublicationReadiness.ps1 -RepositoryRoot $fixtureRoot `
            -RequireCleanWorktree:$RequireClean *> $null
    } catch {
        $rejected = $true
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw }
    }
    if (-not $rejected) { throw "Publication gate ошибочно принял fixture: $ExpectedMessage" }
}

try {
    $fixtureFiles = @(
        'LICENSE', 'README.md', 'SECURITY.md', 'SUPPORT.md', 'CODE_OF_CONDUCT.md',
        'CONTRIBUTING.md', 'CHANGELOG.md', 'docs/README.md', 'docs/ARCHITECTURE.md',
        'docs/API.md', 'docs/CONFIGURATION.md', 'docs/BACKUP_RESTORE.md',
        'docs/PROJECT_STATUS.md',
        '.env.example', '.gitignore', '.dockerignore', '.gitattributes',
        '.github/dependabot.yml', '.github/pull_request_template.md',
        '.github/ISSUE_TEMPLATE/bug_report.yml', '.github/ISSUE_TEMPLATE/source_request.yml',
        '.github/ISSUE_TEMPLATE/documentation.yml', '.github/ISSUE_TEMPLATE/config.yml',
        '.github/workflows/ci.yml', '.github/workflows/codeql.yml',
        '.github/workflows/release.yml', '.github/workflows/source-audit.yml',
        'docs/DEPLOYMENT.md', 'docs/GITHUB_SETUP.md', 'docs/RELEASING.md',
        'src/proxyharbor-web/public/.well-known/security.txt'
    )
    foreach ($relativePath in $fixtureFiles) {
        $path = Join-Path $fixtureRoot $relativePath
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        $content = if ($relativePath -eq 'LICENSE') {
            "MIT License`n`nCopyright (c) ProxyHarbor contributors`n"
        } elseif ($relativePath -eq 'src/proxyharbor-web/public/.well-known/security.txt') {
            $expires = [DateTimeOffset]::UtcNow.AddDays(180).ToString('yyyy-MM-ddTHH:mm:ssZ')
            "Contact: mailto:security@example.com`nExpires: $expires`nPreferred-Languages: ru, en`nCanonical: https://proxy.blagodaty.ru/.well-known/security.txt`n"
        } else { "fixture`n" }
        [IO.File]::WriteAllText($path, $content)
    }

    Invoke-Git init
    Invoke-Git config user.name ProxyHarbor-CI
    Invoke-Git config user.email ci@invalid.example
    Invoke-Git add --all
    Invoke-Git commit -m baseline
    & ./tools/Test-PublicationReadiness.ps1 -RepositoryRoot $fixtureRoot -RequireCleanWorktree *> $null

    [IO.File]::WriteAllText((Join-Path $fixtureRoot '.env.production'), "SECRET=value`n")
    Invoke-Git add -- .env.production
    Assert-PublicationRejected 'secret/generated artifacts'
    Invoke-Git rm --cached -- .env.production
    [IO.File]::Delete((Join-Path $fixtureRoot '.env.production'))

    $artifactPath = Join-Path $fixtureRoot 'artifacts/runtime-audit.json'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($artifactPath)) | Out-Null
    [IO.File]::WriteAllText($artifactPath, "{}`n")
    Invoke-Git add -- artifacts/runtime-audit.json
    Assert-PublicationRejected 'secret/generated artifacts'
    Invoke-Git rm --cached -- artifacts/runtime-audit.json
    [IO.Directory]::Delete((Join-Path $fixtureRoot 'artifacts'), $true)

    $largePath = Join-Path $fixtureRoot 'large.bin'
    $stream = [IO.File]::Create($largePath)
    try { $stream.SetLength(10MB + 1) } finally { $stream.Dispose() }
    Invoke-Git add -- large.bin
    Assert-PublicationRejected 'больше 10 MiB'
    Invoke-Git rm --cached -- large.bin
    [IO.File]::Delete($largePath)

    $blob = ('collision' | & git -C $fixtureRoot hash-object -w --stdin).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $blob) { throw 'Не удалось создать fixture blob.' }
    Invoke-Git update-index --add --cacheinfo "100644,$blob,Case.txt"
    Invoke-Git update-index --add --cacheinfo "100644,$blob,case.txt"
    Assert-PublicationRejected 'конфликты регистра путей'
    Invoke-Git update-index --force-remove Case.txt case.txt

    [IO.File]::AppendAllText((Join-Path $fixtureRoot 'README.md'), "dirty`n")
    Assert-PublicationRejected 'чистый worktree' -RequireClean

    Invoke-Git checkout -- README.md
    $securityTextPath = Join-Path $fixtureRoot 'src/proxyharbor-web/public/.well-known/security.txt'
    [IO.File]::WriteAllText($securityTextPath, "Contact: mailto:security@example.com`nExpires: 2000-01-01T00:00:00Z`nPreferred-Languages: ru, en`nCanonical: https://proxy.blagodaty.ru/.well-known/security.txt`n")
    Assert-PublicationRejected 'security.txt'
}
finally {
    if ([IO.Directory]::Exists($fixtureRoot)) {
        foreach ($file in [IO.Directory]::EnumerateFiles($fixtureRoot, '*', [IO.SearchOption]::AllDirectories)) {
            [IO.File]::SetAttributes($file, [IO.FileAttributes]::Normal)
        }
        [IO.Directory]::Delete($fixtureRoot, $true)
    }
}

Write-Host 'Publication contracts пройдены: wiring/checks и negative fixtures для secret/artifact, size, case collision, dirty worktree и security.txt expiry.' -ForegroundColor Green
