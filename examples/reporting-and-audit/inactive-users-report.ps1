# Users who have not signed in for 90 days.
#
# accountEnabled only tells you the account is allowed to sign in, not that
# anyone does. signInActivity is the property that answers the question, and it
# is filterable server-side, so the tenant is never enumerated in full.
#
# Two caveats worth knowing before the result is trusted:
#   * signInActivity needs a Microsoft Entra ID P1 license and the
#     AuditLog.Read.All scope, on top of User.Read.All.
#   * An account that has NEVER signed in has no signInActivity at all, so it
#     does not match this filter. Those are the second query below.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All", "AuditLog.Read.All"

Import-Module Mgx

$cutoff = (Get-Date).AddDays(-90).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

$inactive = Invoke-MgxRequest /users -All `
    -Filter "signInActivity/lastSignInDateTime le $cutoff" `
    -Property id,displayName,userPrincipalName,accountEnabled,signInActivity

Write-Host "Not seen since $($cutoff.Substring(0,10)): $($inactive.Count)"
$inactive |
    Select-Object displayName, userPrincipalName, accountEnabled,
        @{ n = 'LastSignIn'; e = { ([datetime]$_.signInActivity.lastSignInDateTime).ToString('yyyy-MM-dd') } } |
    Sort-Object LastSignIn |
    Format-Table -AutoSize

# Never signed in at all - created and forgotten, which is the more interesting
# half of the report on most tenants. These have no signInActivity to filter on,
# so this one sweeps and checks locally: one enumeration, same behavior on every
# tenant, and no dependency on how Graph handles a null comparison.
$never = @(Invoke-MgxRequest /users -All `
        -Property id,displayName,userPrincipalName,createdDateTime,accountEnabled,signInActivity |
    Where-Object { -not $_.signInActivity.lastSignInDateTime })

Write-Host "`nNever signed in: $($never.Count)"
$never |
    Select-Object displayName, userPrincipalName, accountEnabled,
        @{ n = 'Created'; e = { ([datetime]$_.createdDateTime).ToString('yyyy-MM-dd') } } |
    Sort-Object Created |
    Format-Table -AutoSize

# license-report.ps1 answers the follow-up: which of these are holding a seat.
# The same shape reports stale devices: /devices, filtered on
# approximateLastSignInDateTime le $cutoff.

<#
Expected output:

Not seen since 2026-05-26: 4

displayName   userPrincipalName          accountEnabled LastSignIn
-----------   -----------------          -------------- ----------
Brian Johnson brianj@contoso.com                  False 2025-11-02
Isaiah Langer isaiahl@contoso.com                 False 2026-01-14
Pradeep Gupta pradeepg@contoso.com                 True 2026-02-28
Raul Razo     raul.razo@woodgrovebank.com          True 2026-03-19

Never signed in: 2

displayName   userPrincipalName    accountEnabled Created
-----------   -----------------    -------------- -------
Sample Vendor vendor@contoso.com             True 2025-08-13
Bianca Pisani bianca.pisani@fabrikam.com     True 2025-07-03
#>
