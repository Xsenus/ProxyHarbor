[CmdletBinding()]
param(
    [string]$RepositoryPath = (Get-Location).Path,
    [string]$Version = '8.28.0',
    [string]$ExpectedSha256 = 'a65b5253807a68ac0cafa4414031fd740aeb55f54fb7e55f386acb52e6a840eb'
)

$ErrorActionPreference = 'Stop'

if (-not $IsLinux -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'Закреплённый Gitleaks runner поддерживает только Linux x64 (целевую GitHub Actions платформу).'
}

$resolvedRepository = (Resolve-Path -LiteralPath $RepositoryPath).Path
if (-not (Test-Path -LiteralPath (Join-Path $resolvedRepository '.git'))) {
    throw "Gitleaks ожидает корень Git-репозитория: $resolvedRepository"
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-gitleaks-$([Guid]::NewGuid().ToString('N'))"
$archivePath = Join-Path $temporaryRoot 'gitleaks.tar.gz'
$downloadUri = "https://github.com/gitleaks/gitleaks/releases/download/v$Version/gitleaks_${Version}_linux_x64.tar.gz"

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    Invoke-WebRequest -Uri $downloadUri -OutFile $archivePath -UseBasicParsing

    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "SHA-256 Gitleaks archive не совпал: ожидался $ExpectedSha256, получен $actualSha256."
    }

    & tar -xzf $archivePath -C $temporaryRoot gitleaks
    if ($LASTEXITCODE -ne 0) { throw "Не удалось распаковать Gitleaks (exit code $LASTEXITCODE)." }

    $executablePath = Join-Path $temporaryRoot 'gitleaks'
    & chmod +x $executablePath
    if ($LASTEXITCODE -ne 0) { throw "Не удалось сделать Gitleaks исполняемым (exit code $LASTEXITCODE)." }

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
