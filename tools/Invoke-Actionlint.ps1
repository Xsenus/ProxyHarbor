param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ArchivePath
)

$ErrorActionPreference = 'Stop'

# Версия и SHA-256 намеренно хранятся рядом с кодом загрузки: обновление
# исполняемого инструмента требует обычного review изменения репозитория.
$version = '1.7.12'
$artifacts = @{
    'linux-x64' = @{
        FileName = "actionlint_${version}_linux_amd64.tar.gz"
        Sha256 = '8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8'
        Executable = 'actionlint'
    }
    'windows-x64' = @{
        FileName = "actionlint_${version}_windows_amd64.zip"
        Sha256 = '6e7241b51e6817ea6a047693d8e6fed13b31819c9a0dd6c5a726e1592d22f6e9'
        Executable = 'actionlint.exe'
    }
}

if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne
    [Runtime.InteropServices.Architecture]::X64) {
    throw 'Actionlint gate поддерживает только x64 runner; добавьте проверенный artifact pin для этой архитектуры.'
}

$platform = if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Windows)) {
    'windows-x64'
} elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Linux)) {
    'linux-x64'
} else {
    throw 'Actionlint gate поддерживает только Windows и Linux; добавьте проверенный artifact pin для этой ОС.'
}

$artifact = $artifacts[$platform]
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "proxyharbor-actionlint-$([Guid]::NewGuid().ToString('N'))"
$ownsArchive = [string]::IsNullOrWhiteSpace($ArchivePath)

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    if ($ownsArchive) {
        $ArchivePath = Join-Path $temporaryRoot $artifact.FileName
        $downloadUri = "https://github.com/rhysd/actionlint/releases/download/v$version/$($artifact.FileName)"
        Invoke-WebRequest -UseBasicParsing -Uri $downloadUri -OutFile $ArchivePath
    }

    $resolvedArchive = [IO.Path]::GetFullPath($ArchivePath)
    if (-not [IO.File]::Exists($resolvedArchive)) {
        throw "Actionlint archive не найден: $resolvedArchive"
    }

    # Проверка выполняется до распаковки: неподтверждённые bytes никогда не
    # получают возможности создать файл или запуститься как код.
    $actualHash = (Get-FileHash -LiteralPath $resolvedArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $artifact.Sha256) {
        throw "Actionlint archive SHA-256 mismatch: ожидался $($artifact.Sha256), получен $actualHash."
    }

    $extractRoot = Join-Path $temporaryRoot 'extracted'
    [IO.Directory]::CreateDirectory($extractRoot) | Out-Null
    if ($platform -eq 'windows-x64') {
        Expand-Archive -LiteralPath $resolvedArchive -DestinationPath $extractRoot
    } else {
        & tar -xzf $resolvedArchive -C $extractRoot
        if ($LASTEXITCODE -ne 0) { throw "Не удалось распаковать actionlint (tar exit code $LASTEXITCODE)." }
    }

    $executable = Join-Path $extractRoot $artifact.Executable
    if (-not [IO.File]::Exists($executable)) {
        throw "Проверенный actionlint archive не содержит $($artifact.Executable)."
    }

    $resolvedRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    $workflowRoot = Join-Path $resolvedRoot '.github/workflows'
    $workflowFiles = @(Get-ChildItem -LiteralPath $workflowRoot -File |
        Where-Object { $_.Extension -in '.yml', '.yaml' } |
        Sort-Object FullName |
        ForEach-Object { $_.FullName })
    if ($workflowFiles.Count -eq 0) { throw 'Не найдено GitHub Actions workflows для проверки.' }

    & $executable @workflowFiles
    if ($LASTEXITCODE -ne 0) { throw "Actionlint отклонил workflow (exit code $LASTEXITCODE)." }
    Write-Host "Actionlint v$version проверил $($workflowFiles.Count) workflow-файлов." -ForegroundColor Green
} finally {
    # Удаляется только созданный этим процессом GUID-каталог внутри системного temp.
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemporaryRoot.StartsWith($resolvedSystemTemp, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Directory]::Exists($resolvedTemporaryRoot)) {
        [IO.Directory]::Delete($resolvedTemporaryRoot, $true)
    }
}
