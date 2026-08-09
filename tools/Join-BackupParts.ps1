param(
    [Parameter(Mandatory)][string]$PartsPattern,
    [Parameter(Mandatory)][string]$OutputFile
)

# Telegram ограничивает размер одного документа. Сервис нумерует части так, что
# обычная сортировка по имени восстанавливает исходный поток без метаданных.
$parts = @(Get-ChildItem -Path $PartsPattern -File | Sort-Object Name)
if ($parts.Count -lt 2) { throw 'По указанному шаблону должно быть найдено не менее двух частей.' }
if (Test-Path -LiteralPath $OutputFile) { throw 'Выходной файл уже существует.' }

$output = [IO.File]::Create($OutputFile)
try {
    foreach ($part in $parts) {
        $input = [IO.File]::OpenRead($part.FullName)
        try { $input.CopyTo($output) } finally { $input.Dispose() }
    }
} catch {
    $output.Dispose()
    Remove-Item -LiteralPath $OutputFile -ErrorAction SilentlyContinue
    throw
} finally {
    $output.Dispose()
}
