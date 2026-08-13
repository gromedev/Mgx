# Benchmark 03: composite "real admin workload" - N users enriched with their group
# memberships (the "who has access to what" afternoon script).
# Contenders:
#   mgx  - Invoke-MgxRequest | Expand-MgxRelation (concurrent enrichment, median of runs)
#   sdk  - Get-MgUser + Get-MgUserMemberOf per user (single run, watchdogged child)
#   rest - raw REST per-user loop, 120s per-call timeout (single run, watchdogged child)
# Baselines run in watchdogged children: bare SDK cmdlets have hung on dead sockets.
param(
    [int] $UserCount = 1000,
    [int] $MgxRuns = 3,
    # child-mode plumbing (internal)
    [ValidateSet('', 'sdk', 'rest')] [string] $Contender = '',
    [string] $IdsFile,
    [string] $ResultFile,
    [string] $HeartbeatFile
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Connect-MgxBenchmark

$props = 'id', 'displayName', 'department'
$filter = "startsWith(userPrincipalName,'bench.u')"

# ---------------- child mode ----------------
if ($Contender) {
    $ids = @(Get-Content $IdsFile)
    function Beat { Set-Content -Path $HeartbeatFile -Value ([datetime]::UtcNow.Ticks) }
    Beat

    $pass = switch ($Contender) {
        'sdk' {
            Measure-BenchPass -Name 'SDK per-user loop (single run)' -Script {
                $edges = 0; $i = 0
                foreach ($id in $ids) {
                    $i++
                    $groups = Get-MgUserMemberOf -UserId $id -All -ErrorAction SilentlyContinue
                    $edges += @($groups).Count
                    if ($i % 100 -eq 0) { Beat; Write-Host ("    sdk progress: {0}/{1}" -f $i, $ids.Count) }
                }
                @{ users = $ids.Count; membershipEdges = $edges }
            }
        }
        'rest' {
            Measure-BenchPass -Name 'raw REST per-user loop (single run)' -Script {
                $tok = Get-BenchAppToken
                $headers = @{ Authorization = "Bearer $tok" }
                $edges = 0; $i = 0
                foreach ($id in $ids) {
                    $i++
                    try {
                        $m = Invoke-RestMethod -Uri ('https://graph.microsoft.com/v1.0/users/' + $id + '/memberOf?$select=id') `
                            -Headers $headers -TimeoutSec 120
                        $edges += @($m.value).Count
                    } catch { }
                    if ($i % 100 -eq 0) { Beat; Write-Host ("    rest progress: {0}/{1}" -f $i, $ids.Count) }
                }
                @{ users = $ids.Count; membershipEdges = $edges }
            }
        }
    }
    $pass | ConvertTo-Json -Depth 8 | Set-Content $ResultFile
    exit 0
}

# ---------------- parent mode ----------------
Write-Host "collecting $UserCount bench user ids..."
$ids = [System.Collections.Generic.List[string]]::new()
Invoke-MgxRequest /users -Filter $filter -Top $UserCount -Property id |
    ForEach-Object { $ids.Add($_.id) }
$tmp = [System.IO.Path]::GetTempPath()
$idsFile = Join-Path $tmp 'bench03-ids.txt'
$ids | Set-Content $idsFile

$results = [ordered]@{}

$results.mgx = Measure-BenchMedian -Name 'Mgx + Expand-MgxRelation' -Runs $MgxRuns -Script {
    $report = Invoke-MgxRequest /users -Filter $filter -Top $UserCount -Property $props |
        Expand-MgxRelation '/users/{id}/memberOf' -As Groups
    $rows = @($report).Count
    $edges = ($report | ForEach-Object { @($_.Groups).Count } | Measure-Object -Sum).Sum
    @{ users = $rows; membershipEdges = $edges }
}

foreach ($c in 'sdk', 'rest') {
    $results[$c] = Invoke-WatchdoggedContender -Name $c -ScriptPath $PSCommandPath `
        -ArgumentList @('-Contender', $c, '-IdsFile', $idsFile, '-ResultFile', (Join-Path $tmp "bench03-$c.json"), '-HeartbeatFile', (Join-Path $tmp "bench03-$c.beat")) `
        -ResultFile (Join-Path $tmp "bench03-$c.json") -HeartbeatFile (Join-Path $tmp "bench03-$c.beat") `
        -StallSeconds 300
}

Write-Host ''
Write-Host ("=== USER REPORT ({0} users + memberships) ===" -f $ids.Count)
$rows = @(
    @($results.mgx.Name, $results.mgx.Median.ElapsedMs, $results.mgx.Median.Output, $false),
    @($results.sdk.Name,  $results.sdk.ElapsedMs,  $results.sdk.Output,  $results.sdk.Hung),
    @($results.rest.Name, $results.rest.ElapsedMs, $results.rest.Output, $results.rest.Hung)
)
foreach ($row in $rows) {
    $status = if ($row[3]) { 'HUNG (killed by watchdog)' } else { "users=$($row[2].users) edges=$($row[2].membershipEdges)" }
    Write-Host ("{0,-36} {1,8:F1}s  {2}" -f $row[0], ($row[1] / 1000), $status)
}
Write-BenchResult -Benchmark '03-user-report' -Result $results
