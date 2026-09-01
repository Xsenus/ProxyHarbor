[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$RequireCleanWorktree
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path

$requiredFiles = @(
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

$missing = @($requiredFiles | Where-Object { -not [IO.File]::Exists((Join-Path $root $_)) })
if ($missing.Count -gt 0) {
    throw "Репозиторий не готов к публикации: отсутствуют $($missing -join ', ')."
}

$trackedOutput = & git -C $root ls-files -z
if ($LASTEXITCODE -ne 0) { throw 'Не удалось прочитать Git index.' }
$tracked = @(($trackedOutput -join '') -split "`0" | Where-Object { $_ })
if ($tracked.Count -eq 0) { throw 'Git index пуст.' }

# Локальные ignored build/test каталоги допустимы, но ни один из них не должен
# оказаться в истории, которую получит публичный GitHub checkout.
$forbiddenPath = '(?i)(^|/)(?:\.env(?:\..+)?|artifacts|backups?|coverage|TestResults(?:-.+)?|node_modules|bin|obj|\.tmp-postgres)(?:/|$)|(?i)\.(?:phbackup|pfx|p12|pem|key|dump|sql|zip)$'
$forbiddenTracked = @($tracked | Where-Object {
    $_ -ne '.env.example' -and $_ -match $forbiddenPath
})
if ($forbiddenTracked.Count -gt 0) {
    throw "Git index содержит запрещённые secret/generated artifacts: $($forbiddenTracked -join ', ')."
}

$caseCollisions = @($tracked |
    Group-Object { $_.ToUpperInvariant() } |
    Where-Object Count -gt 1 |
    ForEach-Object { $_.Group -join ' <> ' })
if ($caseCollisions.Count -gt 0) {
    throw "Найдены несовместимые с Windows/macOS конфликты регистра путей: $($caseCollisions -join '; ')."
}

$oversized = [Collections.Generic.List[string]]::new()
foreach ($relativePath in $tracked) {
    $file = [IO.FileInfo](Join-Path $root $relativePath)
    if ($file.Exists -and $file.Length -gt 10MB) {
        $oversized.Add("$relativePath ($([Math]::Round($file.Length / 1MB, 2)) MiB)")
    }
}
if ($oversized.Count -gt 0) {
    throw "Tracked файлы больше 10 MiB требуют явного artifact/LFS решения: $($oversized -join ', ')."
}

$license = Get-Content -LiteralPath (Join-Path $root 'LICENSE') -Raw
if ($license -notmatch '^MIT License' -or $license -notmatch 'ProxyHarbor contributors') {
    throw 'LICENSE не содержит ожидаемую MIT-лицензию ProxyHarbor.'
}

$securityTextPath = Join-Path $root 'src/proxyharbor-web/public/.well-known/security.txt'
$securityText = Get-Content -LiteralPath $securityTextPath -Raw
if ($securityText -notmatch '(?m)^Contact:\s*mailto:[^\s@]+@[^\s@]+\s*$' -or
    $securityText -notmatch '(?m)^Canonical:\s*https://proxy\.blagodaty\.ru/\.well-known/security\.txt\s*$' -or
    $securityText -notmatch '(?m)^Preferred-Languages:\s*ru,\s*en\s*$') {
    throw 'security.txt не содержит обязательные Contact, Canonical и Preferred-Languages.'
}
$expiresMatch = [regex]::Match($securityText, '(?m)^Expires:\s*(\S+)\s*$')
$expiresAt = [DateTimeOffset]::MinValue
if (-not $expiresMatch.Success -or
    -not [DateTimeOffset]::TryParse(
        $expiresMatch.Groups[1].Value,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal,
        [ref]$expiresAt) -or
    $expiresAt -le [DateTimeOffset]::UtcNow.AddDays(30)) {
    throw 'security.txt отсутствует, содержит некорректный Expires либо истекает менее чем через 30 дней.'
}

if ($RequireCleanWorktree) {
    $status = @(& git -C $root status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось проверить чистоту worktree.' }
    if ($status.Count -gt 0) {
        throw "Publication gate требует чистый worktree: $($status -join '; ')."
    }
}

Write-Host "Publication readiness пройдена: $($tracked.Count) tracked файлов, governance/workflows, security.txt, case, size и artifact policy." -ForegroundColor Green
