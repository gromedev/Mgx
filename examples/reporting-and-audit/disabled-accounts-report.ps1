# Report all disabled user accounts in the tenant.
#
# Uses server-side filtering so only matching records are transferred.
# -ConsistencyLevel eventual enables advanced query support required
# for certain filter expressions on large tenants.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

$disabled = Invoke-MgxRequest /users `
    -All `
    -Filter "accountEnabled eq false" `
    -Property displayName,mail,userPrincipalName,createdDateTime `
    -ConsistencyLevel eventual

Write-Host "Disabled accounts: $($disabled.Count)"
$disabled | Sort-Object createdDateTime | Select-Object displayName, mail, createdDateTime | Format-Table -AutoSize

<#
Expected output:

Disabled accounts: 6

displayName   mail                        createdDateTime
-----------   ----                        ---------------
Brian Johnson brianj@contoso.com          5/21/2025 8:22:32 PM
Isaiah Langer isaiahl@contoso.com         6/24/2025 8:18:13 PM
Bianca Pisani bianca.pisani@fabrikam.com  7/3/2025 8:13:54 PM
Pradeep Gupta pradeepg@contoso.com        8/13/2025 12:23:01 AM
Cameron White cameronw@contoso.com        11/11/2025 5:13:25 PM
Raul Razo     raul.razo@woodgrovebank.com 12/30/2025 11:43:39 AM
#>
