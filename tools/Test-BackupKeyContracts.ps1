$ErrorActionPreference = 'Stop'

$decryptor = Join-Path $PSScriptRoot 'Decrypt-Backup.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-backup-key-$([Guid]::NewGuid().ToString('N'))"
$missingInput = Join-Path $testRoot 'missing.phb'
$legacyInput = Join-Path $testRoot 'legacy-unicode.phb'
$outputZip = Join-Path $testRoot 'output.zip'
$unicodeKeyFile = Join-Path $testRoot 'unicode-key.secret'
$emptyKeyFile = Join-Path $testRoot 'empty-key.secret'
$oversizedKeyFile = Join-Path $testRoot 'oversized-key.secret'
$invalidUtf8KeyFile = Join-Path $testRoot 'invalid-utf8-key.secret'
$directoryKeyFile = Join-Path $testRoot 'key-directory'
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

    [IO.File]::WriteAllBytes($emptyKeyFile, [byte[]]::new(0))
    try {
        & $decryptor -InputFile $missingInput -OutputZip $outputZip -EncryptionKeyFile $emptyKeyFile
        throw 'Пустой key-file неожиданно принят.'
    } catch {
        if ($_.Exception.Message -notmatch '16..1024') {
            throw "Пустой key-file отклонён по неверной причине: $($_.Exception.Message)"
        }
    }

    [IO.File]::WriteAllBytes($oversizedKeyFile, [byte[]]::new(16385))
    try {
        & $decryptor -InputFile $missingInput -OutputZip $outputZip -EncryptionKeyFile $oversizedKeyFile
        throw 'Oversized key-file неожиданно принят.'
    } catch {
        if ($_.Exception.Message -notmatch '16 КиБ') {
            throw "Oversized key-file отклонён по неверной причине: $($_.Exception.Message)"
        }
    }

    [IO.File]::WriteAllBytes($invalidUtf8KeyFile, [byte[]](0xC3, 0x28))
    try {
        & $decryptor -InputFile $missingInput -OutputZip $outputZip -EncryptionKeyFile $invalidUtf8KeyFile
        throw 'Invalid UTF-8 key-file неожиданно принят.'
    } catch {
        if (Test-Path -LiteralPath $outputZip) {
            throw 'Invalid UTF-8 key-file оставил выходной ZIP.'
        }
    }

    Push-Location $testRoot
    try {
        try {
            & $decryptor -InputFile $missingInput -OutputZip $outputZip -EncryptionKeyFile 'empty-key.secret'
            throw 'Относительный key-file неожиданно принят.'
        } catch {
            if ($_.Exception.Message -notmatch 'абсолютным путём') {
                throw "Относительный key-file отклонён по неверной причине: $($_.Exception.Message)"
            }
        }
    } finally {
        Pop-Location
    }

    New-Item -ItemType Directory -Path $directoryKeyFile | Out-Null
    try {
        & $decryptor -InputFile $missingInput -OutputZip $outputZip -EncryptionKeyFile $directoryKeyFile
        throw 'Каталог вместо key-file неожиданно принят.'
    } catch {
        if ($_.Exception.Message -notmatch 'обычный файл') {
            throw "Каталог вместо key-file отклонён по неверной причине: $($_.Exception.Message)"
        }
    }

    $ambiguousParametersRejected = $false
    try {
        & $decryptor -InputFile $missingInput -OutputZip $outputZip `
            -EncryptionKey ('k' * 16) -EncryptionKeyFile $emptyKeyFile
    } catch {
        $ambiguousParametersRejected = $true
    }
    if (-not $ambiguousParametersRejected) {
        throw 'Одновременные EncryptionKey и EncryptionKeyFile неожиданно приняты.'
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

    # BOM и один терминальный CRLF удаляются так же, как Docker secret loader.
    [IO.File]::WriteAllText($unicodeKeyFile, "$unicodeLegacyKey`r`n", [Text.UTF8Encoding]::new($true, $true))
    & $decryptor -InputFile $legacyInput -OutputZip $outputZip -EncryptionKeyFile $unicodeKeyFile
    if (-not [Linq.Enumerable]::SequenceEqual($plaintext, [IO.File]::ReadAllBytes($outputZip))) {
        throw 'PowerShell restore изменил содержимое корректного Unicode PHB2-архива.'
    }

    Write-Host 'Контракты key-file, Unicode-ключей и PHB2-совместимости резервных копий пройдены.' -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
