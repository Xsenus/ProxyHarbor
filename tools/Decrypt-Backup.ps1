param(
    [Parameter(Mandatory)][string]$InputFile,
    [Parameter(Mandatory)][string]$OutputZip,
    [Parameter(Mandatory)][string]$EncryptionKey
)

$ErrorActionPreference = 'Stop'
$inputStream = $null
$reader = $null
$outputStream = $null
$key = $null
$aes = $null

try {
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
    if (Test-Path -LiteralPath $OutputZip) { throw 'Выходной ZIP уже существует.' }
    $inputStream = [IO.File]::OpenRead($resolvedInput)
    $reader = [IO.BinaryReader]::new($inputStream)
    $outputStream = [IO.File]::Create($OutputZip)

    # PHB2/PHB3 обрабатываются потоково: память не зависит от размера резервной копии.
    $magic = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
    if ($magic -ne 'PHB2' -and $magic -ne 'PHB3') { throw 'Файл не является резервной копией ProxyHarbor.' }
    $salt = $reader.ReadBytes(16)
    $chunkSize = $reader.ReadInt32()
    if ($salt.Length -ne 16 -or $chunkSize -lt 65536 -or $chunkSize -gt 16777216) {
        throw 'Заголовок резервной копии повреждён.'
    }

    $key = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        $EncryptionKey, $salt, 200000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
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
} catch {
    if ($null -ne $outputStream) { $outputStream.Dispose(); $outputStream = $null }
    Remove-Item -LiteralPath $OutputZip -ErrorAction SilentlyContinue
    throw
} finally {
    if ($null -ne $key) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($key) }
    if ($null -ne $aes) { $aes.Dispose() }
    if ($null -ne $reader) { $reader.Dispose() }
    if ($null -ne $inputStream) { $inputStream.Dispose() }
    if ($null -ne $outputStream) { $outputStream.Dispose() }
}
