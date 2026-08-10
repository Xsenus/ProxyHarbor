$ErrorActionPreference = 'Stop'

$workflowPath = './.github/workflows/codeql.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw

$requiredPatterns = @(
    '(?m)^\s+security-events:\s+write\s*$',
    '(?m)^\s+contents:\s+read\s*$',
    '(?m)^\s+actions:\s+read\s*$',
    '(?m)^\s+- language:\s+csharp\s*$',
    '(?m)^\s+build-mode:\s+manual\s*$',
    '(?m)^\s+- language:\s+javascript-typescript\s*$',
    '(?m)^\s+build-mode:\s+none\s*$',
    'github/codeql-action/init@[0-9a-f]{40}',
    'github/codeql-action/analyze@[0-9a-f]{40}',
    '(?m)^\s+queries:\s+security-extended\s*$',
    'dotnet restore ProxyHarbor\.slnx --locked-mode',
    'dotnet build ProxyHarbor\.slnx --configuration Release --no-restore'
)

foreach ($pattern in $requiredPatterns) {
    if ($workflow -notmatch $pattern) {
        throw "CodeQL workflow потерял обязательный contract: $pattern"
    }
}

$codeQlPins = [regex]::Matches($workflow, 'github/codeql-action/(?:init|analyze)@(?<sha>[0-9a-f]{40})')
if ($codeQlPins.Count -ne 2 -or @($codeQlPins | ForEach-Object { $_.Groups['sha'].Value } | Sort-Object -Unique).Count -ne 1) {
    throw 'CodeQL init/analyze должны использовать один и тот же полный commit SHA.'
}

Write-Host 'CodeQL contracts пройдены: C#/JS, security-extended, locked manual build, least privilege и единый action SHA.' -ForegroundColor Green
