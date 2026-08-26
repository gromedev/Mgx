# Resource units: what a query actually costs, and how to size a fan-out before running it.
#
# Graph throttles directory workloads on a resource-unit budget, not on request count or
# bandwidth. Two queries that look alike can differ several times over in cost, and the
# difference is invisible unless you read x-ms-resource-unit. Mgx accumulates it for you:
# Get-MgxTelemetry reports ResourceUnitsConsumed for the session.
#
# Requirements: Connect-MgGraph -Scopes "Group.Read.All","User.Read.All"

Import-Module Mgx

function Measure-QueryCost {
    # Cost of one call, measured rather than assumed. The documented RU table is directionally
    # right but not exact - transitiveMembers is published at 5 RU and bills 4 here.
    param([string]$Label, [string]$Uri)

    $before = (Get-MgxTelemetry).ResourceUnitsConsumed
    try { $null = Invoke-MgxRequest $Uri -ErrorAction Stop }
    catch { Write-Warning "$Label : $($_.Exception.Message)"; return }

    [pscustomobject]@{
        Query         = $Label
        ResourceUnits = (Get-MgxTelemetry).ResourceUnitsConsumed - $before
        Uri           = $Uri
    }
}

$sample = (Invoke-MgxRequest '/groups?$top=1&$select=id').id
if (-not $sample) { throw 'No groups in this tenant to measure against.' }

Write-Host "`n=== What each query shape costs ===" -ForegroundColor Cyan
$costs = @(
    Measure-QueryCost 'group members (bare)'          "/groups/$sample/members?`$top=5"
    Measure-QueryCost 'group members ($select)'       "/groups/$sample/members?`$top=5&`$select=id"
    Measure-QueryCost 'transitiveMembers (bare)'      "/groups/$sample/transitiveMembers?`$top=5"
    Measure-QueryCost 'transitiveMembers ($select)'   "/groups/$sample/transitiveMembers?`$top=5&`$select=id"
    Measure-QueryCost 'single group read'             "/groups/$sample"
    Measure-QueryCost 'users list ($top=25)'          "/users?`$top=25"
    Measure-QueryCost 'users list ($select)'          "/users?`$top=25&`$select=id,displayName"
) | Where-Object { $_ }
$costs | Format-Table Query, ResourceUnits -AutoSize

# --- size the fan-out from the measured cost ---
#
# Published budget: 8,000 RU per 10 seconds per app per tenant, i.e. 800 RU/s. Measured onset of
# throttling in one test tenant was appreciably higher (clean at 882 RU/s, 429s from ~1,200
# RU/s), so treat 800 as a conservative floor rather than a hard ceiling - and never as a target.
$BudgetRuPerSecond = 800
$chosen = $costs | Where-Object { $_.Query -eq 'transitiveMembers ($select)' } | Select-Object -First 1
$bare   = $costs | Where-Object { $_.Query -eq 'transitiveMembers (bare)' }    | Select-Object -First 1
if (-not $chosen -or $chosen.ResourceUnits -le 0) {
    Write-Warning 'No RU header observed - this tenant or endpoint may not be RU-metered. Skipping the projection.'
    return
}

$groupCount = 0
$null = Invoke-MgxRequest '/groups' -Top 1 -CountVariable groupCount
$totalRu = $groupCount * $chosen.ResourceUnits
$seconds = $totalRu / $BudgetRuPerSecond

Write-Host "`n=== Sizing a per-group fan-out over the whole tenant ===" -ForegroundColor Cyan
"  groups in tenant   : {0:N0}" -f $groupCount
"  cost per group     : {0} RU  ({1})" -f $chosen.ResourceUnits, $chosen.Query
"  total cost         : {0:N0} RU" -f $totalRu
"  floor at {0} RU/s : {1:N0}s ({2:N1} min) of pure budget consumption" -f $BudgetRuPerSecond, $seconds, ($seconds / 60)
if ($bare -and $bare.ResourceUnits -gt $chosen.ResourceUnits) {
    $extra = $groupCount * ($bare.ResourceUnits - $chosen.ResourceUnits)
    "  dropping `$select   : {0:N0} RU more ({1:P0} increase)" -f `
        $extra, ($extra / $totalRu)
}

# Cost is a property of the query SHAPE, not the object count: $select reduces it, $expand
# raises it. A 403 or 400 costs 0 RU, so a fan-out that fails uniformly looks fast and healthy
# while measuring nothing - check status, not duration.

<#
Expected output:

=== What each query shape costs ===

Query                       ResourceUnits
-----                       -------------
group members (bare)                    3
group members ($select)                 2
transitiveMembers (bare)                4
transitiveMembers ($select)             3
single group read                       1
users list ($top=25)                    2
users list ($select)                    1


=== Sizing a per-group fan-out over the whole tenant ===
  groups in tenant   : 15,779
  cost per group     : 3 RU  (transitiveMembers ($select))
  total cost         : 47,337 RU
  floor at 800 RU/s : 59s (1.0 min) of pure budget consumption
  dropping $select   : 15,779 RU more (33% increase)
#>
