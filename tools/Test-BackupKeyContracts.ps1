$ErrorActionPreference = 'Stop'

$decryptor = Join-Path $PSScriptRoot 'Decrypt-Backup.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-backup-key-$([Guid]::NewGuid().ToString('N'))"
$missingInput = Join-Path $testRoot 'missing.phb'
$legacyInput = Join-Path $testRoot 'legacy-unicode.phb'
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

    # Создаём PHB2 прежним string-overload PBKDF2 и расшифровываем обновлённым
    # byte-overload. Это закрепляет совместимость корректных non-ASCII legacy-ключей.
    $unicodeLegacyKey = ('k' * 14) + [char]::ConvertFromUtf32(0x1F680)
    $plaintext = [Text.Encoding]::UTF8.GetBytes('proxyharbor-unicode-contract')
    $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $legacyDerivedKey = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        $unicodeLegacyKey, $salt, 200000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    $nonce = [Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
    $tag = [byte[]]::new(16)
    $ciphertext = [byte[]]::new($plaintext.Length)
    $aes = [Security.Cryptography.AesGcm]::new($legacyDerivedKey, 16)
    $stream = $null
    $writer = $null
    try {
        $aes.Encrypt($nonce, $plaintext, $ciphertext, $tag)
        $stream = [IO.File]::Create($legacyInput)
        $writer = [IO.BinaryWriter]::new($stream)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('PHB2'))
        $writer.Write($salt)
        $writer.Write([int]1048576)
        $writer.Write([int]$plaintext.Length)
        $writer.Write($nonce)
        $writer.Write($tag)
        $writer.Write($ciphertext)
        $writer.Write([int]0)
    } finally {
        if ($null -ne $writer) { $writer.Dispose() } elseif ($null -ne $stream) { $stream.Dispose() }
        $aes.Dispose()
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($legacyDerivedKey)
    }

    & $decryptor -InputFile $legacyInput -OutputZip $outputZip -EncryptionKey $unicodeLegacyKey
    if (-not [Linq.Enumerable]::SequenceEqual($plaintext, [IO.File]::ReadAllBytes($outputZip))) {
        throw 'PowerShell restore изменил содержимое корректного Unicode PHB2-архива.'
    }

    Write-Host 'Контракты Unicode-ключей и PHB2-совместимости резервных копий пройдены.' -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
