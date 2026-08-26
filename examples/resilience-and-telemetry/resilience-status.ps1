# Add retry, circuit breaking, and rate limiting to existing Microsoft.Graph
# scripts without changing a line of their code - then check it and take it off.
#
# Enable-MgxResilience injects a Polly pipeline into the SDK's HTTP transport, so
# unmodified SDK cmdlets (Get-MgUser, Get-MgGroup, ...) retry on 429/5xx and honor
# Retry-After. Get-MgxResilience reports whether it is currently injected;
# Disable-MgxResilience removes it and restores the original SDK behavior.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Microsoft.Graph.Users
Import-Module Mgx

# Check current state - not injected yet
Get-MgxResilience

# Inject
Enable-MgxResilience
Get-MgxResilience   # IsEnabled: True, IsActive: True

# SDK cmdlets now have retry + circuit breaker
$users = Get-MgUser -Top 5 -Property displayName
Write-Host "Got $($users.Count) users via SDK (with resilience)"

# Remove injection - restore original SDK behavior
Disable-MgxResilience
Get-MgxResilience   # IsEnabled: False, IsActive: False

<#
Expected output:

IsEnabled : False
IsActive  : False

IsEnabled : True
IsActive  : True

Got 5 users via SDK (with resilience)
IsEnabled : False
IsActive  : False
#>
