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

<#
Expected output:

VERBOSE: No existing delta state. Performing full initial sync.
VERBOSE: Delta state saved to '/tmp/tenant-sync/drive-delta.json'.
VERBOSE: Delta sync complete: 11 items in 0.2s. Output: /tmp/tenant-sync/drive-items.jsonl
10 live items, 1 deleted

drive-items.jsonl (first 2 lines):
{"id":"01381BR3EHVNURTDV4B1GIK7R5GN7ON4SL","name":"Q4-Revenue-Report.pdf","size":2418912,"lastM...
{"id":"019NF2ODIGGGJDG696J7PRMO65G9CEUEJ2","name":"Meeting-Notes.txt","size":12480,"lastModifie...
#>
