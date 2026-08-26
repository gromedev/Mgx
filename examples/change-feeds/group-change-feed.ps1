# Watch one resource for changes: baseline now, then get only what changed.
#
# This is the smallest useful change feed, and the thing to read before
# tenant-change-feed.ps1, which does the same across several resources at once.
#
# Two ideas:
#   * -Latest baselines the delta token from now, instantly, without enumerating
#     the resource first. A change feed does not want the initial dump - it wants
#     "what happened since I started watching".
#   * Delta state is a file. Point later runs at the same -DeltaPath and they pick
#     up exactly where the last one stopped, which makes this a cron job.
#
# Requirements: Connect-MgGraph -Scopes "Group.Read.All"

Import-Module Mgx

$deltaFile = "./groups.delta"
$feedFile  = "./groups-feed.jsonl"

# First run only: start the feed from now instead of enumerating every group.
if (-not (Test-Path $deltaFile)) {
    Write-Host "First run: baselining from now (no initial enumeration)."
    $null = Sync-MgxDelta /groups/delta -DeltaPath $deltaFile -Latest
    Write-Host "Baselined. Change a group, then run this again."
    return
}

$changes = Sync-MgxDelta /groups/delta -DeltaPath $deltaFile

foreach ($group in $changes) {
    $group | ConvertTo-Json -Compress -Depth 10 | Add-Content $feedFile
}

Write-Host "$($changes.Count) change(s) since the last run, appended to $feedFile"
$changes |
    Select-Object @{ Name = 'Change'; Expression = { if ($_.'@removed') { 'removed' } else { 'changed' } } },
        id, displayName |
    Format-Table -AutoSize

<#
Expected output (second run):

3 change(s) since the last run, appended to ./groups-feed.jsonl

Change  id                                   displayName
------  --                                   -----------
changed 5cf66b99-8a06-436a-8107-644f1a1c4b3e Mark 8 Project Team
changed 24ffeed6-38b4-4bee-ae29-f0d18b5b7a92 Engineering All Hands
removed 8b1f2c04-19ac-4c0b-9a58-7f5e4c1d2a11
#>
