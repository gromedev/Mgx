# Where the licenses went: seats per SKU, and who is holding them.
#
# /subscribedSkus gives what the tenant bought and how much is consumed; the
# user sweep gives the names behind that number. Both are cheap - assignedLicenses
# comes back with the user object, so no per-user call is needed.
#
# Requirements: Connect-MgGraph -Scopes "Organization.Read.All", "User.Read.All"

Import-Module Mgx

$skus = Invoke-MgxRequest /subscribedSkus

Write-Host "`n=== Seats ===" -ForegroundColor Cyan
$skus |
    ForEach-Object {
        [pscustomobject]@{
            Sku       = $_.skuPartNumber
            Purchased = $_.prepaidUnits.enabled
            Assigned  = $_.consumedUnits
            Free      = $_.prepaidUnits.enabled - $_.consumedUnits
            Warning   = $_.prepaidUnits.warning     # seats in the 30-day grace period
        }
    } |
    Sort-Object Free |
    Format-Table -AutoSize

# skuId is what a user object carries; skuPartNumber is what a human reads.
$nameById = @{}
foreach ($sku in $skus) { $nameById[[string]$sku.skuId] = $sku.skuPartNumber }

$users = Invoke-MgxRequest /users -All -Property id,displayName,userPrincipalName,assignedLicenses,accountEnabled

Write-Host "=== Assignments ===" -ForegroundColor Cyan
$assignments = foreach ($user in $users) {
    foreach ($license in $user.assignedLicenses) {
        [pscustomobject]@{
            Sku     = $nameById[[string]$license.skuId] ?? $license.skuId
            User    = $user.userPrincipalName
            Enabled = $user.accountEnabled
        }
    }
}

$assignments | Group-Object Sku | Sort-Object Count -Descending |
    Select-Object @{ n = 'Sku'; e = { $_.Name } }, Count | Format-Table -AutoSize

# The number worth acting on: seats held by accounts that cannot sign in.
$wasted = @($assignments | Where-Object { -not $_.Enabled })
if ($wasted) {
    Write-Host "$($wasted.Count) seat(s) assigned to disabled accounts:" -ForegroundColor Yellow
    $wasted | Sort-Object Sku, User | Format-Table Sku, User -AutoSize
}

<#
Expected output:

=== Seats ===

Sku            Purchased Assigned Free Warning
---            --------- -------- ---- -------
ENTERPRISEPACK        25       22    3       0
EMSPREMIUM            10        4    6       0
POWER_BI_STANDARD    100        7   93       0

=== Assignments ===

Sku               Count
---               -----
ENTERPRISEPACK       22
POWER_BI_STANDARD     7
EMSPREMIUM            4

3 seat(s) assigned to disabled accounts:

Sku            User
---            ----
EMSPREMIUM     brianj@contoso.com
ENTERPRISEPACK brianj@contoso.com
ENTERPRISEPACK isaiahl@contoso.com
#>
