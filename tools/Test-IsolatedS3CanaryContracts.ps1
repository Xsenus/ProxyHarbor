#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$canary = Join-Path $PSScriptRoot 'Invoke-IsolatedS3Canary.ps1'
$bucket = 'proxyharbor-isolated-test'

$dryRun = (& $canary -Bucket $bucket -PromptForCredential 6>&1 | Out-String).Trim()
if ($dryRun -cne "Dry run: https://s3-nl.hostkey.com / nl / $bucket / proxyharbor-drill/; no provider I/O.") {
    throw 'Canary dry-run изменился или начал запрашивать credentials.'
}

try {
    & $canary -Bucket $bucket -ConfirmBucket 'different-test-bucket' -PromptForCredential -Execute
    throw 'Неверное подтверждение bucket не было отклонено.'
} catch {
    if ($_.Exception.Message -notlike '*повторно укажите точное имя bucket*') { throw }
}

try {
    & $canary -Bucket $bucket -ConfirmBucket $bucket -PromptForCredential -CredentialPath 'unused.xml' -Execute
    throw 'Конфликт двух способов передачи credentials не был отклонён.'
} catch {
    if ($_.Exception.Message -notlike '*Выберите только один способ*') { throw }
}

Write-Host 'Isolated S3 canary contracts пройдены: dry-run без provider I/O и fail-closed preflight.'
