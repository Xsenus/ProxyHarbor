$ErrorActionPreference = 'Stop'

$gate = Join-Path $PSScriptRoot 'Test-WorkflowSecurity.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-supply-chain-$([Guid]::NewGuid().ToString('N'))"
$workflowRoot = Join-Path $fixtureRoot '.github/workflows'
$sourceRoot = Join-Path $fixtureRoot 'src/TestApp'
New-Item -ItemType Directory -Path $workflowRoot, $sourceRoot | Out-Null

function Set-FixtureWorkflow([string]$Content) {
    Set-Content -LiteralPath (Join-Path $workflowRoot 'test.yml') -Value $Content -Encoding utf8NoBOM
}

function Assert-GateRejected([string]$ExpectedMessage) {
    $rejected = $false
    try {
        & $gate -RepositoryRoot $fixtureRoot -MinimumPinnedContainerReferences 0 `
            -MinimumSynchronizedPostgresReferences 0 *> $null
    } catch {
        $rejected = $true
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw }
    }
    if (-not $rejected) {
        throw "Supply-chain gate ошибочно принял небезопасный fixture; ожидалось: $ExpectedMessage"
    }
}

try {
    # Полностью pinned service считается образом, а похожее поле matrix.image —
    # лишь metadata release-матрицы и не должно ошибочно интерпретироваться как runtime.
    Set-FixtureWorkflow @'
name: test
jobs:
  verify:
    strategy:
      matrix:
        include:
          - image: application-component
    services:
      postgres:
        image: postgres:18-alpine@sha256:9a8afca54e7861fd90fab5fdf4c42477a6b1cb7d293595148e674e0a3181de15
    steps:
      - uses: ./local-action
'@
    & $gate -RepositoryRoot $fixtureRoot -MinimumPinnedContainerReferences 1 `
        -MinimumSynchronizedPostgresReferences 0 *> $null

    Set-FixtureWorkflow @'
name: test
jobs:
  verify:
    services:
      postgres:
        image: postgres:18-alpine
'@
    Assert-GateRejected 'mutable workflow container postgres:18-alpine'

    Set-FixtureWorkflow @'
name: test
jobs:
  verify:
    container: node:22-alpine
    steps: []
'@
    Assert-GateRejected 'mutable workflow container node:22-alpine'

    Set-FixtureWorkflow @'
name: test
jobs:
  verify:
    steps:
      - uses: actions/checkout@v4
'@
    Assert-GateRejected 'неприкреплённый action actions/checkout@v4'

    Set-Content -LiteralPath (Join-Path $workflowRoot 'bypass.yaml') -Value @'
name: test-yaml
jobs:
  verify:
    services:
      postgres:
        image: postgres:18-alpine
'@ -Encoding utf8NoBOM
    Assert-GateRejected 'bypass.yaml:6 использует mutable workflow container postgres:18-alpine'
    [IO.File]::Delete((Join-Path $workflowRoot 'bypass.yaml'))

    Set-FixtureWorkflow "name: test`njobs: {}"
    $variantDockerfile = Join-Path $sourceRoot 'Dockerfile.dev'
    Set-Content -LiteralPath $variantDockerfile -Value 'FROM node:22-alpine' -Encoding utf8NoBOM
    Assert-GateRejected 'mutable container reference node:22-alpine'
    [IO.File]::Delete($variantDockerfile)

    $composePath = Join-Path $fixtureRoot 'compose.test.yaml'
    Set-Content -LiteralPath $composePath -Value @'
services:
  cache:
    image: redis:latest
'@ -Encoding utf8NoBOM
    Assert-GateRejected 'mutable container reference redis:latest'
    [IO.File]::Delete($composePath)

    Set-FixtureWorkflow @'
name: test
jobs:
  verify:
    services:
      postgres:
        image: postgres:18-alpine@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
'@
    Set-Content -LiteralPath $composePath -Value @'
services:
  postgres:
    image: postgres:18-alpine@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
'@ -Encoding utf8NoBOM
    Assert-GateRejected 'PostgreSQL tag/digest расходятся между Compose и workflows'

    Write-Host 'Supply-chain contracts пройдены: workflow services/container, matrix metadata, actions, Dockerfile/Compose variants и PostgreSQL pin synchronization.' -ForegroundColor Green
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedFixture.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Directory]::Exists($resolvedFixture)) {
        [IO.Directory]::Delete($resolvedFixture, $true)
    }
}
