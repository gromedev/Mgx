# Benchmark 11: does the data survive throttling?
#
# The other benchmarks ask how FAST a contender is. This one asks whether the rows it handed
# you are the rows the tenant actually holds. Under throttling an enumeration can come back
# short (a page that never succeeded) or long (a page counted twice), and both are silent -
# the script exits 0 and you get a CSV that looks fine.
#
# Ground truth is the server-side /users/$count, which is independent of pagination, of the
# SDK, and of Mgx. Each contender enumerates the same collection at the same page size and is
# scored on:
#   Emitted    objects handed to the pipeline
#   Distinct   unique ids among them
#   Missing    ground truth - Distinct   (rows you will never know you lost)
#   Duplicate  Emitted - Distinct        (rows you will count twice)
#
# Contenders run CONCURRENTLY, in separate processes, against one app registration - because
# the Graph budget is scoped per application+tenant, that is what makes them contend for the
# same tokens instead of each getting a quiet tenant to itself. -PressureConcurrency adds
# background load on top, which is the ordinary condition for a tenant running anything else.
param(
    [int] $PageSize = 100,
    # In-flight pressure requests. 400 measured 886 req/s against a 100k tenant and crossed the
    # 800 RU/s ceiling in 21s, so this is the dial that decides whether the run throttles at all.
    [int] $PressureConcurrency = 400,
    [int] $TimeoutMinutes = 30,
    [int] $CooldownSeconds = 90,
    [switch] $NoPressure
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Connect-MgxBenchmark

if ($NoPressure) { $PressureConcurrency = 0 }

# --- Ground truth -----------------------------------------------------------------------
# $count is served from the directory's own index, not by walking pages, so it cannot inherit
# a pagination bug from the thing it is being used to check.
$token = Get-BenchAppToken
$truth = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/users/$count' `
    -Headers @{ Authorization = "Bearer $token"; ConsistencyLevel = 'eventual' }
$truth = [int] $truth
Write-Host "ground truth: $truth users (from /users/`$count)"
Write-Host "page size $PageSize -> ~$([math]::Ceiling($truth / $PageSize)) requests per contender; pressure concurrency $PressureConcurrency"

# --- Contenders -------------------------------------------------------------------------
# Each returns the same shape. Every one of them pages the same collection at the same size,
# selecting only id, so the only variable is how the contender behaves when Graph says no.

$restBody = {
    param($PageSize, $Retry)
    . "$using:PSScriptRoot/common.ps1"
    $seen = [System.Collections.Generic.HashSet[string]]::new()
    $emitted = 0; $throttles = 0; $requests = 0; $failure = $null
    $token = Get-BenchAppToken
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $url = 'https://graph.microsoft.com/v1.0/users?$top=' + $PageSize + '&$select=id'
    while ($url) {
        # Token lifetime is not the variable under test - a 401 an hour in would look like data
        # loss without being any. Both REST contenders re-mint on the same schedule.
        if ($requests -gt 0 -and $requests % 400 -eq 0) { $token = Get-BenchAppToken }
        $page = $null
        for ($attempt = 1; $attempt -le $Retry; $attempt++) {
            try {
                $requests++
                $page = Invoke-RestMethod -Uri $url -Headers @{ Authorization = "Bearer $token" }
                break
            }
            catch {
                $code = $_.Exception.Response.StatusCode.value__
                if ($code -eq 429) { $throttles++ }
                if ($attempt -eq $Retry) { $failure = "HTTP $code after $attempt attempt(s)"; break }
                # Deliberately a flat sleep: the naive retry loop people actually write does not
                # read Retry-After, which is the whole reason it keeps arriving early.
                Start-Sleep -Seconds 5
            }
        }
        if (-not $page) { break }
        foreach ($u in $page.value) { $emitted++; [void]$seen.Add($u.id) }
        $url = $page.'@odata.nextLink'
    }
    $sw.Stop()
    @{ Emitted = $emitted; Distinct = $seen.Count; Throttles = $throttles
       Requests = $requests; WallMs = $sw.ElapsedMilliseconds; Failure = $failure }
}

$sdkBody = {
    param($PageSize)
    . "$using:PSScriptRoot/common.ps1"
    Import-MgxLocal
    Connect-MgxBenchmark
    $seen = [System.Collections.Generic.HashSet[string]]::new()
    $emitted = 0; $failure = $null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        Get-MgUser -All -PageSize $PageSize -Property id -ErrorAction Stop |
            ForEach-Object { $emitted++; [void]$seen.Add($_.Id) }
    }
    catch { $failure = $_.Exception.Message }
    $sw.Stop()
    @{ Emitted = $emitted; Distinct = $seen.Count; Throttles = $null
       Requests = $null; WallMs = $sw.ElapsedMilliseconds; Failure = $failure }
}

$mgxBody = {
    param($PageSize)
    . "$using:PSScriptRoot/common.ps1"
    Import-MgxLocal
    Connect-MgxBenchmark
    Get-MgxTelemetry -Reset | Out-Null
    $seen = [System.Collections.Generic.HashSet[string]]::new()
    $emitted = 0; $failure = $null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        Invoke-MgxRequest /users -All -PageSize $PageSize -Property id -ErrorAction Stop |
            ForEach-Object { $emitted++; [void]$seen.Add($_.id) }
    }
    catch { $failure = $_.Exception.Message }
    $sw.Stop()
    $t = Get-MgxTelemetry
    @{ Emitted = $emitted; Distinct = $seen.Count; Throttles = $t.ThrottleRetries
       Requests = $t.Requests; WallMs = $sw.ElapsedMilliseconds; Failure = $failure }
}

# Background load. Not a contender and not scored - it exists so the contenders are measured
# on a tenant that is busy, which is the only condition under which any of this matters. Graph
# scopes the budget per application+tenant pair, so this and the contenders draw on one bucket.
# Sequential paging cannot generate this: a contender walking nextLinks manages ~6 req/s, three
# orders short of the ceiling. Only high in-flight concurrency gets there.
$pressureBody = {
    param($Minutes, $Concurrency)
    . "$using:PSScriptRoot/common.ps1"
    $token = Get-BenchAppToken
    $handler = [System.Net.Http.SocketsHttpHandler]::new()
    $handler.MaxConnectionsPerServer = $Concurrency
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(120)
    $client.DefaultRequestHeaders.Authorization =
        [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
    $url = 'https://graph.microsoft.com/v1.0/users?$top=1&$select=id'
    $deadline = [datetime]::UtcNow.AddMinutes($Minutes)
    $sent = 0; $minted = [datetime]::UtcNow
    while ([datetime]::UtcNow -lt $deadline) {
        if (([datetime]::UtcNow - $minted).TotalMinutes -gt 40) {
            $token = Get-BenchAppToken
            $client.DefaultRequestHeaders.Authorization =
                [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
            $minted = [datetime]::UtcNow
        }
        $tasks = foreach ($i in 1..$Concurrency) { $client.GetAsync($url) }
        try { [System.Threading.Tasks.Task]::WaitAll($tasks) } catch { }
        foreach ($t in $tasks) { if ($t.Status -eq 'RanToCompletion') { $t.Result.Dispose() } }
        $sent += $Concurrency
    }
    $client.Dispose()
    $sent
}

# --- Run ----------------------------------------------------------------------------------
$jobs = [ordered]@{}
if ($PressureConcurrency -gt 0) {
    # A previous run may have left the bucket drained, which would throttle the contenders
    # before the pressure job has even started and make the two indistinguishable.
    Write-Host "cooldown ${CooldownSeconds}s (let the resource-unit budget refill)..."
    Start-Sleep -Seconds $CooldownSeconds
    $null = Start-Job -Name 'pressure' -ScriptBlock $pressureBody `
        -ArgumentList $TimeoutMinutes, $PressureConcurrency
    Write-Host "draining the budget at concurrency $PressureConcurrency..."
    Start-Sleep -Seconds 30
}

$jobs['rest-naive'] = Start-Job -Name 'rest-naive' -ScriptBlock $restBody -ArgumentList $PageSize, 1
$jobs['rest-retry'] = Start-Job -Name 'rest-retry' -ScriptBlock $restBody -ArgumentList $PageSize, 3
$jobs['sdk']        = Start-Job -Name 'sdk'        -ScriptBlock $sdkBody  -ArgumentList $PageSize
$jobs['mgx']        = Start-Job -Name 'mgx'        -ScriptBlock $mgxBody  -ArgumentList $PageSize

Write-Host "all four running concurrently against one app registration..."
$null = Wait-Job -Job $jobs.Values -Timeout ($TimeoutMinutes * 60)

$results = [ordered]@{}
foreach ($name in $jobs.Keys) {
    $job = $jobs[$name]
    if ($job.State -eq 'Running') {
        Stop-Job $job
        $results[$name] = @{ Emitted = 0; Distinct = 0; WallMs = $TimeoutMinutes * 60000
                             Failure = "did not finish inside $TimeoutMinutes min" }
    }
    else {
        $out = Receive-Job $job -ErrorAction SilentlyContinue | Where-Object { $_ -is [hashtable] } | Select-Object -Last 1
        $results[$name] = if ($out) { $out } else { @{ Emitted = 0; Distinct = 0; WallMs = 0; Failure = 'job produced no result' } }
    }
    Remove-Job $job -Force
}
Get-Job | Where-Object Name -like 'pressure*' | ForEach-Object { Stop-Job $_ -ErrorAction SilentlyContinue; Remove-Job $_ -Force }

# --- Report -------------------------------------------------------------------------------
$labels = [ordered]@{
    'rest-naive' = 'Invoke-RestMethod'
    'rest-retry' = 'Invoke-RestMethod + retry'
    'sdk'        = 'Get-MgUser -All'
    'mgx'        = 'Invoke-MgxRequest -All'
}
Write-Host ''
Write-Host "=== THROTTLE ACCURACY (ground truth $truth users, page size $PageSize, $PressureConcurrency in flight) ==="
Write-Host ('{0,-28} {1,9} {2,9} {3,8} {4,10} {5,8} {6,6}' -f 'Contender', 'Emitted', 'Distinct', 'Missing', 'Duplicate', 'Wall', '429s')
foreach ($name in $labels.Keys) {
    $r = $results[$name]
    $r.Missing = $truth - $r.Distinct
    $r.Duplicate = $r.Emitted - $r.Distinct
    Write-Host ('{0,-28} {1,9} {2,9} {3,8} {4,10} {5,7:F1}s {6,6}' -f `
        $labels[$name], $r.Emitted, $r.Distinct, $r.Missing, $r.Duplicate,
        ($r.WallMs / 1000), ($r.Throttles ?? '-'))
    if ($r.Failure) { Write-Host ("  ^ $($r.Failure)") }
}

$results.groundTruth = $truth
$results.pageSize = $PageSize
$results.pressureConcurrency = $PressureConcurrency
Write-BenchResult -Benchmark '11-throttle-accuracy' -Result $results
