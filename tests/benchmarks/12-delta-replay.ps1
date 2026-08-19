# Benchmark 12: how many times does a delta enumeration hand you the same object?
#
# Graph's delta endpoints do not paginate a snapshot. An object can appear on more than one
# page of a single enumeration, so the number of objects a delta run EMITS is not the number
# of objects that exist. A caller that counts rows, or appends them to a file, or feeds them
# into a report, gets a number larger than the directory holds and no indication why.
#
# Nothing here is a comparison between tools: Mgx does not deduplicate delta output either.
# The point is to measure the replay factor against known ground truth, so the size of the
# gap between "emitted" and "distinct" is a recorded number rather than folklore.
param(
    [string] $Resource = '/groups/delta',
    [string] $CountResource = '/groups/$count',
    [int] $Top = 0
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Connect-MgxBenchmark

# Ground truth from the directory's own index, not by walking pages.
$token = Get-BenchAppToken
$truth = [int] (Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0$CountResource" `
    -Headers @{ Authorization = "Bearer $token"; ConsistencyLevel = 'eventual' })
Write-Host "ground truth: $truth objects (from $CountResource)"

$results = [ordered]@{}

# --- Raw REST walk of the delta pages -----------------------------------------------------
Write-Host 'walking delta pages with Invoke-RestMethod...'
$seen = [System.Collections.Generic.HashSet[string]]::new()
$emitted = 0; $pages = 0; $removed = 0
$url = "https://graph.microsoft.com/v1.0$Resource"
if ($Top -gt 0) { $url += '?$top=' + $Top }
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($url) {
    if ($pages -gt 0 -and $pages % 400 -eq 0) { $token = Get-BenchAppToken }
    $page = Invoke-RestMethod -Uri $url -Headers @{ Authorization = "Bearer $token" }
    $pages++
    foreach ($o in $page.value) {
        $emitted++
        if ($o.PSObject.Properties.Name -contains '@removed') { $removed++ }
        if ($o.id) { [void]$seen.Add($o.id) }
    }
    $url = $page.'@odata.nextLink'
}
$sw.Stop()
$results.rest = @{
    Emitted = $emitted; Distinct = $seen.Count; Pages = $pages; Removed = $removed
    WallMs  = $sw.ElapsedMilliseconds
}

# --- Sync-MgxDelta over the same resource -------------------------------------------------
Write-Host 'walking the same resource with Sync-MgxDelta...'
$deltaPath = Join-Path ([System.IO.Path]::GetTempPath()) 'bench12-delta.json'
Remove-Item $deltaPath -ErrorAction SilentlyContinue
$seenMgx = [System.Collections.Generic.HashSet[string]]::new()
$emittedMgx = 0
Get-MgxTelemetry -Reset | Out-Null
$sw2 = [Diagnostics.Stopwatch]::StartNew()
$syncArgs = @{ Uri = $Resource; DeltaPath = $deltaPath }
if ($Top -gt 0) { $syncArgs.Top = $Top }
Sync-MgxDelta @syncArgs | ForEach-Object {
    $emittedMgx++
    if ($_.id) { [void]$seenMgx.Add($_.id) }
}
$sw2.Stop()
$results.mgx = @{
    Emitted = $emittedMgx; Distinct = $seenMgx.Count; WallMs = $sw2.ElapsedMilliseconds
}
Remove-Item $deltaPath -ErrorAction SilentlyContinue

# --- Report -------------------------------------------------------------------------------
Write-Host ''
Write-Host "=== DELTA REPLAY ($Resource, ground truth $truth) ==="
Write-Host ('{0,-26} {1,10} {2,10} {3,8} {4,9}' -f 'Contender', 'Emitted', 'Distinct', 'Replay', 'Wall')
foreach ($k in 'rest', 'mgx') {
    $r = $results[$k]
    $label = if ($k -eq 'rest') { 'Invoke-RestMethod' } else { 'Sync-MgxDelta' }
    $factor = if ($r.Distinct -gt 0) { $r.Emitted / $r.Distinct } else { 0 }
    Write-Host ('{0,-26} {1,10} {2,10} {3,7:F1}x {4,8:F1}s' -f `
        $label, $r.Emitted, $r.Distinct, $factor, ($r.WallMs / 1000))
}
Write-Host ("REST pages: $($results.rest.Pages), objects marked @removed: $($results.rest.Removed)")
Write-Host ''
Write-Host 'Distinct should equal ground truth. Emitted above Distinct is replay: the same object'
Write-Host 'handed to the caller more than once inside a single enumeration.'

$results.groundTruth = $truth
$results.resource = $Resource
Write-BenchResult -Benchmark '12-delta-replay' -Result $results -WallMs $results.mgx.WallMs
