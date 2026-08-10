$ErrorActionPreference = 'Stop'

$joiner = Join-Path $PSScriptRoot 'Join-BackupParts.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-backup-parts-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Write-Part([string]$Name, [byte[]]$Bytes) {
    [IO.File]::WriteAllBytes((Join-Path $testRoot $Name), $Bytes)
}

function Assert-Rejected([string]$Pattern, [string]$OutputName, [string]$ExpectedMessage) {
    $output = Join-Path $testRoot $OutputName
    $errorMessage = $null
    try {
        & $joiner -PartsPattern (Join-Path $testRoot $Pattern) -OutputFile $output
    } catch {
        $errorMessage = $_.Exception.Message
    }
    if (-not $errorMessage -or $errorMessage -notlike "*$ExpectedMessage*") {
        throw "Join-контракт ожидал ошибку '$ExpectedMessage', получено: $errorMessage"
    }
    if (Test-Path -LiteralPath $output) { throw "После отказа остался partial output $OutputName." }
}

try {
    Write-Part 'valid.phbackup.part001-of-003' ([byte[]](1, 2, 3, 4))
    Write-Part 'valid.phbackup.part002-of-003' ([byte[]](5, 6, 7, 8))
    Write-Part 'valid.phbackup.part003-of-003' ([byte[]](9, 10))
    $validOutput = Join-Path $testRoot 'valid.phbackup'
    & $joiner -PartsPattern (Join-Path $testRoot 'valid.phbackup.part*-of-*') -OutputFile $validOutput
    if (-not [Linq.Enumerable]::SequenceEqual(
        [byte[]](1, 2, 3, 4, 5, 6, 7, 8, 9, 10), [IO.File]::ReadAllBytes($validOutput))) {
        throw 'Корректный набор частей объединён с потерей или перестановкой данных.'
    }

    Write-Part 'missing.phbackup.part001-of-003' ([byte[]](1, 2))
    Write-Part 'missing.phbackup.part003-of-003' ([byte[]](3))
    Assert-Rejected 'missing.phbackup.part*-of-*' 'missing-output.phbackup' 'Неполный набор'

    Write-Part 'mixed-a.phbackup.part001-of-002' ([byte[]](1, 2))
    Write-Part 'mixed-a.phbackup.part002-of-002' ([byte[]](3))
    Write-Part 'mixed-b.phbackup.part001-of-002' ([byte[]](4, 5))
    Write-Part 'mixed-b.phbackup.part002-of-002' ([byte[]](6))
    Assert-Rejected 'mixed-*.phbackup.part*-of-*' 'mixed-output.phbackup' 'разных резервных копий'

    Write-Part 'malformed-part-one' ([byte[]](1))
    Write-Part 'malformed-part-two' ([byte[]](2))
    Assert-Rejected 'malformed-*' 'malformed-output.phbackup' 'Некорректное имя'

    foreach ($number in 1..21) {
        Write-Part ("oversized.phbackup.part{0:D3}-of-021" -f $number) ([byte[]]$number)
    }
    Assert-Rejected 'oversized.phbackup.part*-of-*' 'oversized-output.phbackup' 'превышает предел 20'

    Write-Host 'Контракты сборки Telegram backup-частей пройдены: valid, missing, mixed, malformed и bounded count.' -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
