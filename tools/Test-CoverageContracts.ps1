$ErrorActionPreference = 'Stop'

$gate = Join-Path $PSScriptRoot 'Assert-Coverage.ps1'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gateContent = Get-Content -LiteralPath $gate -Raw

# Порог фиксируется контрактом: изменение default должно быть осознанным и сопровождаться
# новой фактической базовой линией, а не незаметно ослаблять обязательный CI gate.
foreach ($fragment in @(
    '[double]$MinimumLineRate = 0.65',
    '[double]$MinimumBranchRate = 0.68')) {
    if (-not $gateContent.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Coverage gate потерял обязательный порог: $fragment"
    }
}

foreach ($workflowName in 'ci.yml', 'release.yml') {
    $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot ".github/workflows/$workflowName") -Raw
    if (-not $workflow.Contains('./tools/Assert-Coverage.ps1', [StringComparison]::Ordinal) -or
        -not $workflow.Contains('./tools/Test-CoverageContracts.ps1', [StringComparison]::Ordinal)) {
        throw "$workflowName не запускает coverage gate и его contracts."
    }
}

function Write-CoverageFixture(
    [string]$Path,
    [int[]]$Hits,
    [int]$CoveredBranches,
    [int]$TotalBranches) {
    $lineXml = for ($index = 0; $index -lt $Hits.Count; $index++) {
        $number = $index + 1
        if ($index -eq 0) {
            "<line number='$number' hits='$($Hits[$index])' branch='True' condition-coverage='0% ($CoveredBranches/$TotalBranches)' />"
        } else {
            "<line number='$number' hits='$($Hits[$index])' branch='False' />"
        }
    }
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<coverage><packages><package><classes>
  <class filename="src/Representative.cs"><lines>$($lineXml -join '')</lines></class>
</classes></package></packages></coverage>
"@
    [IO.File]::WriteAllText($Path, $xml)
}

function Assert-CoverageRejected([string]$Path, [string]$ExpectedMessage) {
    $rejected = $false
    try { & $gate -ReportPath $Path *> $null }
    catch {
        $rejected = $_.Exception.Message -like "*$ExpectedMessage*"
        if (-not $rejected) { throw }
    }
    if (-not $rejected) { throw "Coverage gate ошибочно принял fixture: $ExpectedMessage" }
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-coverage-contract-$([Guid]::NewGuid().ToString('N'))"
try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $passing = Join-Path $fixtureRoot 'passing.xml'
    $lowLines = Join-Path $fixtureRoot 'low-lines.xml'
    $lowBranches = Join-Path $fixtureRoot 'low-branches.xml'
    Write-CoverageFixture $passing @(1, 1, 1, 0) 3 4
    Write-CoverageFixture $lowLines @(1, 1, 0, 0) 3 4
    Write-CoverageFixture $lowBranches @(1, 1, 1, 0) 2 4

    & $gate -ReportPath $passing *> $null
    Assert-CoverageRejected $lowLines 'Line coverage'
    Assert-CoverageRejected $lowBranches 'Branch coverage'
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedFixture.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Directory]::Exists($resolvedFixture)) {
        [IO.Directory]::Delete($resolvedFixture, $true)
    }
}

Write-Host 'Coverage contracts пройдены: defaults 65/68, CI/release wiring и pass/line/branch fixtures.' -ForegroundColor Green
