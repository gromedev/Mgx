# A tenant change feed: everything that changed since the last run, resumable.
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
$groupChanges = Sync-MgxDelta /groups/delta `
    -DeltaPath "$stateDir/groups.delta" `
    -CheckpointPath "$stateDir/groups.checkpoint"
# First ever run? Baseline from now instead of enumerating everything:
#   Sync-MgxDelta /groups/delta -DeltaPath "$stateDir/groups.delta" -Latest

foreach ($g in $groupChanges) {
    $g | ConvertTo-Json -Compress -Depth 10 | Add-Content "$stateDir/groups-feed.jsonl"
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
    $new | Format-Table appDisplayName, appId, accountEnabled -AutoSize
}
