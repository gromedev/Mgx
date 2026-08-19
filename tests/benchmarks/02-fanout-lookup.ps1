# Benchmark 02: look up N users individually by id.
# Contenders:
#   mgx      - ids | Invoke-MgxRequest '/users/{id}' (bounded fan-out, default -Concurrency 5)
#   sdk      - foreach Get-MgUser -UserId (what most scripts do)
#   diy      - ForEach-Object -Parallel { Get-MgUser } -ThrottleLimit 5 (the DIY "fix")
#   rest     - sequential Invoke-RestMethod, 120s timeout per call
# Mgx runs $MgxRuns times (median); baselines run once each in WATCHDOGGED child
# processes: bare SDK cmdlets have no default timeout and have been observed hanging
# forever on dead sockets - if that happens, the watchdog kills the child and the
# hang is recorded as the contender's result.
param(
    [int] $SampleSize = 5000,
    [int] $MgxRuns = 3,
    # child-mode plumbing (internal)
    [ValidateSet('', 'sdk', 'diy', 'rest')] [string] $Contender = '',
    [string] $IdsFile,
    [string] $ResultFile,
    [string] $HeartbeatFile
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Connect-MgxBenchmark

# ---------------- child mode: run one baseline contender ----------------
if ($Contender) {
    $ids = @(Get-Content $IdsFile)
    function Beat { Set-Content -Path $HeartbeatFile -Value ([datetime]::UtcNow.Ticks) }
    Beat

    $pass = switch ($Contender) {
        'sdk' {
            Measure-BenchPass -Name 'SDK sequential (single run)' -Script {
                $n = 0; $i = 0
                foreach ($id in $ids) {
                    $i++
                    $u = Get-MgUser -UserId $id -Property id,displayName,department -ErrorAction SilentlyContinue
                    if ($u) { $n++ }
                    if ($i % 250 -eq 0) { Beat; Write-Host ("    sdk progress: {0}/{1}" -f $i, $ids.Count) }
                }
                @{ count = $n }
            }
        }
        'diy' {
            Measure-BenchPass -Name 'DIY ForEach -Parallel (single run)' -Script {
                $done = $ids | ForEach-Object -Parallel {
                    (Get-MgUser -UserId $_ -Property id,displayName,department -ErrorAction SilentlyContinue).Id
                } -ThrottleLimit 5
                @{ count = @($done | Where-Object { $_ }).Count }
            }
        }
        'rest' {
            Measure-BenchPass -Name 'raw REST sequential (single run)' -Script {
                $tok = Get-BenchAppToken
                $headers = @{ Authorization = "Bearer $tok" }
                $n = 0; $i = 0
                foreach ($id in $ids) {
                    $i++
                    try {
                        $null = Invoke-RestMethod -Uri ('https://graph.microsoft.com/v1.0/users/' + $id + '?$select=id,displayName,department') `
                            -Headers $headers -TimeoutSec 120
                        $n++
                    } catch { }
                    if ($i % 250 -eq 0) { Beat; Write-Host ("    rest progress: {0}/{1}" -f $i, $ids.Count) }
                    # app-only tokens live ~60-75 min; a long sequential run must re-mint
                    if ($i % 500 -eq 0) { $tok = Get-BenchAppToken; $headers.Authorization = "Bearer $tok" }
                }
                @{ count = $n }
            }
        }
    }
    $pass | ConvertTo-Json -Depth 8 | Set-Content $ResultFile
    exit 0
}

# ---------------- parent mode ----------------
Write-Host "collecting $SampleSize user ids..."
# Read-only benchmark: seeded users preferred, ordinary users acceptable. See Get-BenchUserIds.
$ids = Get-BenchUserIds -Count $SampleSize
Write-Host "got $($ids.Count) ids"

$tmp = [System.IO.Path]::GetTempPath()
$idsFile = Join-Path $tmp 'bench02-ids.txt'
$ids | Set-Content $idsFile

$results = [ordered]@{}

$results.mgx = Measure-BenchMedian -Name 'Mgx fan-out' -Runs $MgxRuns -Script {
    $n = 0
    $ids | Invoke-MgxRequest '/users/{id}' -Property id,displayName,department | ForEach-Object { $n++ }
    @{ count = $n }
}

foreach ($c in 'sdk', 'diy', 'rest') {
    $stall = if ($c -eq 'diy') { 1800 } else { 300 }  # diy emits no heartbeats: absolute budget
    $results[$c] = Invoke-WatchdoggedContender -Name $c -ScriptPath $PSCommandPath `
        -ArgumentList @('-Contender', $c, '-IdsFile', $idsFile, '-ResultFile', (Join-Path $tmp "bench02-$c.json"), '-HeartbeatFile', (Join-Path $tmp "bench02-$c.beat")) `
        -ResultFile (Join-Path $tmp "bench02-$c.json") -HeartbeatFile (Join-Path $tmp "bench02-$c.beat") `
        -StallSeconds $stall
}

Write-Host ''
Write-Host ("=== FAN-OUT LOOKUP ({0} ids) ===" -f $ids.Count)
$rows = @(
    @($results.mgx.Name, $results.mgx.Median.ElapsedMs, $results.mgx.Median.Output.count, $false),
    @($results.sdk.Name,  $results.sdk.ElapsedMs,  $results.sdk.Output.count,  $results.sdk.Hung),
    @($results.diy.Name,  $results.diy.ElapsedMs,  $results.diy.Output.count,  $results.diy.Hung),
    @($results.rest.Name, $results.rest.ElapsedMs, $results.rest.Output.count, $results.rest.Hung)
)
foreach ($row in $rows) {
    $status = if ($row[3]) { 'HUNG (killed by watchdog)' } else { "resolved=$($row[2])" }
    Write-Host ("{0,-36} {1,8:F1}s  {2}" -f $row[0], ($row[1] / 1000), $status)
}
if ($results.mgx.Median.MgxTelemetry) {
    $t = $results.mgx.Median.MgxTelemetry
    Write-Host ("Mgx receipt: {0} requests, HTTP {1:F1}s, token-wait {2:F1}s, retry-delay {3:F1}s" -f `
        $t.Requests, ($t.HttpMs / 1000), ($t.RateLimiterWaitMs / 1000), ($t.RetryDelayMs / 1000))
}
Write-BenchResult -Benchmark '02-fanout-lookup' -Result $results
