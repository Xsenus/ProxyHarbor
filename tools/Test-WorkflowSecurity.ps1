$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workflowRoot = Join-Path $repositoryRoot '.github/workflows'
$violations = [Collections.Generic.List[string]]::new()

# Tag major-version у action подвижен и способен изменить исполняемый код без
# review репозитория. Разрешаются только локальные actions либо полный SHA-1.
foreach ($workflow in Get-ChildItem -LiteralPath $workflowRoot -Filter '*.yml') {
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $workflow.FullName) {
        $lineNumber++
        if ($line -notmatch '^\s*(?:-\s*)?uses:\s*(?<reference>\S+)') { continue }
        $reference = $Matches['reference']
        if ($reference.StartsWith('./', [StringComparison]::Ordinal) -or
            $reference -match '^[^@\s]+@[0-9a-f]{40}$') { continue }
        $violations.Add("$($workflow.Name):$lineNumber использует неприкреплённый action $reference")
    }
}

# Version tag сохраняет читаемость и позволяет Dependabot предложить upgrade,
# digest одновременно делает фактические build/runtime bytes неизменяемыми.
$containerFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -Filter 'Dockerfile'
    Get-ChildItem -LiteralPath $repositoryRoot -Filter 'docker-compose*.yml'
)
$pinnedImages = 0
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
if ($pinnedImages -lt 10) {
    $violations.Add("Найдено только $pinnedImages pinned container references; ожидалось не менее 10.")
}

if ($violations.Count -gt 0) {
    throw "Supply-chain gate отклонён:`n$($violations -join [Environment]::NewLine)"
}
Write-Host "Supply-chain contract пройден: actions закреплены commit SHA, container references — $pinnedImages tag+digest pins." -ForegroundColor Green
