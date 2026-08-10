[CmdletBinding(DefaultParameterSetName = 'KeyFile')]
param(
    [Parameter(Mandatory)][string]$InputFile,
    [Parameter(Mandatory)][string]$OutputZip,
    [Parameter(Mandatory, ParameterSetName = 'InlineKey')][string]$EncryptionKey,
    [Parameter(Mandatory, ParameterSetName = 'KeyFile')][string]$EncryptionKeyFile
)

$ErrorActionPreference = 'Stop'
$inputStream = $null
$reader = $null
$outputStream = $null
$key = $null
$passwordBytes = $null
$keyFileBytes = $null
$keyCharacters = $null
$keyStream = $null
$aes = $null
$partialOutput = $null

try {
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    if ($PSCmdlet.ParameterSetName -eq 'KeyFile') {
        if (-not [IO.Path]::IsPathFullyQualified($EncryptionKeyFile)) {
            throw 'EncryptionKeyFile должен быть абсолютным путём.'
        }
        $resolvedKeyFile = (Resolve-Path -LiteralPath $EncryptionKeyFile).Path
        $keyFile = Get-Item -LiteralPath $resolvedKeyFile
        if ($keyFile -isnot [IO.FileInfo]) { throw 'EncryptionKeyFile должен указывать на обычный файл.' }

        # Один descriptor устраняет гонку между отдельными проверкой размера и чтением.
        # Изменившийся во время чтения secret отклоняется вместо частичного использования.
        $keyStream = [IO.File]::Open(
            $resolvedKeyFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($keyStream.Length -gt 16384) { throw 'EncryptionKeyFile превышает безопасный лимит 16 КиБ.' }
        $keyFileBytes = [byte[]]::new([int]$keyStream.Length)
        $totalRead = 0
        while ($totalRead -lt $keyFileBytes.Length) {
            $read = $keyStream.Read($keyFileBytes, $totalRead, $keyFileBytes.Length - $totalRead)
            if ($read -eq 0) { throw 'EncryptionKeyFile изменился во время чтения.' }
            $totalRead += $read
        }
        if ($keyStream.ReadByte() -ne -1) { throw 'EncryptionKeyFile изменился во время чтения.' }
        $keyStream.Dispose()
        $keyStream = $null

        $offset = if ($keyFileBytes.Length -ge 3 -and $keyFileBytes[0] -eq 0xEF -and
            $keyFileBytes[1] -eq 0xBB -and $keyFileBytes[2] -eq 0xBF) { 3 } else { 0 }
        $EncryptionKey = $strictUtf8.GetString($keyFileBytes, $offset, $keyFileBytes.Length - $offset)
        # Совпадает с container secret loader: удаляется ровно один терминальный LF/CRLF,
        # но внутренний newline и одиночный CR остаются и затем отклоняются как control.
        if ($EncryptionKey.EndsWith("`r`n", [StringComparison]::Ordinal)) {
            $EncryptionKey = $EncryptionKey.Substring(0, $EncryptionKey.Length - 2)
        } elseif ($EncryptionKey.EndsWith("`n", [StringComparison]::Ordinal)) {
            $EncryptionKey = $EncryptionKey.Substring(0, $EncryptionKey.Length - 1)
        }
    }

    $keyHasControl = $false
    $keyHasInvalidSurrogate = $false
    $keyCharacters = $EncryptionKey.ToCharArray()
    for ($index = 0; $index -lt $keyCharacters.Length; $index++) {
        $character = $keyCharacters[$index]
        if ([char]::IsControl($character)) {
            $keyHasControl = $true
            break
        }
        if ([char]::IsHighSurrogate($character)) {
            if ($index + 1 -ge $keyCharacters.Length -or -not [char]::IsLowSurrogate($keyCharacters[$index + 1])) {
                $keyHasInvalidSurrogate = $true
                break
            }
            $index++
        } elseif ([char]::IsLowSurrogate($character)) {
            $keyHasInvalidSurrogate = $true
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($EncryptionKey) -or $EncryptionKey.Length -lt 16 -or
        $EncryptionKey.Length -gt 1024 -or $keyHasControl -or $keyHasInvalidSurrogate) {
        throw 'Ключ расшифрования должен содержать 16..1024 символа с корректной Unicode-кодировкой без управляющих знаков.'
    }

    $resolvedInput = (Resolve-Path -LiteralPath $InputFile).Path
    $resolvedOutput = [IO.Path]::GetFullPath($OutputZip)
    if (Test-Path -LiteralPath $resolvedOutput) { throw 'Выходной ZIP уже существует.' }
    $outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
    if (-not [IO.Directory]::Exists($outputDirectory)) { throw 'Каталог выходного ZIP не существует.' }

    $inputStream = [IO.File]::OpenRead($resolvedInput)
    $reader = [IO.BinaryReader]::new($inputStream)
    # Plaintext появляется только в уникальном sibling-файле без sharing. Финальный
    # Move без overwrite публикует уже аутентифицированный ZIP атомарно и не позволяет
    # конкурентно созданному OutputZip быть затёртым либо удалённым при ошибке.
    $partialOutput = [IO.Path]::Combine(
        $outputDirectory, ".proxyharbor-restore-$([Guid]::NewGuid().ToString('N')).partial")
    $outputStream = [IO.File]::Open(
        $partialOutput, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)

    # PHB2/PHB3 обрабатываются потоково: память не зависит от размера резервной копии.
    $magic = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
    if ($magic -ne 'PHB2' -and $magic -ne 'PHB3') { throw 'Файл не является резервной копией ProxyHarbor.' }
    $salt = $reader.ReadBytes(16)
    $chunkSize = $reader.ReadInt32()
    if ($salt.Length -ne 16 -or $chunkSize -lt 65536 -or $chunkSize -gt 16777216) {
        throw 'Заголовок резервной копии повреждён.'
    }

    # Строгая UTF-8 кодировка не допускает replacement bytes; временное представление
    # ключа очищается в finally вместе с результатом PBKDF2.
    $passwordBytes = $strictUtf8.GetBytes($EncryptionKey)
    $key = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        $passwordBytes, $salt, 200000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    $aes = [Security.Cryptography.AesGcm]::new($key, 16)
    [long]$blockIndex = 0
    while ($true) {
        $length = $reader.ReadInt32()
        if ($length -lt 0 -or $length -gt $chunkSize) { throw 'Повреждён размер зашифрованного блока.' }
        if ($length -eq 0 -and $magic -eq 'PHB2') { break }
        $nonce = $reader.ReadBytes(12)
        $tag = $reader.ReadBytes(16)
        $ciphertext = $reader.ReadBytes($length)
        if ($nonce.Length -ne 12 -or $tag.Length -ne 16 -or $ciphertext.Length -ne $length) {
            throw 'Резервная копия оборвана.'
        }
        $plaintext = [byte[]]::new($length)
        try {
            if ($magic -eq 'PHB3') {
                # Associated data связывает блок с заголовком, позицией и длиной.
                $associatedData = [byte[]]::new(36)
                [Text.Encoding]::ASCII.GetBytes('PHB3').CopyTo($associatedData, 0)
                $salt.CopyTo($associatedData, 4)
                [BitConverter]::GetBytes([int]$chunkSize).CopyTo($associatedData, 20)
                [BitConverter]::GetBytes([long]$blockIndex).CopyTo($associatedData, 24)
                [BitConverter]::GetBytes([int]$length).CopyTo($associatedData, 32)
                $aes.Decrypt($nonce, $ciphertext, $tag, $plaintext, $associatedData)
            } else {
                $aes.Decrypt($nonce, $ciphertext, $tag, $plaintext)
            }
            $outputStream.Write($plaintext, 0, $plaintext.Length)
        } finally {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($plaintext)
        }
        if ($length -eq 0) { break }
        $blockIndex++
    }
    if ($inputStream.Position -ne $inputStream.Length) { throw 'После завершающего блока обнаружены лишние данные.' }

    # Flush(true) синхронизирует содержимое до публикации. Move с overwrite=false
    # является последней защитой от TOCTOU после начальной проверки существования.
    $outputStream.Flush($true)
    $outputStream.Dispose()
    $outputStream = $null
    [IO.File]::Move($partialOutput, $resolvedOutput, $false)
    $partialOutput = $null
} finally {
    try {
        if ($null -ne $keyCharacters) { [Array]::Clear($keyCharacters, 0, $keyCharacters.Length) }
        if ($null -ne $keyFileBytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($keyFileBytes) }
        if ($null -ne $passwordBytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($passwordBytes) }
        if ($null -ne $key) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($key) }
        if ($null -ne $aes) { $aes.Dispose() }
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $inputStream) { $inputStream.Dispose() }
        if ($null -ne $keyStream) { $keyStream.Dispose() }
    } finally {
        # Даже исключение одного из cleanup-шагов не должно пропустить закрытие и
        # удаление plaintext. File.Delete намеренно не подавляет отказ удаления.
        try {
            if ($null -ne $outputStream) { $outputStream.Dispose() }
        } finally {
            try {
                if ($null -ne $partialOutput) { [IO.File]::Delete($partialOutput) }
            } finally {
                $EncryptionKey = $null
            }
        }
    }
}
