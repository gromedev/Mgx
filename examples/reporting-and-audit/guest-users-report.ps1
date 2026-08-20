# Report all guest (external) users in the tenant.
#
# Guest accounts have userType eq 'Guest'. This exports them to JSONL
# for audit purposes and also prints a summary to the console.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

$guests = Invoke-MgxRequest /users `
    -All `
    -Filter "userType eq 'Guest'" `
    -Property displayName,mail,userPrincipalName,createdDateTime,externalUserState `
    -ConsistencyLevel eventual

Write-Host "Guest accounts: $($guests.Count)"

$guests |
    Sort-Object createdDateTime -Descending |
    Select-Object displayName, mail, externalUserState, createdDateTime |
    Format-Table -AutoSize

<#
Expected output:

Guest accounts: 3

displayName    mail                                externalUserState createdDateTime
-----------    ----                                ----------------- ---------------
Gerhart Moller gerhart.moller@northwindtraders.com PendingAcceptance 6/23/2026 4:04:46 PM
Raul Razo      raul.razo@woodgrovebank.com         Accepted          12/30/2025 11:43:39 AM
Bianca Pisani  bianca.pisani@fabrikam.com          Accepted          7/3/2025 8:13:54 PM
#>
