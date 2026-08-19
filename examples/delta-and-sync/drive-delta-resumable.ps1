# Enumerate an entire OneDrive/SharePoint drive to JSONL - resumable.
#
# Drive enumerations at scale run for hours; a crash, Ctrl+C, or network
# death should not restart them from zero. -CheckpointPath saves the
# position at every page boundary, so re-running the identical command
# continues where the last run stopped. On successful completion the
# checkpoint is deleted and the delta token is saved: the NEXT run
# returns only what changed - the incremental backbone for backup-shaped
# workloads.
#
# Requirements: Connect-MgGraph -Scopes "Files.Read.All"

Import-Module Mgx

$deltaFile      = "./drive-delta.json"
$checkpointFile = "./drive-delta.checkpoint"
$outputFile     = "./drive-items.jsonl"

# First run: full enumeration (resumable). Later runs: changes only.
Sync-MgxDelta /me/drive/root/delta `
    -DeltaPath $deltaFile `
    -CheckpointPath $checkpointFile `
    -OutputFile $outputFile `
    -Prefer deltashowremovedasdeleted `
    -Verbose

# The JSONL is one driveItem per line. Deleted items carry the "deleted"
# facet (because of the Prefer token above); resume is at-least-once, so
# deduplicate on id if this feeds a downstream system:
$items = Get-Content $outputFile | ConvertFrom-Json
$live = $items | Where-Object { -not $_.deleted } | Sort-Object id -Unique
Write-Host "$($live.Count) live items, $(@($items | Where-Object deleted).Count) deleted"

# Tip - track changes WITHOUT the initial enumeration: baseline from now
# with -Latest, then every later run returns only changes:
#   Sync-MgxDelta /me/drive/root/delta -DeltaPath $deltaFile -Latest
