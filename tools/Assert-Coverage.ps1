param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ReportPath,
    [ValidateRange(0.0, 1.0)][double]$MinimumLineRate = 0.55,
    [ValidateRange(0.0, 1.0)][double]$MinimumBranchRate = 0.55
)

$ErrorActionPreference = 'Stop'
[xml]$report = Get-Content -LiteralPath $ReportPath -Raw
$lines = @{}
$branches = @{}

# Cobertura повторяет строки для вложенных compiler-generated классов. Считаем
# уникальную пару source-file/line и берём максимальное число hits/covered branches.
foreach ($class in $report.coverage.packages.package.classes.class) {
    $file = ([string]$class.filename).Replace('\', '/')
    if ("/$file" -like '*/obj/*' -or "/$file" -like '*/Persistence/Migrations/*') { continue }

    foreach ($line in $class.lines.line) {
        $key = "$file`0$($line.number)"
        $hits = [int]$line.hits
        if (-not $lines.ContainsKey($key) -or $hits -gt $lines[$key]) { $lines[$key] = $hits }

        if ([string]$line.branch -ne 'True') { continue }
        if ([string]$line.'condition-coverage' -notmatch '\((\d+)/(\d+)\)') {
            throw "Некорректный Cobertura branch counter: $($line.'condition-coverage')"
        }
        $covered = [int]$Matches[1]
        $total = [int]$Matches[2]
        if (-not $branches.ContainsKey($key)) {
            $branches[$key] = @($covered, $total)
        } else {
            $branches[$key] = @(
                [Math]::Max($branches[$key][0], $covered),
                [Math]::Max($branches[$key][1], $total))
        }
    }
}

if ($lines.Count -eq 0 -or $branches.Count -eq 0) {
    throw 'Coverage report не содержит исполняемых строк или ветвей после исключений.'
}
$coveredLines = @($lines.Values | Where-Object { $_ -gt 0 }).Count
$coveredBranches = ($branches.Values | Measure-Object -Property { $_[0] } -Sum).Sum
$totalBranches = ($branches.Values | Measure-Object -Property { $_[1] } -Sum).Sum
$lineRate = $coveredLines / $lines.Count
$branchRate = $coveredBranches / $totalBranches

Write-Host ("Meaningful coverage: lines {0}/{1} ({2:P2}), branches {3}/{4} ({5:P2})." -f
    $coveredLines, $lines.Count, $lineRate, $coveredBranches, $totalBranches, $branchRate)
if ($lineRate -lt $MinimumLineRate) {
    throw ("Line coverage {0:P2} ниже обязательного порога {1:P2}." -f $lineRate, $MinimumLineRate)
}
if ($branchRate -lt $MinimumBranchRate) {
    throw ("Branch coverage {0:P2} ниже обязательного порога {1:P2}." -f $branchRate, $MinimumBranchRate)
}
