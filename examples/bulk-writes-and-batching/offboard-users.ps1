# Offboard departed users: block sign-in, kill live sessions, strip licenses.
#
# Three writes per user, and the order is not optional - blocking sign-in does
# nothing about a session that is already open, so the revoke has to follow it.
# A batch runs its items concurrently, so ordering means three batched stages
# rather than one batch of three requests per user.
#
# Stage 3 reads back each user's licenses first: assignLicense needs the skuIds
# to remove, and the account is already blocked by then, so nothing can change
# underneath it.
#
# Requirements: Connect-MgGraph -Scopes "User.ReadWrite.All", "Directory.ReadWrite.All"

param(
    [string[]]$UserPrincipalName,
    [string]$Path,                  # or a CSV with a UserPrincipalName column
    [switch]$Execute
)

Import-Module Mgx

$upns = if ($Path) { (Import-Csv $Path).UserPrincipalName } else { $UserPrincipalName }
if (-not $upns) { throw 'Nothing to offboard: pass -UserPrincipalName or -Path.' }

# Resolve first: a typo in the list should surface here, not halfway through
# stage 2 with some accounts already blocked.
# {id} accepts bare strings as well as objects with an id, so UPNs pipe straight in.
$users = @($upns | Invoke-MgxRequest '/users/{id}' -SkipNotFound `
        -Property id,displayName,userPrincipalName,accountEnabled,assignedLicenses)

Write-Host "$($users.Count) of $(@($upns).Count) account(s) resolved"
$users | Select-Object userPrincipalName, displayName, accountEnabled | Format-Table -AutoSize

if (-not $Execute) {
    Write-Host "Preview only - re-run with -Execute to write."
    return
}

# Stage 1: block sign-in.
$blocked = $users | ForEach-Object {
    [PSCustomObject]@{ Url = "/users/$($_.id)"; Method = 'PATCH'; Body = @{ accountEnabled = $false } }
} | Invoke-MgxBatchRequest
Write-Host "blocked : $(@($blocked | Where-Object { $_.Status -lt 400 }).Count)"

# Stage 2: revoke refresh tokens, ending sessions that are already open.
$revoked = $users | ForEach-Object {
    [PSCustomObject]@{ Url = "/users/$($_.id)/revokeSignInSessions"; Method = 'POST' }
} | Invoke-MgxBatchRequest
Write-Host "revoked : $(@($revoked | Where-Object { $_.Status -lt 400 }).Count)"

# Stage 3: return the licenses to the pool. Users with none are skipped - an
# empty removeLicenses array is a wasted request, not a no-op.
$licensed = @($users | Where-Object { $_.assignedLicenses.Count -gt 0 })
if ($licensed) {
    $stripped = $licensed | ForEach-Object {
        [PSCustomObject]@{
            Url    = "/users/$($_.id)/assignLicense"
            Method = 'POST'
            Body   = @{ addLicenses = @(); removeLicenses = @($_.assignedLicenses.skuId) }
        }
    } | Invoke-MgxBatchRequest -DeadLetterPath ./offboard-failures.jsonl
    Write-Host "unlicensed: $(@($stripped | Where-Object { $_.Status -lt 400 }).Count) of $($licensed.Count)"
}

# Deliberately not done here: deleting the account. A blocked, unlicensed user
# keeps their OneDrive and mailbox reachable for whoever inherits the work -
# download-user-onedrive.ps1 is the other half of that.

<#
Expected output:

3 of 3 account(s) resolved

userPrincipalName      displayName    accountEnabled
-----------------      -----------    --------------
graceb@contoso.com     Grace Blake              True
henriettam@contoso.com Henrietta Mueller        True
irvins@contoso.com     Irvin Sayers             True

blocked : 3
revoked : 3
unlicensed: 2 of 2
#>
