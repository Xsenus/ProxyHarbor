param(
    [Parameter(Mandatory)][string]$PartsPattern,
    [Parameter(Mandatory)][string]$OutputFile
)

# Telegram ограничивает размер одного документа. До создания результата строго
# проверяем, что wildcard выбрал один полный и непрерывный набор частей.
$parts = @(Get-ChildItem -Path $PartsPattern -File)
if ($parts.Count -lt 2) { throw 'По указанному шаблону должно быть найдено не менее двух частей.' }
if (Test-Path -LiteralPath $OutputFile) { throw 'Выходной файл уже существует.' }

$descriptors = foreach ($part in $parts) {
    $match = [regex]::Match(
        $part.Name,
        '^(?<base>.+)\.part(?<number>[0-9]{3,10})-of-(?<total>[0-9]{3,10})$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) { throw "Некорректное имя Telegram-части: $($part.Name)." }

    $number = 0
    $total = 0
    if (-not [int]::TryParse($match.Groups['number'].Value, [ref]$number) -or
        -not [int]::TryParse($match.Groups['total'].Value, [ref]$total) -or
        $number -lt 1 -or $total -lt 2 -or $number -gt $total) {
        throw "Некорректная нумерация Telegram-части: $($part.Name)."
    }
    [pscustomobject]@{
        Part = $part
        SetIdentity = Join-Path $part.DirectoryName $match.Groups['base'].Value
        Number = $number
        Total = $total
    }
}

$setIdentities = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$declaredTotals = [Collections.Generic.HashSet[int]]::new()
foreach ($descriptor in $descriptors) {
    [void]$setIdentities.Add($descriptor.SetIdentity)
    [void]$declaredTotals.Add($descriptor.Total)
}
if ($setIdentities.Count -ne 1 -or $declaredTotals.Count -ne 1) {
    throw 'Шаблон выбрал части разных резервных копий или с разным объявленным количеством.'
}
$declaredTotal = [Linq.Enumerable]::Single($declaredTotals)
if ($parts.Count -ne $declaredTotal) {
    throw "Неполный набор Telegram-частей: найдено $($parts.Count), ожидалось $declaredTotal."
}
$ordered = @($descriptors | Sort-Object Number)
for ($index = 0; $index -lt $ordered.Count; $index++) {
    if ($ordered[$index].Number -ne $index + 1) {
        throw "Нарушена последовательность Telegram-частей: ожидалась часть $($index + 1)."
    }
}
$regularPartLength = $ordered[0].Part.Length
if ($regularPartLength -le 0 -or
    @($ordered[0..($ordered.Count - 2)] | Where-Object { $_.Part.Length -ne $regularPartLength }).Count -gt 0 -or
    $ordered[-1].Part.Length -le 0 -or $ordered[-1].Part.Length -gt $regularPartLength) {
    throw 'Размеры Telegram-частей не соответствуют одному корректному split-набору.'
}

$output = $null
try {
    # CreateNew закрывает race между предварительной проверкой и созданием результата.
    $output = [IO.File]::Open($OutputFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    foreach ($descriptor in $ordered) {
        $input = [IO.File]::OpenRead($descriptor.Part.FullName)
        try { $input.CopyTo($output) } finally { $input.Dispose() }
    }
} catch {
    if ($null -ne $output) { $output.Dispose(); $output = $null }
    Remove-Item -LiteralPath $OutputFile -ErrorAction SilentlyContinue
    throw
} finally {
    if ($null -ne $output) { $output.Dispose() }
}
