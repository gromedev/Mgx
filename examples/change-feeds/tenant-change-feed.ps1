# A tenant change feed: everything that changed since the last run, resumable.
#
# group-change-feed.ps1 does this for a single resource and is the shorter read;
# this is the same idea across several resources, with the details that show up
# at tenant scale - checkpointing and duplicate collapsing.
#
# A feed baselined with -Latest deliberately skips what existed before it
# started. If you want that too, tenant-inventory.ps1 is the snapshot half:
# inventory once, then this feed keeps it current.
#
# Baseline once with -Latest (instant - no initial enumeration), then each
# scheduled run appends only the changes to a JSONL log. Delta state makes
# it incremental; -CheckpointPath makes a killed run continue instead of
# restarting. "What changed in the tenant since yesterday" as a cron job.
#
# Requirements: Connect-MgGraph -Scopes "Group.Read.All","Application.Read.All"

Import-Module Mgx

$stateDir = "./tenant-feed"
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null

# --- groups: membership and lifecycle changes ---
#
# Baseline with -Latest on the very first run. This matters more than it looks: without it
# the first call enumerates every group in the tenant, and Graph does not promise one
# response per object. Measured on a 15,779-group tenant, a full enumeration emitted
# 156,413 objects - the same groups repeated ~10x across pages, which is explicitly allowed
# ("can't ensure that entities are unified in a single response"). -Latest skips all of it
# and starts the feed from now, which is what a change feed actually wants.
$groupState = "$stateDir/groups.delta"
if (-not (Test-Path $groupState)) {
    Write-Host "First run: baselining groups from now (no initial enumeration)."
    $null = Sync-MgxDelta /groups/delta -DeltaPath $groupState -Latest
}

$groupChanges = Sync-MgxDelta /groups/delta `
    -DeltaPath $groupState `
    -CheckpointPath "$stateDir/groups.checkpoint"

# Dedup by id, last write wins. Incremental rounds are usually clean, but the replay above
# is a property of delta itself, not of the initial page - so anything that treats one
# emitted object as one change event needs this. Keep the LAST occurrence: for an object
# that changed twice in one window, that is the current state.
$latestById = [ordered]@{}
foreach ($g in $groupChanges) { $latestById[[string]$g.id] = $g }
foreach ($g in $latestById.Values) {
    $g | ConvertTo-Json -Compress -Depth 10 | Add-Content "$stateDir/groups-feed.jsonl"
}
if ($groupChanges.Count -ne $latestById.Count) {
    Write-Host ("  collapsed {0} emitted objects into {1} distinct groups" -f `
        $groupChanges.Count, $latestById.Count)
}

# --- service principals: the security variant - new app registrations,
#     credential changes, and deletions stand out in this feed ---
$spChanges = Sync-MgxDelta /servicePrincipals/delta `
    -DeltaPath "$stateDir/sp.delta" `
    -CheckpointPath "$stateDir/sp.checkpoint"

$new     = $spChanges | Where-Object { -not $_.'@removed' -and $_.appId }
$removed = $spChanges | Where-Object { $_.'@removed' }
foreach ($sp in $spChanges) {
    $sp | ConvertTo-Json -Compress -Depth 10 | Add-Content "$stateDir/sp-feed.jsonl"
}

Write-Host ("Groups changed: {0}  |  Service principals: {1} changed, {2} removed" -f `
    $groupChanges.Count, $new.Count, $removed.Count)
if ($new.Count -gt 0) {
    Write-Host "Review new/changed service principals:"
    $new | Select-Object appDisplayName, appId, accountEnabled | Format-Table -AutoSize
}

<#
Expected output:

First run: baselining groups from now (no initial enumeration).
Groups changed: 3  |  Service principals: 3 changed, 0 removed
Review new/changed service principals:

appDisplayName            appId                                accountEnabled
--------------            -----                                --------------
Contoso Expense Portal    dbc7b719-a215-42e9-bf03-7f78957e21a9           True
Contoso HR Sync           01d9b6b1-564d-4c5d-843f-10c58eccdffd           True
Contoso Reporting Service 14b89564-33a9-402c-ab8e-2bc84a7164ab          False

tenant-feed/groups-feed.jsonl (first 2 lines):
{"displayName":"Mark 8 Project Team","groupTypes":["Unified"],"id":"5cf66b99-8a06-436a-8107-644...
{"displayName":"Engineering All Hands","groupTypes":["Unified"],"id":"24ffeed6-38b4-4bee-ae29-f...
#>
