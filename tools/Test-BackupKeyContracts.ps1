$ErrorActionPreference = 'Stop'

$decryptor = Join-Path $PSScriptRoot 'Decrypt-Backup.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-backup-key-$([Guid]::NewGuid().ToString('N'))"
$missingInput = Join-Path $testRoot 'missing.phb'
$outputZip = Join-Path $testRoot 'output.zip'
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Invoke-FailingDecrypt([string]$EncryptionKey) {
    try {
        & $decryptor -InputFile $missingInput -OutputZip $outputZip -EncryptionKey $EncryptionKey
        throw 'Decrypt-Backup.ps1 неожиданно завершился успешно.'
    } catch {
        return $_.Exception.Message
    }
}

try {
    # Непарный UTF-16 surrogate нельзя молча преобразовывать в replacement character:
    # разные введённые ключи иначе могли бы дать одинаковые байты для PBKDF2.
    $ambiguousKey = ('k' * 15) + [char]0xD800
    $ambiguousError = Invoke-FailingDecrypt -EncryptionKey $ambiguousKey
    if ($ambiguousError -notmatch 'корректной Unicode-кодировкой') {
        throw "Неоднозначный Unicode-ключ отклонён по неверной причине: $ambiguousError"
    }

    # Корректный минимальный PHB2 legacy-ключ остаётся допустимым и должен дойти
    # до следующей независимой проверки входного файла.
    $legacyError = Invoke-FailingDecrypt -EncryptionKey 'legacy-key-16chr'
    if ($legacyError -match 'Unicode-кодировкой' -or $legacyError -notmatch 'Cannot find path|не удается найти путь|не удаётся найти путь') {
        throw "Корректный legacy-ключ отклонён по неверной причине: $legacyError"
    }
    if (Test-Path -LiteralPath $outputZip) {
        throw 'После неуспешной проверки не должен оставаться выходной ZIP.'
    }

    Write-Host 'Контракты Unicode-ключей резервных копий пройдены.' -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
