$ErrorActionPreference = 'Stop'
$metadataScript = Join-Path $PSScriptRoot 'Get-ReleaseMetadata.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-release-metadata-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Invoke-Metadata([string]$Tag, [string]$Owner) {
    $output = Join-Path $testRoot "$([Guid]::NewGuid().ToString('N')).txt"
    New-Item -ItemType File -Path $output | Out-Null
    & $metadataScript -Tag $Tag -RepositoryOwner $Owner -OutputFile $output
    $result = @{}
    foreach ($line in Get-Content -LiteralPath $output) {
        $key, $value = $line -split '=', 2
        $result[$key] = $value
    }
    return $result
}

function Assert-Rejected([string]$Tag, [string]$Owner = 'ProxyHarbor') {
    $rejected = $false
    try { $null = Invoke-Metadata -Tag $Tag -Owner $Owner }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Некорректные release metadata были приняты: tag=$Tag owner=$Owner" }
}

try {
    $stable = Invoke-Metadata -Tag 'v1.2.3' -Owner 'ProxyHarbor'
    if ($stable.version -ne '1.2.3' -or $stable['image-tag'] -ne '1.2.3' -or
        $stable['image-prefix'] -ne 'ghcr.io/proxyharbor' -or
        $stable['is-prerelease'] -ne 'false') {
        throw 'Stable release metadata рассчитаны неверно.'
    }

    $prerelease = Invoke-Metadata -Tag 'v2.0.0-rc.1+build.5' -Owner 'Team-42'
    if ($prerelease.version -ne '2.0.0-rc.1+build.5' -or $prerelease['image-tag'] -ne '2.0.0-rc.1_build_build.5' -or
        $prerelease['image-prefix'] -ne 'ghcr.io/team-42' -or $prerelease['is-prerelease'] -ne 'true') {
        throw 'Prerelease metadata рассчитаны неверно.'
    }

    $stableBuild = Invoke-Metadata -Tag 'v2.0.0+build-5' -Owner 'Team-42'
    if ($stableBuild['image-tag'] -ne '2.0.0_build_build-5' -or $stableBuild['is-prerelease'] -ne 'false') {
        throw 'SemVer build metadata ошибочно классифицированы как prerelease.'
    }

    Assert-Rejected -Tag '1.2.3'
    Assert-Rejected -Tag 'v1.2'
    Assert-Rejected -Tag 'v01.2.3'
    Assert-Rejected -Tag 'v1.2.3-01'
    Assert-Rejected -Tag ("v1.2.3+" + ('a' * 130))
    Assert-Rejected -Tag 'v1.2.3' -Owner 'unsafe/owner'
    Write-Host 'Release metadata contracts пройдены: stable, prerelease, invalid tags и unsafe owner.' -ForegroundColor Green
} finally {
    # Удаляется только созданный этим тестом GUID-каталог внутри system temp.
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
