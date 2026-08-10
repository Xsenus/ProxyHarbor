$ErrorActionPreference = 'Stop'

$runner = Join-Path $PSScriptRoot 'Invoke-Actionlint.ps1'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$runnerContent = Get-Content -LiteralPath $runner -Raw

# Контракт фиксирует источник, версию и обе платформенные суммы, чтобы замена
# загрузки на latest либо удаление fail-closed hash gate ломали CI.
$requiredRunnerFragments = @(
    "`$version = '1.7.12'",
    'https://github.com/rhysd/actionlint/releases/download/v$version/',
    '8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8',
    '6e7241b51e6817ea6a047693d8e6fed13b31819c9a0dd6c5a726e1592d22f6e9',
    'Get-FileHash -LiteralPath $resolvedArchive -Algorithm SHA256',
    'if ($actualHash -cne $artifact.Sha256)'
)
foreach ($fragment in $requiredRunnerFragments) {
    if (-not $runnerContent.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Actionlint runner contract потерял обязательный фрагмент: $fragment"
    }
}

foreach ($workflowName in 'ci.yml', 'release.yml') {
    $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot ".github/workflows/$workflowName") -Raw
    if (-not $workflow.Contains('./tools/Invoke-Actionlint.ps1', [StringComparison]::Ordinal)) {
        throw "$workflowName не запускает закреплённый actionlint gate."
    }
    if (-not $workflow.Contains('./tools/Test-ActionlintContracts.ps1', [StringComparison]::Ordinal)) {
        throw "$workflowName не запускает actionlint contract test."
    }
}

# Неверный архив обязан быть отклонён до распаковки. Fixture находится в
# отдельном GUID-каталоге и не может пересечься с пользовательскими файлами.
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-actionlint-contract-$([Guid]::NewGuid().ToString('N'))"
try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $invalidArchive = Join-Path $fixtureRoot 'untrusted-archive.bin'
    [IO.File]::WriteAllText($invalidArchive, 'not an actionlint release artifact')
    $rejected = $false
    try {
        & $runner -RepositoryRoot $repositoryRoot -ArchivePath $invalidArchive *> $null
    } catch {
        $rejected = $_.Exception.Message -like '*SHA-256 mismatch*'
        if (-not $rejected) { throw }
    }
    if (-not $rejected) { throw 'Actionlint runner ошибочно принял архив с неверным SHA-256.' }
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedFixture.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Directory]::Exists($resolvedFixture)) {
        [IO.Directory]::Delete($resolvedFixture, $true)
    }
}

Write-Host 'Actionlint contracts пройдены: version/hash pins, CI+release wiring и fail-closed archive rejection.' -ForegroundColor Green
