param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [ValidateRange(0, 10000)][int]$MinimumPinnedContainerReferences = 13,
    [ValidateRange(0, 100)][int]$MinimumSynchronizedPostgresReferences = 4
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$workflowRoot = Join-Path $repositoryRoot '.github/workflows'
$violations = [Collections.Generic.List[string]]::new()
$postgresReferences = [Collections.Generic.List[string]]::new()
$workflowFiles = @(Get-ChildItem -LiteralPath $workflowRoot -File |
    Where-Object { $_.Extension -in '.yml', '.yaml' })

# Tag major-version у action подвижен и способен изменить исполняемый код без
# review репозитория. Разрешаются только локальные actions либо полный SHA-1.
$pinnedWorkflowImages = 0
foreach ($workflow in $workflowFiles) {
    $lineNumber = 0
    $containerScopeIndents = [Collections.Generic.List[int]]::new()
    foreach ($line in Get-Content -LiteralPath $workflow.FullName) {
        $lineNumber++
        if ($line -match '^\s*(?:-\s*)?uses:\s*(?<reference>\S+)') {
            $reference = $Matches['reference']
            if (-not $reference.StartsWith('./', [StringComparison]::Ordinal) -and
                $reference -notmatch '^[^@\s]+@[0-9a-f]{40}$') {
                $violations.Add("$($workflow.Name):$lineNumber использует неприкреплённый action $reference")
            }
        }
        if ($line -match '^\s*(?:#.*)?$') { continue }

        $indent = $line.Length - $line.TrimStart().Length
        for ($index = $containerScopeIndents.Count - 1; $index -ge 0; $index--) {
            if ($indent -le $containerScopeIndents[$index]) {
                $containerScopeIndents.RemoveAt($index)
            }
        }
        if ($line -match '^\s*(?:services|container):\s*$') {
            $containerScopeIndents.Add($indent)
            continue
        }
        $isScopedImage = $containerScopeIndents.Count -gt 0 -and
            $line -match '^\s*image:\s*(?<reference>\S+)'
        $isInlineContainer = $line -match '^\s*container:\s+(?<reference>\S+)'
        if ($isScopedImage -or $isInlineContainer) {
            $reference = $Matches['reference']
            if ($reference.StartsWith('postgres:', [StringComparison]::Ordinal)) {
                $postgresReferences.Add($reference)
            }
            if ($reference -match '^[^@\s]+:[^@\s]+@sha256:[0-9a-f]{64}$') {
                $pinnedWorkflowImages++
            } else {
                $violations.Add("$($workflow.Name):$lineNumber использует mutable workflow container $reference")
            }
        }
    }
}

# Version tag сохраняет читаемость и позволяет Dependabot предложить upgrade,
# digest одновременно делает фактические build/runtime bytes неизменяемыми.
$containerFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -File |
        Where-Object { $_.Name -like 'Dockerfile*' }
    Get-ChildItem -LiteralPath $repositoryRoot -File |
        Where-Object { $_.Name -match '^(?:docker-)?compose.*\.ya?ml$' }
)
$pinnedImages = $pinnedWorkflowImages
foreach ($file in $containerFiles) {
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        $lineNumber++
        $reference = $null
        if ($line -match '^\s*FROM\s+(?<reference>\S+)') {
            $reference = $Matches['reference']
        } elseif ($line -match '^\s*image:\s*(?<reference>\S+)') {
            $reference = $Matches['reference']
        }
        if (-not $reference) { continue }
        if ($reference.StartsWith('postgres:', [StringComparison]::Ordinal)) {
            $postgresReferences.Add($reference)
        }

        # Release overlay получает собственные GHCR-образы пользователя и проверяет
        # их digest через release metadata/attestation, а не фиксирует чужой namespace.
        if ($reference.Contains('${PROXYHARBOR_IMAGE_PREFIX', [StringComparison]::Ordinal)) { continue }
        if ($reference -match '^[^@\s]+:[^@\s]+@sha256:[0-9a-f]{64}$') {
            $pinnedImages++
            continue
        }
        $relative = [IO.Path]::GetRelativePath($repositoryRoot, $file.FullName)
        $violations.Add("$relative`:$lineNumber использует mutable container reference $reference")
    }
}
if ($pinnedImages -lt $MinimumPinnedContainerReferences) {
    $violations.Add("Найдено только $pinnedImages pinned container references; ожидалось не менее $MinimumPinnedContainerReferences.")
}
$distinctPostgresReferences = @($postgresReferences | Sort-Object -Unique)
if ($postgresReferences.Count -lt $MinimumSynchronizedPostgresReferences) {
    $violations.Add("Найдено только $($postgresReferences.Count) PostgreSQL references; ожидалось не менее $MinimumSynchronizedPostgresReferences.")
}
if ($distinctPostgresReferences.Count -gt 1) {
    $violations.Add("PostgreSQL tag/digest расходятся между Compose и workflows: $($distinctPostgresReferences -join ', ')")
}

if ($violations.Count -gt 0) {
    throw "Supply-chain gate отклонён:`n$($violations -join [Environment]::NewLine)"
}
Write-Host "Supply-chain contract пройден: actions закреплены commit SHA, container references — $pinnedImages tag+digest pins, PostgreSQL references — $($postgresReferences.Count) синхронизированы." -ForegroundColor Green
