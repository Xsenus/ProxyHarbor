$ErrorActionPreference = 'Stop'
$workflowRoot = Join-Path (Split-Path -Parent $PSScriptRoot) '.github/workflows'
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

if ($violations.Count -gt 0) {
    throw "Workflow supply-chain gate отклонён:`n$($violations -join [Environment]::NewLine)"
}
Write-Host 'Workflow security contract пройден: все внешние actions закреплены полными commit SHA.' -ForegroundColor Green
