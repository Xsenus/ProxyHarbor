[CmdletBinding()]
param(
    [string]$RepositoryPath = (Get-Location).Path,
    [string]$Version = '8.28.0',
    [string]$ExpectedSha256 = 'a65b5253807a68ac0cafa4414031fd740aeb55f54fb7e55f386acb52e6a840eb',
    [string]$ExpectedWindowsSha256 = 'da6458e8864af553807de1c46a7a8eac0880bd6b99ba56288e87e86a45af884f'
)

$ErrorActionPreference = 'Stop'

if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'Закреплённый Gitleaks runner поддерживает только x64.'
}

if ($IsLinux) {
    $assetName = "gitleaks_${Version}_linux_x64.tar.gz"
    $expectedPlatformSha256 = $ExpectedSha256
    $executableName = 'gitleaks'
}
elseif ($IsWindows) {
    $assetName = "gitleaks_${Version}_windows_x64.zip"
    $expectedPlatformSha256 = $ExpectedWindowsSha256
    $executableName = 'gitleaks.exe'
}
else {
    throw 'Закреплённый Gitleaks runner поддерживает только Linux x64 и Windows x64.'
}

$resolvedRepository = (Resolve-Path -LiteralPath $RepositoryPath).Path
if (-not (Test-Path -LiteralPath (Join-Path $resolvedRepository '.git'))) {
    throw "Gitleaks ожидает корень Git-репозитория: $resolvedRepository"
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-gitleaks-$([Guid]::NewGuid().ToString('N'))"
$archivePath = Join-Path $temporaryRoot $assetName
$downloadUri = "https://github.com/gitleaks/gitleaks/releases/download/v$Version/$assetName"

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    Invoke-WebRequest -Uri $downloadUri -OutFile $archivePath -UseBasicParsing

    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedPlatformSha256.ToLowerInvariant()) {
        throw "SHA-256 Gitleaks archive не совпал: ожидался $expectedPlatformSha256, получен $actualSha256."
    }

    if ($IsWindows) {
        Expand-Archive -LiteralPath $archivePath -DestinationPath $temporaryRoot
    }
    else {
        & tar -xzf $archivePath -C $temporaryRoot gitleaks
        if ($LASTEXITCODE -ne 0) { throw "Не удалось распаковать Gitleaks (exit code $LASTEXITCODE)." }
    }

    $executablePath = Join-Path $temporaryRoot $executableName
    if ($IsLinux) {
        & chmod +x $executablePath
        if ($LASTEXITCODE -ne 0) { throw "Не удалось сделать Gitleaks исполняемым (exit code $LASTEXITCODE)." }
    }

    # `git` сканирует всю достижимую историю, а redaction не выводит найденные значения в CI log.
    & $executablePath git $resolvedRepository `
        --gitleaks-ignore-path (Join-Path $resolvedRepository '.gitleaksignore') `
        --redact --no-banner --exit-code 1
    if ($LASTEXITCODE -ne 0) { throw "Gitleaks обнаружил секрет либо завершился с ошибкой (exit code $LASTEXITCODE)." }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host "Gitleaks v${Version}: история репозитория не содержит известных секретов." -ForegroundColor Green
