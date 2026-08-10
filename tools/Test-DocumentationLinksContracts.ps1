$ErrorActionPreference = 'Stop'

$checker = Get-Content -LiteralPath './tools/Test-DocumentationLinks.ps1' -Raw
$ci = Get-Content -LiteralPath './.github/workflows/ci.yml' -Raw
$release = Get-Content -LiteralPath './.github/workflows/release.yml' -Raw
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-doc-links-$([Guid]::NewGuid().ToString('N'))"

if ($ci -notmatch 'Test-DocumentationLinks\.ps1' -or
    $release -notmatch 'Test-DocumentationLinks\.ps1' -or
    $checker -notmatch 'GetFullPath' -or $checker -notmatch 'UnescapeDataString') {
    throw 'Documentation link checker потерял CI/release wiring или path-normalization contract.'
}

function Assert-CheckerRejected([string]$ExpectedMessage) {
    $rejected = $false
    try { & ./tools/Test-DocumentationLinks.ps1 -RepositoryRoot $fixtureRoot *> $null }
    catch {
        $rejected = $true
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw }
    }
    if (-not $rejected) { throw "Documentation checker ошибочно принял fixture: $ExpectedMessage" }
}

try {
    [IO.Directory]::CreateDirectory((Join-Path $fixtureRoot 'docs')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'README.md'),
        "[valid](docs/guide.md) [anchor](#section) [web](https://example.invalid)`n")
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'docs/guide.md'), "# Guide`n")
    & ./tools/Test-DocumentationLinks.ps1 -RepositoryRoot $fixtureRoot *> $null

    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'README.md'), "[missing](docs/missing.md)`n")
    Assert-CheckerRejected 'отсутствует цель'

    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'README.md'), "[escape](../outside.md)`n")
    Assert-CheckerRejected 'выходит за пределы репозитория'
}
finally {
    if ([IO.Directory]::Exists($fixtureRoot)) { [IO.Directory]::Delete($fixtureRoot, $true) }
}

Write-Host 'Documentation link contracts пройдены: valid, missing и traversal fixtures.' -ForegroundColor Green
