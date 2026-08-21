# View session telemetry after running Graph operations.
#
# Get-MgxTelemetry shows accumulated request counts, retry events,
# throttle waits, and timing across all Mgx cmdlet calls in the session.
# Useful for understanding what your script actually did.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

# Run some operations
$users = Invoke-MgxRequest /users -All -Property id,displayName
Write-Host "Users: $($users.Count)"

$groups = Invoke-MgxRequest /groups -All -Property id,displayName
Write-Host "Groups: $($groups.Count)"

# See what happened
Get-MgxTelemetry

<#
Expected output:

Users: 28
Groups: 6

Requests               : 2
Succeeded              : 2
Failed                 : 0
Throttle Retries (429) : 0
Other Retries (5xx)    : 0
Circuit Breaker Trips  : 0
Rate Limiter Wait (ms) : 0
Retry Delay (ms)       : 0
HTTP Time (ms)         : 486
Total Elapsed (ms)     : 704
Resource Units         : 2
Batch Item Throttles   : 0
Pacing Wait (ms)       : 201
Pacing Activations     : 1
Last Throttle %        : never seen
Pacing State           : directory: slow-start 4 rps, latency 237ms (1.0x of 241ms baseline)
Content Bytes          : 0
#>
