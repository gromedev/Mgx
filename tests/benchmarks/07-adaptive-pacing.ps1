# Benchmark 07: adaptive write pacing under REAL Graph throttling.
# The workload - creating groups with 20 members bound at birth - is heavy enough
# (~21 directory writes per item) to provoke genuine 429 waves at scale.
# Two rounds, each in a FRESH pwsh process (Mgx's adapted rate is static process
# state, so rounds must not share a process):
#   paced   - Mgx defaults: 20 items/sec + AIMD (429 halves rate, clean chunks recover)
#   unpaced - pacing off, chunk concurrency 10: full-speed slam, retries only
# A cooldown separates rounds so the second inherits a calm throttle budget.
# Reports wall time, item 429s, retry-delay time; captures the -Verbose stream and
# extracts the rate trajectory + adaptive events to results/07-rate-trajectory-<mode>.txt.
param(
    [ValidateSet('both', 'paced', 'unpaced')] [string] $Mode = 'both',
    [int] $GroupCount = 1000,
    [int] $CooldownSeconds = 300
)

. "$PSScriptRoot/common.ps1"

if ($Mode -eq 'both') {
    foreach ($m in 'paced', 'unpaced') {
        Write-Host "=== round: $m ==="
        $p = Start-Process pwsh -PassThru -Wait -NoNewWindow -ArgumentList `
            '-NoProfile', '-File', $PSCommandPath, '-Mode', $m, '-GroupCount', $GroupCount
        if ($p.ExitCode -ne 0) { throw "round '$m' failed (exit $($p.ExitCode))" }
        if ($m -eq 'paced') {
            Write-Host "cooldown ${CooldownSeconds}s (let the tenant's throttle budget recover)..."
            Start-Sleep -Seconds $CooldownSeconds
        }
    }
    Write-Host 'both rounds complete - see results/07-adaptive-pacing.json'
    exit 0
}

Import-MgxLocal
Connect-MgxBenchmark

if ($Mode -eq 'unpaced') {
    Set-MgxOption -BatchItemsPerSecond 0 -BatchChunkConcurrency 10
}

$prefix = "bench07$($Mode.Substring(0,1))"
$verboseLog = Join-Path $PSScriptRoot "results/07-rate-trajectory-$Mode.txt"

# Need 20 real member ids to bind
$memberIds = [System.Collections.Generic.List[string]]::new()
Invoke-MgxRequest /users -Filter "startsWith(userPrincipalName,'bench.u')" -Top 200 -Property id |
    ForEach-Object { $memberIds.Add($_.id) }
if ($memberIds.Count -lt 20) { throw 'seed the tenant first (need bench.u users to bind)' }

$result = Measure-BenchPass -Name "group-create slam ($Mode)" -Script {
    $items = foreach ($i in 1..$GroupCount) {
        $refs = foreach ($k in 0..19) { $memberIds[($i * 7 + $k * 13) % $memberIds.Count] }
        [pscustomobject]@{
            Url    = '/groups'
            Method = 'POST'
            Body   = @{
                displayName          = "Bench07 $Mode $i"
                mailNickname         = ('{0}.{1:D5}' -f $prefix, $i)
                mailEnabled          = $false
                securityEnabled      = $true
                'members@odata.bind' = @($refs | Sort-Object -Unique | ForEach-Object { "https://graph.microsoft.com/v1.0/users/$_" })
            }
        }
    }
    $r = @($items | Invoke-MgxBatchRequest -Verbose -ErrorAction SilentlyContinue -WarningAction SilentlyContinue 4> $verboseLog)
    @{ ok = @($r | Where-Object Status -lt 400).Count; failed = @($r | Where-Object Status -ge 400).Count }
}

# --- Cleanup: delete this round's groups ---
Write-Host 'cleanup: deleting round groups...'
$gids = [System.Collections.Generic.List[string]]::new()
Invoke-MgxRequest /groups -All -Filter "startsWith(mailNickname,'$prefix')" -Property id |
    ForEach-Object { $gids.Add($_.id) }
if ($gids.Count -gt 0) {
    $null = @($gids | ForEach-Object { "/groups/$_" } |
        Invoke-MgxBatchRequest -Method DELETE -ErrorAction SilentlyContinue -WarningAction SilentlyContinue)
}

# --- Extract trajectory highlights from the verbose stream ---
$adaptiveEvents = @()
if (Test-Path $verboseLog) {
    $adaptiveEvents = @(Get-Content $verboseLog | Where-Object { $_ -match 'Adaptive pacing|rate=' })
}

$t = $result.MgxTelemetry
Write-Host ''
Write-Host ("=== ADAPTIVE PACING round '{0}' (N={1}) ===" -f $Mode, $GroupCount)
Write-Host ("wall {0,7:F1}s  created {1}  failed {2}" -f ($result.ElapsedMs / 1000), $result.Output.ok, $result.Output.failed)
if ($t) {
    Write-Host ("item 429s: {0}   retry-delay: {1:F1}s   rate-limiter wait: {2:F1}s" -f `
        $t.BatchItemThrottles, ($t.RetryDelayMs / 1000), ($t.RateLimiterWaitMs / 1000))
}
if ($adaptiveEvents.Count -gt 0) {
    Write-Host 'trajectory highlights:'
    $adaptiveEvents | Select-Object -First 12 | ForEach-Object { Write-Host "  $_" }
}
$result | Add-Member -NotePropertyName Mode -NotePropertyValue $Mode
$result | Add-Member -NotePropertyName AdaptiveEvents -NotePropertyValue $adaptiveEvents
Write-BenchResult -Benchmark '07-adaptive-pacing' -Result $result
