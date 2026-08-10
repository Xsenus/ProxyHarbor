[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path.TrimEnd(
    [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
$excludedDirectories = @('.git', 'bin', 'obj', 'node_modules', 'TestResults', 'ReleaseTestResults')
$markdownFiles = Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.md' |
    Where-Object {
        $relative = [IO.Path]::GetRelativePath($root, $_.FullName)
        -not ($relative -split '[\\/]' | Where-Object { $_ -in $excludedDirectories })
    }
$failures = [Collections.Generic.List[string]]::new()
$linkPattern = [regex]'!?(?<!\!)\[[^\]]*\]\((?<target>[^)]+)\)'

foreach ($file in $markdownFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($match in $linkPattern.Matches($content)) {
        $target = $match.Groups['target'].Value.Trim()
        if ($target.StartsWith('<') -and $target.EndsWith('>')) {
            $target = $target.Substring(1, $target.Length - 2)
        }
        if ($target -match '^(?i:https?|mailto):' -or $target.StartsWith('#')) { continue }
        $target = ($target -split '[#?]', 2)[0]
        if ([string]::IsNullOrWhiteSpace($target)) { continue }
        try { $target = [Uri]::UnescapeDataString($target) }
        catch { $failures.Add("$($file.FullName): некорректный URI '$target'."); continue }

        $baseDirectory = Split-Path -Parent $file.FullName
        $candidate = if ($target.StartsWith('/')) {
            Join-Path $root $target.TrimStart('/')
        } else { Join-Path $baseDirectory $target }
        try { $fullPath = [IO.Path]::GetFullPath($candidate) }
        catch { $failures.Add("$($file.FullName): некорректный путь '$target'."); continue }

        if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals($fullPath, $root, [StringComparison]::OrdinalIgnoreCase)) {
            $failures.Add("$($file.FullName): ссылка выходит за пределы репозитория '$target'.")
        } elseif (-not [IO.File]::Exists($fullPath) -and -not [IO.Directory]::Exists($fullPath)) {
            $relativeFile = [IO.Path]::GetRelativePath($root, $file.FullName)
            $failures.Add("${relativeFile}: отсутствует цель '$target'.")
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "Обнаружены невалидные локальные Markdown-ссылки: $($failures.Count)."
}
Write-Host "Documentation links пройдены: проверено $($markdownFiles.Count) Markdown-файлов." -ForegroundColor Green
