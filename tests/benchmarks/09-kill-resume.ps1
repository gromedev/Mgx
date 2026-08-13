# Benchmark 09: checkpoint/resume correctness + overhead.
# Three measurements:
#   baseline    - full export, no checkpoint (the speed reference)
#   checkpointed- full export with -CheckpointPath (same work + checkpoint writes)
#                 -> overhead % is the price of crash insurance
#   kill/resume - start the checkpointed export in a child process, kill it around
#                 half-way, resume in-process, then verify the line count matches the
#                 tenant exactly and no id appears twice (the mid-page dedup claim).
# Run with -AsChild (internal): executes just the export half for the kill target.
param(
    [switch] $AsChild,
    [string] $ChildOut,
    [string] $ChildCheckpoint
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Connect-MgxBenchmark

$props = 'id', 'displayName', 'department'

if ($AsChild) {
    # Kill target: just run the checkpointed export until murdered.
    $null = Export-MgxCollection /users -OutputFile $ChildOut -CheckpointPath $ChildCheckpoint -All -Property $props
    exit 0
}

$tmp = [System.IO.Path]::GetTempPath()
$outBase   = Join-Path $tmp 'bench09-baseline.jsonl'
$outCp     = Join-Path $tmp 'bench09-checkpointed.jsonl'
$outKill   = Join-Path $tmp 'bench09-kill.jsonl'
$cpPath    = Join-Path $tmp 'bench09-cp.json'
Remove-Item $outBase, $outCp, $outKill, $cpPath -ErrorAction SilentlyContinue
$results = [ordered]@{}

# --- Baseline / overhead ---
$results.baseline = Measure-BenchPass -Name 'export, no checkpoint' -Script {
    $r = Export-MgxCollection /users -OutputFile $outBase -All -Property $props
    @{ items = $r.ItemCount }
}
$truth = $results.baseline.Output.items

$results.checkpointed = Measure-BenchPass -Name 'export, with checkpoint' -Script {
    $r = Export-MgxCollection /users -OutputFile $outCp -CheckpointPath $cpPath -All -Property $props
    @{ items = $r.ItemCount }
}
$overheadPct = [math]::Round(
    ($results.checkpointed.ElapsedMs - $results.baseline.ElapsedMs) * 100.0 / $results.baseline.ElapsedMs, 1)

# --- Kill/resume ---
# Purge the final file, checkpoint, AND any stale temp from a previous run's kill:
# a leftover temp can otherwise win the glob below and blind the watcher.
Remove-Item "$outKill*" -ErrorAction SilentlyContinue
Remove-Item $cpPath -ErrorAction SilentlyContinue
Write-Host "starting child export (kill target), aiming to kill near 50%..."
$child = Start-Process pwsh -PassThru -ArgumentList `
    '-NoProfile', '-File', $PSCommandPath, '-AsChild', '-ChildOut', $outKill, '-ChildCheckpoint', $cpPath
# Byte-based kill trigger: counting lines with Get-Content was slower than the
# export itself, so the child finished before the kill. File length is O(1).
$avgLineBytes = [double](Get-Item $outBase).Length / $truth
$halfBytes = [long]((Get-Item $outBase).Length * 0.45)
$killedAt = -1
foreach ($i in 1..3000) {
    Start-Sleep -Milliseconds 200
    if ($child.HasExited) { break }
    # A FRESH export writes to <output>.<guid>.tmp and renames at the end - the
    # final path stays empty until completion, so watch the temp file.
    $bytes = 0
    $tmpFile = Get-ChildItem "$outKill.*.tmp" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($tmpFile) { $bytes = $tmpFile.Length }
    elseif (Test-Path $outKill) { $bytes = (Get-Item $outKill).Length }
    if ($bytes -ge $halfBytes) {
        $child.Kill()
        $killedAt = [int]($bytes / $avgLineBytes)
        break
    }
}
if ($child.HasExited -and $killedAt -lt 0) {
    Write-Host 'WARNING: child finished before the kill - tenant too small or kill loop too slow'
}
else {
    Write-Host ("killed child at ~{0} of {1} items" -f $killedAt, $truth)
}

$results.resume = Measure-BenchPass -Name 'resume from checkpoint' -Script {
    $r = Export-MgxCollection /users -OutputFile $outKill -CheckpointPath $cpPath -All -Property $props
    @{ itemsThisRun = $r.ItemCount; resumedFrom = $r.ResumedFrom }
}

# --- Verify: exact count, zero duplicate ids ---
$idRegex = [regex]'"id"\s*:\s*"([^"]+)"'
$seen = [System.Collections.Generic.HashSet[string]]::new()
$total = 0; $dupes = 0
foreach ($line in [System.IO.File]::ReadLines($outKill)) {
    $total++
    $m = $idRegex.Match($line)
    if ($m.Success -and -not $seen.Add($m.Groups[1].Value)) { $dupes++ }
}

Write-Host ''
Write-Host '=== KILL / RESUME ==='
Write-Host ("baseline export        {0,8:F1}s  ({1} items)" -f ($results.baseline.ElapsedMs / 1000), $truth)
Write-Host ("with checkpoint        {0,8:F1}s  (overhead {1}%)" -f ($results.checkpointed.ElapsedMs / 1000), $overheadPct)
Write-Host ("killed at ~{0}, resumed; final file: {1} lines (expected {2}), duplicate ids: {3}" -f `
    $killedAt, $total, $truth, $dupes)
$verdict = if ($total -eq $truth -and $dupes -eq 0) { 'EXACT - no loss, no duplicates' } else { 'MISMATCH - investigate' }
Write-Host ("verdict: {0}" -f $verdict)
# Report what resume actually did. Current Mgx behavior: a killed FIRST run leaves
# only an orphaned temp file, so the checkpoint is declared stale and the "resume"
# is a full restart (documented in NOTES.md as a 1.0.5 candidate).
$resumedFrom = $results.resume.Output.resumedFrom
Write-Host ($(if ($resumedFrom) { "resume semantics: TRUE RESUME from item $resumedFrom" }
             else { 'resume semantics: checkpoint discarded (fresh-run temp design) - full restart, no corruption' }))

$results.verification = @{ expected = $truth; actual = $total; duplicateIds = $dupes; killedAt = $killedAt; overheadPct = $overheadPct; verdict = $verdict }
Write-BenchResult -Benchmark '09-kill-resume' -Result $results
Remove-Item $outBase, $outCp, $outKill, $cpPath -ErrorAction SilentlyContinue
