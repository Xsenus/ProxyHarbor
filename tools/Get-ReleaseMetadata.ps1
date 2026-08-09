param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$RepositoryOwner,
    [Parameter(Mandatory)][string]$OutputFile
)

$ErrorActionPreference = 'Stop'

# Строгий SemVer 2.0 запрещает неоднозначные leading zero и произвольные tag'и,
# которые иначе могли бы стать именем пакета, assembly version или Docker tag.
$semVerPattern = '^v(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)$'
$match = [regex]::Match($Tag, $semVerPattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $match.Success) {
    throw "Release tag '$Tag' должен быть строгим SemVer вида v1.2.3 или v1.2.3-rc.1."
}

$owner = $RepositoryOwner.ToLowerInvariant()
if ($owner -notmatch '^[a-z0-9](?:[a-z0-9-]{0,37}[a-z0-9])?$') {
    throw 'GitHub repository owner нельзя безопасно преобразовать в GHCR namespace.'
}
if (-not [IO.Path]::IsPathFullyQualified($OutputFile)) {
    throw 'OutputFile должен быть абсолютным путём GitHub Actions output-файла.'
}

$version = $match.Groups['version'].Value
$buildSeparator = $version.IndexOf('+', [StringComparison]::Ordinal)
$precedenceVersion = if ($buildSeparator -ge 0) { $version.Substring(0, $buildSeparator) } else { $version }
$isPrerelease = $precedenceVersion.Contains('-', [StringComparison]::Ordinal)
# `_build_` содержит запрещённый SemVer-символ `_`, поэтому такое отображение
# build metadata в Docker tag обратимо и не сталкивается с prerelease `-build`.
$imageTag = $version.Replace('+', '_build_')
if ($imageTag.Length -gt 128) {
    throw 'Нормализованный Docker image tag превышает OCI-лимит 128 символов.'
}
$outputs = [ordered]@{
    version = $version
    'image-tag' = $imageTag
    'image-prefix' = "ghcr.io/$owner"
    'is-prerelease' = $isPrerelease.ToString().ToLowerInvariant()
}
foreach ($entry in $outputs.GetEnumerator()) {
    Add-Content -LiteralPath $OutputFile -Value "$($entry.Key)=$($entry.Value)" -Encoding utf8NoBOM
}

Write-Host "Release metadata: version=$version, image-tag=$imageTag, image-prefix=$($outputs['image-prefix']), prerelease=$isPrerelease"
