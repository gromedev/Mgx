# Benchmark 10: does adaptive pacing pay for itself against a service that throttles?
#
# Benchmark 06 answers "what does pacing cost" but structurally cannot answer "what does it
# buy": its mock injects 429s on a fixed schedule keyed on entity id, so the throttle rate is
# independent of send rate and backing off has no upside by construction. Real Graph throttles
# HARDER the faster you push, which is the whole premise of pacing.
#
# This drives the SAME read workload twice against a real tenant - once paced (2.1 default),
# once with -NoAdaptivePacing - hard enough to cross the resource-unit budget and provoke
# genuine 429s. Reads only: throttling is not write-specific, so this needs no write consent.
#
# Each round runs in a FRESH pwsh process because the adapted rate is static process state,
# with a cooldown between so the second round does not inherit a drained budget.
param(
    [ValidateSet('both', 'paced', 'unpaced')] [string] $Mode = 'both',
    [int] $Count = 20000,
    [int] $Concurrency = 60,
    [int] $CooldownSeconds = 300
)

. "$PSScriptRoot/common.ps1"

if ($Mode -eq 'both') {
    foreach ($m in 'paced', 'unpaced') {
        Write-Host "=== round: $m ===" -ForegroundColor Cyan
        $p = Start-Process pwsh -PassThru -Wait -NoNewWindow -ArgumentList `
            '-NoProfile', '-File', $PSCommandPath, '-Mode', $m, '-Count', $Count, '-Concurrency', $Concurrency
        if ($p.ExitCode -ne 0) { throw "round '$m' failed (exit $($p.ExitCode))" }
        if ($m -eq 'paced') {
            Write-Host "cooldown ${CooldownSeconds}s (let the resource-unit budget refill)..."
            Start-Sleep -Seconds $CooldownSeconds
        }
    }
    Write-Host 'both rounds complete - see results/10-pacing-under-real-throttling.json'
    return
}

Import-MgxLocal
Connect-MgxBenchmark

# A fixed, pre-materialised id list. Bounded work only: no loop that can run away.
Write-Host "collecting $Count ids..."
$ids = Get-BenchUserIds -Count $Count
Write-Host "  got $($ids.Count)"

# The token-bucket limiter would cap us well under the RU budget and we would never reach
# the throttling regime this benchmark exists to measure. Lift it; the pacer (or its absence)
# is the variable under test.
Set-MgxOption -NoRateLimit
if ($Mode -eq 'unpaced') {
    Set-MgxOption -NoAdaptivePacing
    Write-Host '  adaptive pacing DISABLED'
} else {
    Write-Host '  adaptive pacing ON (2.1 default)'
}

$result = Measure-BenchPass -Name "reads $Mode" -Script {
    $items = @($ids | Invoke-MgxRequest '/users/{id}' -Property id,displayName `
                  -Concurrency $Concurrency -ErrorVariable errs -ErrorAction SilentlyContinue)
    @{ ok = $items.Count; failed = @($errs).Count }
}

$t = Get-MgxTelemetry
Write-Host ''
Write-Host ("=== $Mode ===")
Write-Host ("  wall              {0,8:F1}s" -f ($result.ElapsedMs / 1000))
Write-Host ("  completed         {0,8}  failed {1}" -f $result.Output.ok, $result.Output.failed)
Write-Host ("  throttle retries  {0,8}" -f $t.ThrottleRetries)
Write-Host ("  retry delay       {0,8:F1}s  (time spent honouring Retry-After)" -f ($t.RetryDelayMs / 1000))
Write-Host ("  pacing wait       {0,8:F1}s  over {1} activations" -f ($t.AdaptivePacingWaitMs / 1000), $t.AdaptivePacingActivations)
Write-Host ("  resource units    {0,8}   ({1:F1} RU/s)" -f $t.ResourceUnitsConsumed, ($t.ResourceUnitsConsumed / ($result.ElapsedMs / 1000)))
Write-Host ("  pacing state      {0}" -f $t.PacingState)

# Write-BenchResult attaches the telemetry snapshot itself, including resource units.
Write-BenchResult -Benchmark '10-pacing-under-real-throttling' -Result ([pscustomobject]@{
    Mode        = $Mode
    Count       = $ids.Count
    Concurrency = $Concurrency
    WallMs      = $result.ElapsedMs
    Ok          = $result.Output.ok
    Failed      = $result.Output.failed
})
