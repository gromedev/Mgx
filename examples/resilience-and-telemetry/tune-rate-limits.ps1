# Tune resilience options for a specific workload.
#
# Set-MgxOption lets you adjust the rate limiter, retry count, circuit
# breaker, and timeouts at runtime without restarting the session.
# Only the parameters you pass are changed; everything else stays.
#
# Use Get-MgxOption to inspect current settings.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

# View current defaults
Get-MgxOption

# Slow down for a tenant that throttles aggressively
Set-MgxOption -BatchItemsPerSecond 5 -MaxRetryAttempts 10

# Run your workload
$users = Invoke-MgxRequest /users -All -Property id,displayName
Write-Host "Users: $($users.Count)"

# Restore all defaults when done
Set-MgxOption -Reset

Get-MgxOption

<#
Expected output:

Rate Limit Burst        : 200
Rate Limit/sec          : 50
No Rate Limit           : False
No Adaptive Pacing      : False
Queue Limit             : 500
Max Retry Attempts      : 7
Max Retry-After (s)     : 120
Total Timeout (s)       : 300
Attempt Timeout (s)     : 30
CB Duration (s)         : 15
CB Failure Ratio        : 0.1
CB Min Throughput       : 40
CB Sampling (s)         : 30
Batch Chunk Concurrency : 1
Batch Items/sec         : 20

Users: 28
Rate Limit Burst        : 200
Rate Limit/sec          : 50
No Rate Limit           : False
No Adaptive Pacing      : False
Queue Limit             : 500
Max Retry Attempts      : 7
Max Retry-After (s)     : 120
Total Timeout (s)       : 300
Attempt Timeout (s)     : 30
CB Duration (s)         : 15
CB Failure Ratio        : 0.1
CB Min Throughput       : 40
CB Sampling (s)         : 30
Batch Chunk Concurrency : 1
Batch Items/sec         : 20
#>
