# A one-call snapshot of the tenant: users, groups, apps, and licenses.
#
# Four unrelated endpoints in one HTTP round trip instead of four. Useful as the
# opening move of a script that needs a bit of everything, and as the thing to
# run when someone asks how big the tenant is.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All", "Group.Read.All", "Application.Read.All"

Import-Module Mgx

# $count=true returns the total in @odata.count without paging anything, and
# -ConsistencyLevel rides along on each subrequest, which Graph requires for it.
$counts = @(
    "/users?`$top=1&`$count=true"
    "/groups?`$top=1&`$count=true"
    "/applications?`$top=1&`$count=true"
    "/servicePrincipals?`$top=1&`$count=true"
) | Invoke-MgxBatchRequest -ConsistencyLevel eventual

Write-Host "`n=== Tenant size ===" -ForegroundColor Cyan
$counts | ForEach-Object {
    "  {0,-20} {1,6}" -f ($_.Url -replace '\?.*$', ''), $_.Body.'@odata.count'
}

$orgUri = "/organization?`$select=displayName,verifiedDomains"
$samples = @($orgUri, '/subscribedSkus') | Invoke-MgxBatchRequest

# A batch result is Url / Method / Status / Body - the entity itself lives under
# Body, and a collection nests it one deeper under Body.value. Results come back
# keyed by the URL that produced them rather than in the order they were sent.
$byUrl = @{}
foreach ($r in $samples) { $byUrl[$r.Url] = $r.Body }

$tenant = $byUrl[$orgUri].value[0]
$skus   = $byUrl['/subscribedSkus'].value

Write-Host "`n=== $($tenant.displayName) ===" -ForegroundColor Cyan
"  domains : $(($tenant.verifiedDomains.name) -join ', ')"
"  licenses: $(($skus | ForEach-Object { '{0} {1}/{2}' -f $_.skuPartNumber, $_.consumedUnits, $_.prepaidUnits.enabled }) -join '  ')"

<#
Expected output:

=== Tenant size ===
  /users                   28
  /groups                  12
  /applications             9
  /servicePrincipals       47

=== Contoso Ltd ===
  domains : contoso.onmicrosoft.com, contoso.com
  licenses: ENTERPRISEPACK 22/25  EMSPREMIUM 4/10  POWER_BI_STANDARD 7/100
#>
