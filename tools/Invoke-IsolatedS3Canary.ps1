#Requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$Bucket,
    [string]$Endpoint = 'https://s3-nl.hostkey.com',
    [string]$Region = 'nl',
    [string]$CredentialPath,
    [string]$ConfirmBucket,
    [switch]$Execute
)

$ErrorActionPreference = 'Stop'
$prefix = 'proxyharbor-drill'
if ($Bucket.Length -lt 3 -or $Bucket.Length -gt 63 -or
    $Bucket -cnotmatch '^[a-z0-9][a-z0-9.-]*[a-z0-9]$' -or $Bucket.Contains('..')) {
    throw 'Некорректное имя тестового bucket.'
}
if ($Endpoint -ne 'https://s3-nl.hostkey.com' -or $Region -ne 'nl') {
    throw 'Этот canary ограничен подтверждённым HOSTKEY NL endpoint и регионом.'
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$runtime = Join-Path $repoRoot 'src/ProxyHarbor.Restore/bin/Release/net10.0'
foreach ($assembly in @('AWSSDK.Core.dll', 'AWSSDK.S3.dll', 'ProxyHarbor.Domain.dll', 'ProxyHarbor.Infrastructure.dll')) {
    $assemblyPath = Join-Path $runtime $assembly
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw 'Сначала выполните dotnet build ProxyHarbor.slnx -c Release.'
    }
    Add-Type -Path $assemblyPath
}

if (-not $Execute) {
    Write-Host "Dry run: $Endpoint / $Region / $Bucket / $prefix/; no provider I/O."
    return
}
if (-not [OperatingSystem]::IsWindows()) {
    throw 'Этот canary требует Windows DPAPI Export-Clixml для временного credential-файла.'
}
if ($ConfirmBucket -cne $Bucket) {
    throw 'Для -Execute повторно укажите точное имя bucket в -ConfirmBucket.'
}
if ([string]::IsNullOrWhiteSpace($CredentialPath) -or
    -not (Test-Path -LiteralPath $CredentialPath -PathType Leaf)) {
    throw 'Для -Execute нужен локальный DPAPI Export-Clixml PSCredential вне репозитория.'
}
$credentialFullPath = [IO.Path]::GetFullPath($CredentialPath)
if ($credentialFullPath.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'CredentialPath должен находиться вне репозитория.'
}
$credential = Import-Clixml -LiteralPath $credentialFullPath
if ($credential -isnot [pscredential]) {
    throw 'CredentialPath не содержит DPAPI Export-Clixml PSCredential.'
}

$options = [ProxyHarbor.Infrastructure.BackupOptions]::new()
$options.ObjectStorageEndpoint = $Endpoint
$options.ObjectStorageRegion = $Region
$options.ObjectStorageBucket = $Bucket
$options.ObjectStoragePrefix = $prefix
$options.ObjectStorageUsePathStyle = $true
$options.ObjectStorageAccessKey = $credential.UserName
$options.ObjectStorageSecretKey = $credential.GetNetworkCredential().Password
if (-not [ProxyHarbor.Infrastructure.BackupOptions]::IsObjectStorageConfigurationValid($options)) {
    throw 'S3 конфигурация не прошла project validation.'
}

$name = 'canary-' + [guid]::NewGuid().ToString('N') + '.phbackup'
$objectKey = [ProxyHarbor.Infrastructure.S3BackupObjectStorageTransport]::BuildObjectKey($prefix, $name)
$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ('proxyharbor-s3-canary-' + [guid]::NewGuid().ToString('N'))
$sourcePath = Join-Path $tempDirectory $name
$resultPath = Join-Path $tempDirectory ('materialized-' + $name)
$transport = [ProxyHarbor.Infrastructure.S3BackupObjectStorageTransport]::new()
$deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(2))
$token = $deadline.Token
$failure = $null
$cleanupFailures = [Collections.Generic.List[string]]::new()
$wroteObject = $false
$cleanupTargets = [Collections.Generic.List[object]]::new()

try {
    [IO.Directory]::CreateDirectory($tempDirectory) | Out-Null
    $plainBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(4096)
    $password = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
    $plainStream = [IO.MemoryStream]::new($plainBytes, $false)
    try {
        [ProxyHarbor.Infrastructure.BackupEncryption]::EncryptAsync(
            $plainStream, $sourcePath, $password, $token).GetAwaiter().GetResult()
    } finally { $plainStream.Dispose() }
    $bytes = [IO.File]::ReadAllBytes($sourcePath)
    $sha256 = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
    $cleanupTargets.Add([pscustomobject]@{ Key = $objectKey; Size = $bytes.Length; Hash = $sha256 })

    $upload = $transport.UploadAndVerifyDetailedAsync($sourcePath, $options, $token).GetAwaiter().GetResult()
    if ($upload.ObjectKey -cne $objectKey -or $upload.SizeBytes -ne $bytes.Length -or
        $upload.Sha256 -cne $sha256) { throw 'PUT+HEAD вернул другую identity.' }
    $wroteObject = $true

    $materialized = $transport.MaterializeAndVerifyAsync(
        $objectKey, $resultPath, $bytes.Length, $sha256, $options, $token).GetAwaiter().GetResult()
    if ($materialized.SizeBytes -ne $bytes.Length -or $materialized.Sha256 -cne $sha256 -or
        [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData(
            [IO.File]::ReadAllBytes($resultPath))) -cne $sha256) {
        throw 'GET вернул другие bytes.'
    }
    [ProxyHarbor.Infrastructure.BackupEncryption]::VerifyAsync(
        $resultPath, $password, $token).GetAwaiter().GetResult()

    $collision = $false
    try {
        $null = $transport.UploadAndVerifyDetailedAsync($sourcePath, $options, $token).GetAwaiter().GetResult()
    } catch {
        $inner = $_.Exception
        while ($inner -isnot [ProxyHarbor.Infrastructure.BackupDestinationOperationException] -and
            $inner.InnerException) { $inner = $inner.InnerException }
        if ($inner -is [ProxyHarbor.Infrastructure.BackupDestinationOperationException] -and
            $inner.Failure.Code -eq [ProxyHarbor.Infrastructure.BackupDestinationErrorCode]::Collision) {
            $collision = $true
        } else { throw }
    }
    if (-not $collision) { throw 'Provider не подтвердил conditional PUT collision.' }

    $verifiedAt = [DateTimeOffset]::UtcNow
    $copy = [ProxyHarbor.Infrastructure.BackupCatalogCopy]::new(
        [guid]::NewGuid(), [guid]::NewGuid(), 's3', $objectKey, 0, $verifiedAt)
    $snapshot = [ProxyHarbor.Infrastructure.BackupCatalogSnapshot]::new(
        [guid]::NewGuid(), $name, $bytes.Length, $sha256, 1,
        $verifiedAt.AddSeconds(-1), $verifiedAt, 'canary-v1',
        [ProxyHarbor.Infrastructure.BackupCatalogCopy[]]@($copy))
    $signingKey = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
    $catalogBytes = [ProxyHarbor.Infrastructure.BackupCatalogService]::SealForCopySidecar(
        $snapshot, $signingKey)
    $catalogKey = "$objectKey.catalog.v1.json"
    $catalogHash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($catalogBytes))
    $cleanupTargets.Insert(0, [pscustomobject]@{
        Key = $catalogKey; Size = $catalogBytes.Length; Hash = $catalogHash
    })
    $catalogResult = $transport.PublishCatalogAsync(
        $objectKey, [ReadOnlyMemory[byte]]::new($catalogBytes), $options, $token
    ).GetAwaiter().GetResult()
    if ($catalogResult.ObjectKey -cne $catalogKey -or
        $catalogResult.SizeBytes -ne $catalogBytes.Length -or
        $catalogResult.Sha256 -cne $catalogHash) {
        throw 'Catalog PUT+HEAD+GET вернул другую identity.'
    }
    $awsCredentials = [Amazon.Runtime.BasicAWSCredentials]::new(
        $options.ObjectStorageAccessKey, $options.ObjectStorageSecretKey)
    $config = [Amazon.S3.AmazonS3Config]::new()
    $config.ServiceURL = $Endpoint
    $config.AuthenticationRegion = $Region
    $config.ForcePathStyle = $true
    $client = [Amazon.S3.AmazonS3Client]::new($awsCredentials, $config)
    try {
        $get = [Amazon.S3.Model.GetObjectRequest]::new()
        $get.BucketName = $Bucket
        $get.Key = $catalogKey
        $response = $client.GetObjectAsync($get, $token).GetAwaiter().GetResult()
        try {
            $downloaded = [IO.MemoryStream]::new()
            try {
                $response.ResponseStream.CopyToAsync($downloaded, $token).GetAwaiter().GetResult()
                $downloadedBytes = $downloaded.ToArray()
            } finally { $downloaded.Dispose() }
        } finally { $response.Dispose() }
    } finally { $client.Dispose() }
    if ($downloadedBytes.Length -ne $catalogBytes.Length -or
        [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($downloadedBytes)) -cne
        $catalogHash) { throw 'Catalog GET вернул другие bytes.' }
    $opened = [ProxyHarbor.Infrastructure.BackupCatalogService]::Open(
        $downloadedBytes, $signingKey)
    if ($opened.BackupRunId -ne $snapshot.BackupRunId -or
        $opened.Copies[0].NativeLocator -cne $objectKey) {
        throw 'Скачанный catalog не прошёл binding к PHB3.'
    }
    Write-Host "Canary PHB3 PUT/HEAD/GET/conditional collision and signed catalog passed: $Bucket/$objectKey."
} catch {
    $inner = $_.Exception
    while ($inner -isnot [ProxyHarbor.Infrastructure.BackupDestinationOperationException] -and
        $inner.InnerException) { $inner = $inner.InnerException }
    if ($inner -is [ProxyHarbor.Infrastructure.BackupDestinationOperationException]) {
        $failure = "S3 canary failed: $($inner.Failure.Code) / $($inner.Failure.Disposition)."
    } else {
        $failure = "S3 canary failed: $($inner.GetType().Name)."
    }
} finally {
    $cleanupDeadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(1))
    $cleanupToken = $cleanupDeadline.Token
    # Delete only if independent HEAD proves the exact random body. This also handles
    # an uncertain PUT response without deleting an unrelated object at the key.
    foreach ($target in $cleanupTargets) {
        try {
            $verified = $transport.VerifyAsync(
                $target.Key, $target.Size, $target.Hash, $options, $cleanupToken
            ).GetAwaiter().GetResult()
            $awsCredentials = [Amazon.Runtime.BasicAWSCredentials]::new(
                $options.ObjectStorageAccessKey, $options.ObjectStorageSecretKey)
            $config = [Amazon.S3.AmazonS3Config]::new()
            $config.ServiceURL = $Endpoint
            $config.AuthenticationRegion = $Region
            $config.ForcePathStyle = $true
            $client = [Amazon.S3.AmazonS3Client]::new($awsCredentials, $config)
            try {
                $delete = [Amazon.S3.Model.DeleteObjectRequest]::new()
                $delete.BucketName = $Bucket
                $delete.Key = $target.Key
                # A versioned bucket needs deletion of the exact verified version;
                # a plain DeleteObject would only add a delete marker.
                if (-not [string]::IsNullOrWhiteSpace($verified.VersionId) -and
                    $verified.VersionId -cne 'null') {
                    $delete.VersionId = $verified.VersionId
                }
                $null = $client.DeleteObjectAsync($delete, $cleanupToken).GetAwaiter().GetResult()
                $gone = $false
                for ($attempt = 0; $attempt -lt 3; $attempt++) {
                    try {
                        $null = $transport.VerifyAsync(
                            $target.Key, $target.Size, $target.Hash, $options, $cleanupToken
                        ).GetAwaiter().GetResult()
                        Start-Sleep -Seconds 1
                    } catch {
                        $inner = $_.Exception
                        while ($inner -isnot [ProxyHarbor.Infrastructure.BackupDestinationOperationException] -and
                            $inner.InnerException) { $inner = $inner.InnerException }
                        if ($inner -is [ProxyHarbor.Infrastructure.BackupDestinationOperationException] -and
                            $inner.Failure.Code -eq [ProxyHarbor.Infrastructure.BackupDestinationErrorCode]::NotFound) {
                            $gone = $true
                            break
                        }
                        throw
                    }
                }
                if (-not $gone) { throw 'DeleteObject не подтверждён повторным HEAD.' }
                Write-Host "Deleted and confirmed absent: $Bucket/$($target.Key)."
            } finally { $client.Dispose() }
        } catch {
            $inner = $_.Exception
            while ($inner -isnot [ProxyHarbor.Infrastructure.BackupDestinationOperationException] -and
                $inner.InnerException) { $inner = $inner.InnerException }
            if ($inner -isnot [ProxyHarbor.Infrastructure.BackupDestinationOperationException] -or
                $inner.Failure.Code -ne [ProxyHarbor.Infrastructure.BackupDestinationErrorCode]::NotFound) {
                $cleanupFailures.Add("Не подтверждено удаление $Bucket/$($target.Key); проверьте этот точный ключ вручную.")
            }
        }
    }
    if (Test-Path -LiteralPath $tempDirectory -PathType Container) {
        foreach ($file in [IO.Directory]::GetFiles($tempDirectory)) { [IO.File]::Delete($file) }
        [IO.Directory]::Delete($tempDirectory)
    }
    $options.ObjectStorageSecretKey = $null
    $password = $null
    $signingKey = $null
    $credential = $null
    $cleanupDeadline.Dispose()
    $deadline.Dispose()
}
if ($failure) { throw $failure }
if ($cleanupFailures.Count -gt 0) { throw ($cleanupFailures -join ' ') }
if (-not $wroteObject) { throw 'Canary не получил подтверждённого PUT.' }
