# Benchmark 01: enumerate every user in the tenant.
# Contenders: Mgx streaming (-All), SDK Get-MgUser -All, raw Invoke-RestMethod paging
# at its best configuration ($top=999 + $select). All three select identical properties
# and stream into a counter - nothing is materialized into an array, so the numbers
# measure enumeration, not collection building.
# Also measures time-to-first-result: elapsed until the first object reaches the pipeline.
param(
    [int] $Runs = 3
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Import-Module Microsoft.Graph.Users
Connect-MgxBenchmark

$props = 'id', 'displayName', 'mail', 'department', 'jobTitle', 'accountEnabled'
$select = $props -join ','
$results = [ordered]@{}

# --- Full enumeration ---
$results.mgx = Measure-BenchMedian -Name 'Mgx list all' -Runs $Runs -Script {
    $n = 0
    Invoke-MgxRequest /users -All -Property $props | ForEach-Object { $n++ }
    @{ count = $n }
}

$results.sdk = Measure-BenchMedian -Name 'SDK Get-MgUser -All' -Runs $Runs -Script {
    $n = 0
    Get-MgUser -All -Property $props -PageSize 999 | ForEach-Object { $n++ }
    @{ count = $n }
}

# The README footnote compares the SDK tuned against the SDK as it comes. Without this arm that
# ratio was a claim with nothing behind it - it said ten times, and it is three.
$results.sdkDefault = Measure-BenchMedian -Name 'SDK Get-MgUser -All (default page)' -Runs $Runs -Script {
    $n = 0
    Get-MgUser -All -Property $props | ForEach-Object { $n++ }
    @{ count = $n }
}

$results.rest = Measure-BenchMedian -Name 'raw Invoke-RestMethod' -Runs $Runs -Script {
    $tok = Get-BenchAppToken
    $headers = @{ Authorization = "Bearer $tok" }
    $n = 0
    $url = 'https://graph.microsoft.com/v1.0/users?$top=999&$select=' + $select
    while ($url) {
        $page = Invoke-RestMethod -Uri $url -Headers $headers
        $n += $page.value.Count
        $url = $page.'@odata.nextLink'
    }
    @{ count = $n }
}

# --- Time to first result ---
$results.ttfrMgx = Measure-BenchMedian -Name 'Mgx time-to-first' -Runs $Runs -Script {
    $first = Invoke-MgxRequest /users -All -Property $props | Select-Object -First 1
    @{ got = [bool]$first }
}
$results.ttfrSdk = Measure-BenchMedian -Name 'SDK time-to-first' -Runs $Runs -Script {
    $first = Get-MgUser -All -Property $props -PageSize 999 | Select-Object -First 1
    @{ got = [bool]$first }
}

Write-Host ''
Write-Host '=== LIST USERS ==='
foreach ($k in 'mgx', 'sdk', 'sdkDefault', 'rest') {
    $m = $results[$k].Median
    Write-Host ("{0,-24} {1,8:F1}s  count={2}  peakWS={3}MB" -f $results[$k].Name, ($m.ElapsedMs / 1000), $m.Output.count, $m.PeakWorkingSetMB)
}
foreach ($k in 'ttfrMgx', 'ttfrSdk') {
    $m = $results[$k].Median
    Write-Host ("{0,-24} {1,8:F2}s" -f $results[$k].Name, ($m.ElapsedMs / 1000))
}
if ($results.mgx.Median.MgxTelemetry) {
    $t = $results.mgx.Median.MgxTelemetry
    Write-Host ("Mgx receipt: {0} requests, HTTP {1:F1}s, token-wait {2:F1}s, retry-delay {3:F1}s" -f `
        $t.Requests, ($t.HttpMs / 1000), ($t.RateLimiterWaitMs / 1000), ($t.RetryDelayMs / 1000))
}
Write-BenchResult -Benchmark '01-list-users' -Result $results
