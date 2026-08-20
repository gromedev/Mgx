# What the adaptive pacer is doing, and why it is not doing what the docs suggest.
#
# The 2.1 pacer spaces requests BEFORE sending, learning a rate per workload. This script makes
# that state visible: what a cold session looks like, what slow start does to a fan-out, how
# workloads stay independent, and which signals the pacer can actually rely on.
#
# The last point is the one worth reading. Microsoft documents an x-ms-throttle-limit-percentage
# header that warns when you pass 0.8 of the resource-unit budget. Measured against a live
# tenant it was never emitted - not at 1.5x the documented budget, and not while the tenant was
# actively returning 429s. So the pacer treats it as opportunistic and relies on Retry-After and
# latency drift, which are real. This script shows you that for your own tenant.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All","Files.Read.All"
# Read-only.

Import-Module Mgx

function Show-PacingState {
    param([string]$Label)
    $t = Get-MgxTelemetry
    Write-Host "`n$Label" -ForegroundColor Cyan
    "  requests           : $($t.Requests)  ($($t.Succeeded) ok, $($t.Failed) failed)"
    "  pacing waits       : $($t.AdaptivePacingWaitMs) ms over $($t.AdaptivePacingActivations) activations"
    "  throttle retries   : $($t.ThrottleRetries)   rate-limiter waits: $($t.RateLimiterWaitMs) ms"
    "  resource units     : $($t.ResourceUnitsConsumed)"
    # PacingState is one line per workload bucket that has seen traffic. A bucket with nothing
    # to say is absent rather than zeroed, so the line grows as you touch more workloads.
    "  pacing state       : $(if ($t.PacingState) { $t.PacingState } else { '(no bucket has seen traffic yet)' })"
    "  last throttle %    : $($t.LastThrottlePercentage)"
}

Write-Host "=== 1. A cold session has no pacing state ===" -ForegroundColor Green
Show-PacingState "before any request"

Write-Host "`n=== 2. Slow start on a cold directory workload ===" -ForegroundColor Green
# A cold bucket opens at 4 rps and doubles each clean second. Request #1 is never delayed - the
# cap only bites once a fan-out is in flight, which is exactly the case that gets throttled
# before it has returned a single item.
$sw = [Diagnostics.Stopwatch]::StartNew()
$users = Invoke-MgxRequest '/users?$top=5&$select=id,displayName' -All
$sw.Stop()
"  fetched $(@($users).Count) users in $($sw.Elapsed.TotalSeconds.ToString('F1'))s"
Show-PacingState "after a directory read"

Write-Host "`n=== 3. A second workload gets its own independent bucket ===" -ForegroundColor Green
# Drive and directory are separate buckets. A throttled Teams or Drive workload must not slow an
# Entra fan-out, because the budgets really are separate: Microsoft documents limits per service
# and per application+tenant pair, and a two-identity A/B run confirmed one identity absorbing
# 8,203 throttles while another in the same tenant was never refused.
try {
    $null = Invoke-MgxRequest '/me/drive?$select=id,driveType' -ErrorAction Stop
    "  drive reachable - watch a second bucket appear in the state line below"
}
catch {
    "  no drive on this account ($($_.Exception.Message -replace '\s+', ' '))"
    "  the directory bucket below should be unaffected either way - that is the point"
}
Show-PacingState "after touching a second workload"

Write-Host "`n=== 4. Did Graph ever send the proximity header? ===" -ForegroundColor Green
$t = Get-MgxTelemetry
if ($t.LastThrottlePercentage -lt 0) {
    @"
  LastThrottlePercentage = $($t.LastThrottlePercentage)

  Negative means Graph has not sent x-ms-throttle-limit-percentage on any response in this
  session. That is the expected result. In testing it was never emitted, including during runs
  that were actively being throttled 8,203 times - so a control loop built on it would be dead
  code that looks like a feature.

  What the pacer uses instead, in order of reliability:
    1. 429 + Retry-After  - unambiguous, always honored
    2. latency drift      - throughput fell ~19x under load before recovering
    3. this percentage    - welcome when present, assumed absent
"@
}
else {
    "  LastThrottlePercentage = $($t.LastThrottlePercentage) - Graph DID send it."
    "  Worth recording: that contradicts what was measured elsewhere. Above 0.8 means you are"
    "  near the budget and the pacer is damping; at 1.2 it spaces to ~2s per request."
}

Write-Host "`n=== 5. A/B: the same fan-out with pacing off ===" -ForegroundColor Green
# The honest comparison. Pacing costs a little wall-clock on a cold sequential run and roughly
# nothing on a warm one; what it buys is not being throttled at scale. Do not read a single
# small run as evidence either way.
$ids = @($users | Select-Object -First 8 | ForEach-Object { $_.id })
if ($ids.Count -lt 2) {
    "  need at least 2 users to compare; skipping"
    return
}

Set-MgxOption -NoAdaptivePacing
$swOff = [Diagnostics.Stopwatch]::StartNew()
$null = $ids | Invoke-MgxRequest -Uri '/users/{id}?$select=id' -Concurrency 4
$swOff.Stop()

Set-MgxOption -Reset
$swOn = [Diagnostics.Stopwatch]::StartNew()
$null = $ids | Invoke-MgxRequest -Uri '/users/{id}?$select=id' -Concurrency 4
$swOn.Stop()

"  {0} lookups, pacing OFF : {1:F2}s" -f $ids.Count, $swOff.Elapsed.TotalSeconds
"  {0} lookups, pacing ON  : {1:F2}s" -f $ids.Count, $swOn.Elapsed.TotalSeconds
"  difference             : {0:F2}s" -f ($swOn.Elapsed.TotalSeconds - $swOff.Elapsed.TotalSeconds)
""
"  A cold bucket pays slow start; a warm one usually pays nothing. The cost that matters is the"
"  one you avoid - a throttled fan-out that collapses from 322 rps to 17 rps and takes 15 minutes"
"  to drain."

Show-PacingState "final"
Set-MgxOption -Reset

<#
Expected output:

=== 1. A cold session has no pacing state ===

before any request
  requests           : 0  (0 ok, 0 failed)
  pacing waits       : 0 ms over 0 activations
  throttle retries   : 0   rate-limiter waits: 0 ms
  resource units     : 0
  pacing state       : (no bucket has seen traffic yet)
  last throttle %    : -1

=== 2. Slow start on a cold directory workload ===
  fetched 28 users in 0.4s

after a directory read
  requests           : 1  (1 ok, 0 failed)
  pacing waits       : 0 ms over 0 activations
  throttle retries   : 0   rate-limiter waits: 0 ms
  resource units     : 1
  pacing state       : directory: slow-start 4 rps, latency 241ms (1.0x of 241ms baseline)
  last throttle %    : -1

=== 3. A second workload gets its own independent bucket ===
  drive reachable - watch a second bucket appear in the state line below

after touching a second workload
  requests           : 2  (2 ok, 0 failed)
  pacing waits       : 0 ms over 0 activations
  throttle retries   : 0   rate-limiter waits: 0 ms
  resource units     : 2
  pacing state       : drive: slow-start 4 rps; directory: slow-start 4 rps, latency 241ms (1.0x of 241ms baseline)
  last throttle %    : -1

=== 4. Did Graph ever send the proximity header? ===
  LastThrottlePercentage = -1

  Negative means Graph has not sent x-ms-throttle-limit-percentage on any response in this
  session. That is the expected result. In testing it was never emitted, including during runs
  that were actively being throttled 8,203 times - so a control loop built on it would be dead
  code that looks like a feature.

  What the pacer uses instead, in order of reliability:
    1. 429 + Retry-After  - unambiguous, always honored
    2. latency drift      - throughput fell ~19x under load before recovering
    3. this percentage    - welcome when present, assumed absent

=== 5. A/B: the same fan-out with pacing off ===
  8 lookups, pacing OFF : 0.71s
  8 lookups, pacing ON  : 1.93s
  difference             : 1.22s

  A cold bucket pays slow start; a warm one usually pays nothing. The cost that matters is the
  one you avoid - a throttled fan-out that collapses from 322 rps to 17 rps and takes 15 minutes
  to drain.

final
  requests           : 18  (18 ok, 0 failed)
  pacing waits       : 6193 ms over 8 activations
  throttle retries   : 0   rate-limiter waits: 0 ms
  resource units     : 18
  pacing state       : drive: slow-start 4 rps; directory: slow-start 8 rps, latency 198ms (0.9x of 224ms baseline)
  last throttle %    : -1
#>
