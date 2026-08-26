# Benchmark 15: does raising -Concurrency actually put more requests on the wire?
#
# Benchmark 10 recorded 8,000 reads at -Concurrency 128 and achieved ~180 req/s, with the pacer's
# per-attempt latency baseline sitting at 632ms. The same tenant at -Concurrency 20 recorded a
# 94ms baseline. If 128 requests were genuinely in flight at 94ms each the run would have moved
# ~1,360 req/s, so something between the fan-out semaphore and the socket is not absorbing them -
# and until that is known, raising Invoke-MgxRequest's documented 1-128 range would buy nothing.
#
# The recorded latency cannot settle it on its own. RecordHttpTime wraps HttpClient.SendAsync
# (ResilientGraphClient.cs:245-248), and SendAsync includes waiting for a free connection out of
# the pool, so queueing time is INSIDE the number. Little's law over it is circular: it returns
# the offered concurrency by construction whether or not the transport is the constraint.
#
# So the question is settled by control rather than inference. Two raw HttpClients, identical to
# the module's transport in every respect except MaxConnectionsPerServer - 20 (what
# TransportDefaults ships) against 512 - offered the same concurrency over the same requests. If
# the pool is the ceiling, 512 pulls away and the gap IS the cost of the default. If they match,
# 20 connections were never the constraint and the service is simply slower under load, which
# makes the current range correct as it stands.
#
# The module ladder above it answers the user-facing half: what a caller gets for each step up
# in -Concurrency. Rate limiting and pacing are both off - either one would be measured instead
# of the transport.
#
# Reads only, ~1 RU each, a few thousand units total: this stays far inside the burst allowance
# benchmark 13 measured, so nothing here provokes throttling.
param(
    [int[]] $Ladder = @(8, 20, 40, 64, 128),
    [int] $Count = 400,
    [int] $IdPool = 400,
    [int] $RawOffered = 128,
    [int[]] $RawPools = @(20, 512)
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Connect-MgxBenchmark

Write-Host "collecting up to $IdPool ids..."
$ids = Get-BenchUserIds -Count $IdPool
Write-Host "  got $($ids.Count)"
$work = @(0..($Count - 1) | ForEach-Object { $ids[$_ % $ids.Count] })

# --- Part A: what a caller gets per step of -Concurrency ------------------------------------

# Both gates off. The token bucket caps request rate outright and the pacer holds a cold
# workload at 4 rps doubling per clean second; either would be the thing measured.
Set-MgxOption -NoRateLimit
Set-MgxOption -NoAdaptivePacing

# A cold connection pool would charge its handshakes to whichever arm ran first. Warm it, then
# run the ladder up and back down: if the two directions disagree, the ladder is measuring drift
# rather than concurrency, and the report says so instead of averaging the disagreement away.
Write-Host 'warming the connection pool...'
$null = @($work | Select-Object -First 40 | Invoke-MgxRequest '/users/{id}' -Property id -Concurrency 8 `
              -ErrorAction SilentlyContinue)

function Measure-Ladder {
    param([int[]] $Steps, [string] $Direction)
    $out = [ordered]@{}
    foreach ($c in $Steps) {
        $pass = Measure-BenchPass -Name "concurrency $c ($Direction)" -Script {
            $items = @($work | Invoke-MgxRequest '/users/{id}' -Property id -Concurrency $c `
                          -ErrorVariable errs -ErrorAction SilentlyContinue)
            @{ ok = $items.Count; failed = @($errs).Count }
        }
        $seconds = $pass.ElapsedMs / 1000
        $requests = if ($pass.MgxTelemetry) { $pass.MgxTelemetry.Requests } else { $pass.Output.ok }
        $out["$c"] = [pscustomobject]@{
            Concurrency  = $c
            Seconds      = [math]::Round($seconds, 2)
            Ok           = $pass.Output.ok
            Failed       = $pass.Output.failed
            RequestsPerSecond = if ($seconds -gt 0) { [math]::Round($pass.Output.ok / $seconds, 1) } else { 0 }
            # Mean time inside SendAsync, which includes any wait for a free connection.
            MeanHttpMs   = if ($pass.MgxTelemetry -and $requests -gt 0) {
                               [math]::Round($pass.MgxTelemetry.HttpMs / $requests, 1)
                           } else { $null }
        }
        Write-Host ("  concurrency {0,4}: {1,7:F2}s  {2,7:F1} req/s  mean http {3,7} ms" -f `
            $c, $out["$c"].Seconds, $out["$c"].RequestsPerSecond, $out["$c"].MeanHttpMs)
    }
    $out
}

Write-Host ''
Write-Host "=== Part A: Invoke-MgxRequest -Concurrency ladder ($Count reads per step) ==="
$up   = Measure-Ladder -Steps $Ladder -Direction 'up'
Write-Host '  --- and back down (ordering check) ---'
$down = Measure-Ladder -Steps ($Ladder[($Ladder.Count - 1)..0]) -Direction 'down'

# --- Part B: the transport control ----------------------------------------------------------

function Invoke-RawFanOut {
    <#
        The module's transport with one variable changed. Everything here mirrors
        MgxCmdletBase's handler and TransportDefaults - HTTP/2 preferred with fallback allowed,
        the same connect timeout, lifetime and decompression - so MaxConnectionsPerServer is the
        only difference between the two arms, and the difference between their numbers has
        exactly one cause.
    #>
    param(
        [Parameter(Mandatory)] [string] $Token,
        [Parameter(Mandatory)] [string[]] $Work,
        [Parameter(Mandatory)] [int] $Offered,
        [Parameter(Mandatory)] [int] $PoolSize,
        [int] $DeadlineSeconds = 180
    )
    $handler = [System.Net.Http.SocketsHttpHandler]::new()
    $handler.MaxConnectionsPerServer        = $PoolSize
    $handler.EnableMultipleHttp2Connections = $true
    $handler.ConnectTimeout                 = [TimeSpan]::FromSeconds(10)
    $handler.PooledConnectionLifetime       = [TimeSpan]::FromMinutes(2)
    $handler.AutomaticDecompression         = [System.Net.DecompressionMethods]::GZip -bor
                                              [System.Net.DecompressionMethods]::Deflate -bor
                                              [System.Net.DecompressionMethods]::Brotli
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout              = [TimeSpan]::FromSeconds(120)
    $client.DefaultRequestVersion= [System.Net.HttpVersion]::Version20
    $client.DefaultVersionPolicy = [System.Net.Http.HttpVersionPolicy]::RequestVersionOrLower
    $client.DefaultRequestHeaders.Authorization =
        [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token)

    # Warm the pool at this size before timing: connection setup belongs to neither arm.
    try { ($client.GetAsync("https://graph.microsoft.com/v1.0/users/$($Work[0])?`$select=id")).GetAwaiter().GetResult().Dispose() } catch { }

    $pending = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    $sent = 0; $ok = 0; $failed = 0; $version = $null

    # Harvest is inline rather than a scriptblock. Benchmark 13 can use one because its counters
    # live at script scope; in here they are function locals, and a scriptblock assigning to
    # $script:ok would tick a different variable and report zero completions forever.
    # A deadline, because the loop's exit condition is "everything landed". HttpClient.Timeout
    # would eventually fault a hung request, but an arm that spends two minutes per straggler is
    # not a measurement either - it is a hang with a stopwatch attached.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (($sent -lt $Work.Count -or $pending.Count -gt 0) -and $sw.Elapsed.TotalSeconds -lt $DeadlineSeconds) {
        while ($pending.Count -lt $Offered -and $sent -lt $Work.Count) {
            $pending.Add($client.GetAsync("https://graph.microsoft.com/v1.0/users/$($Work[$sent])?`$select=id"))
            $sent++
        }

        $still = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
        foreach ($t in $pending) {
            if (-not $t.IsCompleted) { $still.Add($t); continue }
            if ($t.Status -ne 'RanToCompletion') { $failed++; continue }
            $r = $t.Result
            if ([int]$r.StatusCode -ge 200 -and [int]$r.StatusCode -lt 300) {
                $ok++
                if ($null -eq $version) { $version = $r.Version.ToString() }
            } else { $failed++ }
            $r.Dispose()
        }
        $pending = $still

        if ($pending.Count -ge $Offered -or $sent -ge $Work.Count) { Start-Sleep -Milliseconds 5 }
    }
    $sw.Stop()
    $abandoned = $pending.Count + ($Work.Count - $sent)
    $client.Dispose()

    if ($abandoned -gt 0) {
        Write-Host ("  WARNING: pool $PoolSize hit the ${DeadlineSeconds}s deadline with $abandoned request(s) unaccounted for;") -ForegroundColor Yellow
        Write-Host ('  its rate is a floor, not a measurement.') -ForegroundColor Yellow
    }

    [pscustomobject]@{
        PoolSize          = $PoolSize
        Offered           = $Offered
        Seconds           = [math]::Round($sw.Elapsed.TotalSeconds, 2)
        Ok                = $ok
        Failed            = $failed
        Abandoned         = $abandoned
        RequestsPerSecond = if ($sw.Elapsed.TotalSeconds -gt 0) { [math]::Round($ok / $sw.Elapsed.TotalSeconds, 1) } else { 0 }
        NegotiatedVersion = $version
    }
}

Write-Host ''
Write-Host "=== Part B: same transport, pool size varied (offered concurrency $RawOffered) ==="
$token = Get-BenchAppToken
$raw = [ordered]@{}
foreach ($pool in $RawPools) {
    $r = Invoke-RawFanOut -Token $token -Work $work -Offered $RawOffered -PoolSize $pool
    $raw["$pool"] = $r
    Write-Host ("  pool {0,4}: {1,7:F2}s  {2,7:F1} req/s  ok {3}  failed {4}  negotiated HTTP/{5}" -f `
        $r.PoolSize, $r.Seconds, $r.RequestsPerSecond, $r.Ok, $r.Failed, $r.NegotiatedVersion)
}

# --- Verdict ---------------------------------------------------------------------------------

Write-Host ''
$default = $raw["$($RawPools[0])"]
$wide    = $raw["$($RawPools[-1])"]
$ratio   = if ($default.RequestsPerSecond -gt 0) { $wide.RequestsPerSecond / $default.RequestsPerSecond } else { 0 }

if ($default.Abandoned -gt 0 -or $wide.Abandoned -gt 0) {
    Write-Host 'INCONCLUSIVE: an arm did not finish inside its deadline, so the two rates are not comparable.' -ForegroundColor Yellow
    Write-Host '  Re-run with a smaller -Count, or a longer deadline, before reading anything into the ratio.' -ForegroundColor Yellow
}

if ($default.NegotiatedVersion -and $default.NegotiatedVersion -notlike '2*') {
    Write-Host ("HTTP/{0} was negotiated, not HTTP/2. TransportDefaults sizes the pool at 20 on the" -f $default.NegotiatedVersion) -ForegroundColor Yellow
    Write-Host '  assumption of multiplexing, so without it 20 connections is 20 requests in flight.' -ForegroundColor Yellow
}
if ($ratio -ge 1.5) {
    Write-Host ("POOL-BOUND: {0} connections served {1:F1} req/s, {2} served {3:F1} - {4:F1}x." -f `
        $default.PoolSize, $default.RequestsPerSecond, $wide.PoolSize, $wide.RequestsPerSecond, $ratio)
    Write-Host '  MaxConnectionsPerServer is the fan-out ceiling, not -Concurrency. Its comment sizes it'
    Write-Host '  for "fan-out concurrency of 5" - the default, not the 128 the parameter allows.'
}
else {
    Write-Host ("NOT POOL-BOUND: {0} connections and {1} are within {2:F1}x of each other." -f `
        $default.PoolSize, $wide.PoolSize, [math]::Max($ratio, 1 / [math]::Max($ratio, 0.001)))
    Write-Host '  The service, not the connection pool, is what limits this fan-out. Raising either'
    Write-Host '  MaxConnectionsPerServer or the -Concurrency range would buy nothing.'
}

$first = $up["$($Ladder[0])"]
$last  = $up["$($Ladder[-1])"]
$ladderGain = if ($first.RequestsPerSecond -gt 0) { $last.RequestsPerSecond / $first.RequestsPerSecond } else { 0 }
Write-Host ("Ladder: {0:F1}x concurrency ({1} -> {2}) returned {3:F1}x throughput." -f `
    ($Ladder[-1] / $Ladder[0]), $Ladder[0], $Ladder[-1], $ladderGain)

Write-BenchResult -Benchmark '15-fanout-concurrency-scaling' -Result ([pscustomobject]@{
    LadderUp    = $up
    LadderDown  = $down
    RawByPool   = $raw
    PoolRatio   = [math]::Round($ratio, 2)
    PoolBound   = ($ratio -ge 1.5)
    NegotiatedVersion = $default.NegotiatedVersion
    Count       = $Count
})
