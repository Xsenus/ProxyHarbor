$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sharedBuild = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$testProjectPath = Join-Path $repositoryRoot 'tests/ProxyHarbor.Tests/ProxyHarbor.Tests.csproj'
$testProject = Get-Content -LiteralPath $testProjectPath -Raw

if ($sharedBuild -notmatch '<GenerateDocumentationFile>true</GenerateDocumentationFile>' -or
    $sharedBuild -notmatch '<TreatWarningsAsErrors>true</TreatWarningsAsErrors>' -or
    $sharedBuild -match 'CS1591') {
    throw 'Production build должен генерировать XML, считать warnings ошибками и не подавлять CS1591 глобально.'
}

$suppressions = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File |
    Where-Object { $_.Extension -in '.csproj', '.props', '.targets' } |
    Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'CS1591' }
if ($suppressions.Count -ne 1 -or
    -not [string]::Equals($suppressions[0].FullName, $testProjectPath, [StringComparison]::OrdinalIgnoreCase) -or
    $testProject -notmatch '<NoWarn>\$\(NoWarn\);CS1591</NoWarn>') {
    throw 'CS1591 разрешено подавлять только точечно в xUnit-проекте.'
}

Write-Host 'Documentation contracts пройдены: production XML docs и fail-closed CS1591 обязательны.' -ForegroundColor Green
