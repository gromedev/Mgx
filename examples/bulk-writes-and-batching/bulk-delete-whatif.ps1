# Bulk delete users with -WhatIf to preview before committing.
#
# All Mgx write operations support -WhatIf and -Confirm.
# Run without -WhatIf to execute; add -Confirm for interactive approval
# per item. Useful for destructive operations on production tenants.
#
# This example deletes all disabled guest accounts older than 180 days.
#
# Requirements: Connect-MgGraph -Scopes "User.ReadWrite.All"

Import-Module Mgx

$cutoff = (Get-Date).AddDays(-180).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [System.Globalization.CultureInfo]::InvariantCulture)

$targets = Invoke-MgxRequest /users `
    -All `
    -Filter "userType eq 'Guest' and accountEnabled eq false and createdDateTime le $cutoff" `
    -Property id,displayName,mail,createdDateTime `
    -ConsistencyLevel eventual

Write-Host "Targets: $($targets.Count)"

if ($targets.Count -eq 0) {
    Write-Host "No matching guests found - nothing to delete."
    return
}

$targets | Select-Object displayName, mail, createdDateTime | Format-Table -AutoSize

# Preview - remove -WhatIf to execute
$targets | Invoke-MgxRequest '/users/{id}' -Method DELETE -WhatIf

<#
Expected output:

Targets: 2

displayName   mail                        createdDateTime
-----------   ----                        ---------------
Bianca Pisani bianca.pisani@fabrikam.com  7/3/2025 8:13:54 PM
Raul Razo     raul.razo@woodgrovebank.com 12/30/2025 11:43:39 AM

What if: Performing the operation "Bulk write" on target "DELETE 2 items via /users/{id}".
#>
