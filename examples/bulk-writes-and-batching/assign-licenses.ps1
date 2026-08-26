# Assign a license to every enabled user who does not have one yet.
#
# Two things have to happen and both are batched: a user with no usageLocation
# cannot be licensed at all, so those get a PATCH first, then everyone gets an
# assignLicense POST. Invoke-MgxBatchRequest bundles 20 requests per HTTP call,
# so 400 users are 20 calls rather than 400.
#
# Seats are checked against /subscribedSkus before anything is written - Graph
# will happily refuse item 300 of 400 and leave you working out which ones landed.
#
# Requirements: Connect-MgGraph -Scopes "User.ReadWrite.All",
#               "Organization.Read.All", "Directory.ReadWrite.All"

param(
    [string]$SkuPartNumber = 'ENTERPRISEPACK',   # Office 365 E3
    [string]$DefaultUsageLocation = 'US',
    [switch]$Execute                              # omit for a preview
)

Import-Module Mgx

$sku = Invoke-MgxRequest /subscribedSkus |
    Where-Object { $_.skuPartNumber -eq $SkuPartNumber } |
    Select-Object -First 1
if (-not $sku) { throw "No subscription for $SkuPartNumber in this tenant." }

$available = $sku.prepaidUnits.enabled - $sku.consumedUnits
Write-Host ("{0}: {1} of {2} seats used, {3} free" -f `
    $SkuPartNumber, $sku.consumedUnits, $sku.prepaidUnits.enabled, $available)

# assignedLicenses is filterable server-side, but only as an advanced query
# ($count=true plus ConsistencyLevel eventual). Checking it here keeps the
# example to one query shape and costs nothing extra on a tenant this size.
$candidates = @(Invoke-MgxRequest /users -All `
        -Filter 'accountEnabled eq true' `
        -Property id,userPrincipalName,usageLocation,assignedLicenses |
    Where-Object { $_.assignedLicenses.skuId -notcontains $sku.skuId })

Write-Host "$($candidates.Count) user(s) without $SkuPartNumber"
if ($candidates.Count -eq 0) { return }
if ($candidates.Count -gt $available) {
    throw "$($candidates.Count) users need a license but only $available seats are free."
}

$needsLocation = @($candidates | Where-Object { -not $_.usageLocation })
if (-not $Execute) {
    Write-Host "Preview only - re-run with -Execute to write."
    Write-Host "  would set usageLocation on $($needsLocation.Count) user(s)"
    Write-Host "  would license $($candidates.Count) user(s)"
    $candidates | Select-Object userPrincipalName, usageLocation | Format-Table -AutoSize
    return
}

# Stage 1: usageLocation. Batches run their items concurrently, so anything
# order-dependent has to be a separate call - which is why this is not folded
# into the assignLicense batch below.
if ($needsLocation) {
    $patched = $needsLocation | ForEach-Object {
        [PSCustomObject]@{
            Url    = "/users/$($_.id)"
            Method = 'PATCH'
            Body   = @{ usageLocation = $DefaultUsageLocation }
        }
    } | Invoke-MgxBatchRequest
    Write-Host "usageLocation set: $(@($patched | Where-Object { $_.Status -lt 400 }).Count)"
}

# Stage 2: the licenses themselves.
$assigned = $candidates | ForEach-Object {
    [PSCustomObject]@{
        Url    = "/users/$($_.id)/assignLicense"
        Method = 'POST'
        Body   = @{
            addLicenses    = @(@{ skuId = $sku.skuId; disabledPlans = @() })
            removeLicenses = @()
        }
    }
} | Invoke-MgxBatchRequest -DeadLetterPath ./license-failures.jsonl

$ok   = @($assigned | Where-Object { $_.Status -lt 400 }).Count
$fail = @($assigned | Where-Object { $_.Status -ge 400 }).Count
Write-Host "Licensed: $ok  Failed: $fail"
if ($fail) { Write-Host "Failed items are in ./license-failures.jsonl" }

<#
Expected output (preview):

ENTERPRISEPACK: 22 of 25 seats used, 3 free
3 user(s) without ENTERPRISEPACK
Preview only - re-run with -Execute to write.
  would set usageLocation on 1 user(s)
  would license 3 user(s)

userPrincipalName             usageLocation
-----------------             -------------
graceb@contoso.com            US
henriettam@contoso.com        US
irvins@contoso.com

Expected output (-Execute):

ENTERPRISEPACK: 22 of 25 seats used, 3 free
3 user(s) without ENTERPRISEPACK
usageLocation set: 1
Licensed: 3  Failed: 0
#>
