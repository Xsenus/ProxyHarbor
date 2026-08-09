param(
    [string]$ApiBaseUrl = 'http://localhost:8080',
    [Parameter(Mandatory)][string]$AdminKey
)

# Полный сбор обновляет LastItemCount/LastError каждого feed'а тем же кодом, который работает в production.
$headers = @{ 'X-Admin-Key' = $AdminKey }
Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl/api/v1/admin/collect" -Headers $headers | Out-Null
$sources = Invoke-RestMethod -Method Get -Uri "$ApiBaseUrl/api/v1/admin/sources" -Headers $headers
$sources | Sort-Object Priority | Select-Object Name, DefaultProtocol, LastItemCount, LastFetchedAt, LastError | Format-Table -AutoSize

$failed = @($sources | Where-Object { $_.Enabled -and ($_.LastItemCount -le 0 -or $_.LastError) })
if ($failed.Count -gt 0) {
    Write-Error "$($failed.Count) активных источников не прошли аудит. Подробности показаны выше."
    exit 1
}

Write-Host "Аудит пройден: $($sources.Count) источников вернули распознаваемые прокси." -ForegroundColor Green
