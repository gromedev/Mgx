# Query multiple Graph endpoints in a single HTTP round-trip.
#
# Invoke-MgxBatchRequest accepts mixed URLs, methods, and bodies
# in one batch. This avoids serial requests when you need data
# from unrelated endpoints (users, groups, apps, skus, etc.).
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All", "Group.Read.All", "Application.Read.All"

Import-Module Mgx

# Mixed GET: pull a snapshot from four different endpoints at once
$results = @(
    "/users?`$top=5&`$select=displayName,userPrincipalName"
    "/groups?`$top=5&`$select=displayName,groupTypes"
    "/applications?`$top=5&`$select=displayName,appId"
    "/subscribedSkus"
) | Invoke-MgxBatchRequest

Write-Host "Batch returned $($results.Count) responses"
# A batch result is a Hashtable of Url / Method / Status / Body - the entity itself lives under
# Body. Reading $_.displayName straight off the result finds nothing, and a Hashtable returns
# $null for a missing key rather than erroring, so the mistake prints a fallback string forever
# instead of failing loudly.
$results | ForEach-Object {
    $count = if ($_.Body -and $_.Body.value) { @($_.Body.value).Count } else { 0 }
    Write-Host ("  {0,-3} {1,-46} {2} item(s)" -f $_.Status, $_.Url, $count)
}

# Mixed methods: read some things from different endpoints, all in one call
$requests = @(
    [PSCustomObject]@{ Url = "/me";              Method = "GET" }
    [PSCustomObject]@{ Url = "/organization";    Method = "GET" }
    [PSCustomObject]@{ Url = "/users?`$top=1";   Method = "GET" }
)
$mixed = $requests | Invoke-MgxBatchRequest

Write-Host "`nMixed-method batch:"
$mixed | ForEach-Object {
    # Single-entity responses put the object directly in Body; collections nest it under
    # Body.value. Checking for value distinguishes the two without guessing per endpoint.
    $summary = if ($_.Body -and $_.Body.value) { "$(@($_.Body.value).Count) item(s)" }
               elseif ($_.Body -and $_.Body.displayName) { $_.Body.displayName }
               elseif ($_.Body -and $_.Body.id) { $_.Body.id }
               else { '(no body)' }
    Write-Host ("  {0,-6} {1,-3} {2,-24} {3}" -f $_.Method, $_.Status, $_.Url, $summary)
}

<#
Expected output:

Batch returned 4 responses
  200 /users?$top=5&$select=displayName,userPrincipalName 5 item(s)
  200 /groups?$top=5&$select=displayName,groupTypes  5 item(s)
  200 /applications?$top=5&$select=displayName,appId 5 item(s)
  200 /subscribedSkus                                3 item(s)

Mixed-method batch:
  GET    200 /me                      MOD Administrator
  GET    200 /organization            1 item(s)
  GET    200 /users?$top=1            1 item(s)
#>
