$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Initialize-SourceEndpointSample.ps1')
$count = 0
function Assert-Sample([string]$Name, [string]$Url, [string]$Text, [bool]$Expected, [bool]$Complete = $true) {
    $actual = [ProxyHarbor.SourceAudit.SourceEndpointSample]::ContainsEndpoint($Url, [Text.Encoding]::UTF8.GetBytes($Text), $Complete)
    if ($actual -ne $Expected) { throw "Source endpoint sample contract failed: $Name" }
    $script:count++
}
$jsonUrl = 'https://raw.githubusercontent.com/proxio-io/proxy-list/main/all.json'
$record = '{"ip":"8.8.8.8","port":8080,"protocols":["HTTP","CONNECT80"]}'
Assert-Sample 'complete JSON' $jsonUrl ('{"proxies":[' + $record + ']}') $true
Assert-Sample 'bounded JSON complete record' $jsonUrl ('{"proxies":[' + $record + ',{"ip":"1.') $true $false
Assert-Sample 'bounded JSON incomplete first record' $jsonUrl '{"proxies":[{"ip":"8.8.8.8","port":8080,"protocols":["HTTP"' $false $false
Assert-Sample 'complete JSON cannot be truncated' $jsonUrl ('{"proxies":[' + $record + ',{"ip":"1.') $false
Assert-Sample 'JSON only inside diagnostic field' $jsonUrl ('{"message":' + $record + '}') $false
Assert-Sample 'nested metadata is not the list' $jsonUrl ('{"metadata":{"proxies":[' + $record + ']}}') $false
Assert-Sample 'error before list' $jsonUrl ('{"error":true,"proxies":[' + $record + ']}') $false
Assert-Sample 'error after list' $jsonUrl ('{"proxies":[' + $record + '],"error":true}') $false
Assert-Sample 'unsuccessful envelope' $jsonUrl ('{"success":false,"proxies":[' + $record + ']}') $false
Assert-Sample 'zero status' $jsonUrl ('{"status":0,"proxies":[' + $record + ']}') $false
Assert-Sample 'credentials are not stripped' $jsonUrl ('{"proxies":[' + $record.Replace('"ip":', '"password":"sample","ip":') + ']}') $false
Assert-Sample 'JSON invalid address' $jsonUrl ('{"proxies":[' + $record.Replace('8.8.8.8','999.8.8.8') + ']}') $false
Assert-Sample 'JSON invalid port' $jsonUrl ('{"proxies":[' + $record.Replace('8080','65536') + ']}') $false
Assert-Sample 'JSON loopback' $jsonUrl ('{"proxies":[' + $record.Replace('8.8.8.8','127.0.0.1') + ']}') $false
Assert-Sample 'unsupported CONNECT only' $jsonUrl ('{"proxies":[' + $record.Replace('"HTTP",','') + ']}') $false
Assert-Sample 'SOCKS version must be explicit' $jsonUrl ('{"proxies":[' + $record.Replace('"HTTP","CONNECT80"','"SOCKS"') + ']}') $false
Assert-Sample 'SOCKS5 JSON' $jsonUrl ('{"proxies":[' + $record.Replace('"HTTP","CONNECT80"','"SOCKS5"') + ']}') $true
Assert-Sample 'escaped quote and braces' $jsonUrl ('{"note":"\"}","proxies":[' + $record + ']}') $true
Assert-Sample 'malformed JSON' $jsonUrl ('{"proxies":[' + $record + '] broken}') $false
Assert-Sample 'duplicate root list cannot mask last empty list' $jsonUrl ('{"proxies":[' + $record + '],"proxies":[]}') $false
Assert-Sample 'JSON source policy is explicit' 'https://example.org/all.json' ('{"proxies":[' + $record + ']}') $false
$csvUrl = 'https://raw.githubusercontent.com/hproxy-com/free-proxy-list/main/all.csv'
$header = 'ip,port,protocols,anonymity,country,city,latency_ms,uptime_pct,alive,uptime_24h,uptime_7d,reliability'
$row = '8.8.8.8,8080,http|socks5,elite,US,"New York",123,90,true,90,90,90'
Assert-Sample 'CSV' $csvUrl ($header + "`n" + $row) $true
Assert-Sample 'country CSV resource' ($csvUrl + '?country=US') ($header + "`n" + $row) $true
Assert-Sample 'refs heads CSV alias' $csvUrl.Replace('/main/','/refs/heads/main/') ($header + "`n" + $row) $true
Assert-Sample 'quoted newline CSV uses production reader' $csvUrl ($header + "`n" + $row.Replace('New York',"New`nYork")) $true
Assert-Sample 'quoted escaped CSV field' $csvUrl ($header + "`n" + $row.Replace('New York','New ""York""')) $true
Assert-Sample 'CSV first row complete despite bounded suffix' $csvUrl ($header + "`n" + $row + "`n" + '1.1.') $true $false
Assert-Sample 'CSV header only' $csvUrl $header $false
Assert-Sample 'CSV wrong header' $csvUrl ($header.Replace('protocols','scheme') + "`n" + $row) $false
Assert-Sample 'CSV invalid port' $csvUrl ($header + "`n" + $row.Replace('8080','0')) $false
Assert-Sample 'CSV invalid address' $csvUrl ($header + "`n" + $row.Replace('8.8.8.8','999.8.8.8')) $false
Assert-Sample 'CSV malformed quotes' $csvUrl ($header + "`n" + $row.Replace('"New York"','"New York')) $false
Assert-Sample 'CSV duplicate protocol' $csvUrl ($header + "`n" + $row.Replace('http|socks5','http|http')) $false
Assert-Sample 'CSV unsupported protocol' $csvUrl ($header + "`n" + $row.Replace('http|socks5','CONNECT80')) $false
Assert-Sample 'CSV too many fields' $csvUrl ($header + "`n" + $row + ',extra') $false
Assert-Sample 'CSV wrong country' $csvUrl ($header + "`n" + $row.Replace(',US,',',usa,')) $false
Assert-Sample 'CSV source policy is explicit' 'https://example.org/all.csv' ($header + "`n" + $row) $false
$textUrl = 'https://example.org/list.txt'
Assert-Sample 'plain endpoint' $textUrl '8.8.8.8:8080' $true
Assert-Sample 'plain protocol URI' $textUrl 'socks5://8.8.8.8:1080' $true
Assert-Sample 'plain hostname prefix' $textUrl 'example8.8.8.8:8080' $false
Assert-Sample 'plain credential' $textUrl 'user:pass@8.8.8.8:8080' $false
Assert-Sample 'plain extra port suffix' $textUrl '8.8.8.8:8080:extra' $false
Assert-Sample 'plain error page without endpoints' $textUrl '<html>unavailable</html>' $false
Write-Output "Source endpoint sample contracts passed: $count"
