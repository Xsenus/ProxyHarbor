param(
    [string]$Path = (Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md'),
    [string]$Version,
    [string]$OutputSectionFile
)

$ErrorActionPreference = 'Stop'
$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$content = Get-Content -LiteralPath $resolvedPath -Raw
if (-not $content.StartsWith("# Changelog`n") -and -not $content.StartsWith("# Changelog`r`n")) {
    throw 'CHANGELOG должен начинаться с заголовка # Changelog.'
}

$strictSemVer = '(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?'
$headingPattern = "(?m)^## \[(?<version>Unreleased|$strictSemVer)\](?: - (?<date>\d{4}-\d{2}-\d{2}))?\r?$"
$headings = [regex]::Matches($content, $headingPattern)
$releaseLikeHeadings = [regex]::Matches($content, '(?m)^## \[')
if ($releaseLikeHeadings.Count -ne $headings.Count) {
    throw 'CHANGELOG содержит release-заголовок, не соответствующий строгому формату.'
}
if ($headings.Count -lt 2 -or $headings[0].Groups['version'].Value -ne 'Unreleased') {
    throw 'CHANGELOG должен содержать Unreleased первым и хотя бы один versioned release.'
}

$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($heading in $headings) {
    $headingVersion = $heading.Groups['version'].Value
    if (-not $seen.Add($headingVersion)) { throw "CHANGELOG содержит повторяющийся раздел $headingVersion." }
    $date = $heading.Groups['date'].Value
    if ($headingVersion -eq 'Unreleased') {
        if ($date) { throw 'Раздел Unreleased не должен иметь дату.' }
    } else {
        [DateOnly]$parsedDate = [DateOnly]::MinValue
        $dateValid = $date -and [DateOnly]::TryParseExact(
            $date,
            'yyyy-MM-dd',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None,
            [ref]$parsedDate)
        if (-not $dateValid) { throw "Release $headingVersion должен иметь корректную дату YYYY-MM-DD." }
    }
}

if ($OutputSectionFile -and -not $Version) {
    throw 'OutputSectionFile разрешён только вместе с конкретной Version.'
}

if ($Version) {
    if ($Version -notmatch "^$strictSemVer$") {
        throw "Некорректная SemVer-версия changelog: $Version."
    }
    $targetIndex = -1
    for ($index = 0; $index -lt $headings.Count; $index++) {
        if ($headings[$index].Groups['version'].Value -eq $Version) { $targetIndex = $index; break }
    }
    if ($targetIndex -lt 0) { throw "CHANGELOG не содержит release-раздел [$Version]." }
    $bodyStart = $headings[$targetIndex].Index + $headings[$targetIndex].Length
    $bodyEnd = if ($targetIndex + 1 -lt $headings.Count) { $headings[$targetIndex + 1].Index } else { $content.Length }
    $body = $content.Substring($bodyStart, $bodyEnd - $bodyStart).Trim()
    if (-not $body -or $body -notmatch '(?m)^### (Added|Changed|Deprecated|Removed|Fixed|Security)\s*$') {
        throw "Release-раздел [$Version] не содержит непустую стандартную категорию изменений."
    }
    if ($OutputSectionFile) {
        $parent = Split-Path -Parent $OutputSectionFile
        if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Set-Content -LiteralPath $OutputSectionFile -Value $body -Encoding utf8NoBOM
    }
}

Write-Host "Changelog contract пройден: $($headings.Count - 1) release-разделов."
