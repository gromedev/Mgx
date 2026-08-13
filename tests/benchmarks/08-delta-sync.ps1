# Benchmark 08: delta sync - what incremental costs after the initial pull.
# Measures three phases with Sync-MgxDelta:
#   initial     - first sync: full enumeration + delta token capture
#   incremental - after PATCHing $ChangeCount users: only changes come back
#   steady      - immediately after: a no-change sync (the nightly-cron cost)
# The "SDK equivalent" of incremental sync is re-enumerating everything - that is
# benchmark 01's number; the README table cites it as the comparison column.
param(
    [int] $ChangeCount = 200,
    [int] $PropagationWaitSeconds = 30
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Connect-MgxBenchmark

$deltaPath = Join-Path ([System.IO.Path]::GetTempPath()) 'bench08-delta.json'
Remove-Item $deltaPath -ErrorAction SilentlyContinue
$results = [ordered]@{}
$props = 'id', 'displayName', 'officeLocation'

$results.initial = Measure-BenchPass -Name 'initial full sync' -Script {
    $n = 0
    Sync-MgxDelta /users/delta -DeltaPath $deltaPath -Property $props | ForEach-Object { $n++ }
    @{ items = $n }
}

# --- Make a known number of changes ---
Write-Host "patching $ChangeCount users to generate delta changes..."
$ids = [System.Collections.Generic.List[string]]::new()
Invoke-MgxRequest /users -All -Filter "startsWith(userPrincipalName,'bench.u')" -Property id |
    Select-Object -First $ChangeCount | ForEach-Object { $ids.Add($_.id) }
$stamp = 'Bench08-' + (Get-Date -Format 'HHmmss')
$null = $ids | ForEach-Object { "/users/$_" } |
    Invoke-MgxBatchRequest -Method PATCH -Body @{ officeLocation = $stamp } -ErrorAction SilentlyContinue
Write-Host "waiting ${PropagationWaitSeconds}s for delta propagation..."
Start-Sleep -Seconds $PropagationWaitSeconds

$results.incremental = Measure-BenchPass -Name "incremental sync ($ChangeCount changed)" -Script {
    $n = 0
    Sync-MgxDelta /users/delta -DeltaPath $deltaPath -Property $props | ForEach-Object { $n++ }
    @{ items = $n }
}

$results.steady = Measure-BenchPass -Name 'steady-state sync (no changes)' -Script {
    $n = 0
    Sync-MgxDelta /users/delta -DeltaPath $deltaPath -Property $props | ForEach-Object { $n++ }
    @{ items = $n }
}

Remove-Item $deltaPath -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '=== DELTA SYNC ==='
foreach ($k in $results.Keys) {
    $r = $results[$k]
    Write-Host ("{0,-36} {1,8:F1}s  items={2}" -f $r.Name, ($r.ElapsedMs / 1000), $r.Output.items)
}
Write-Host '(compare incremental against benchmark 01 full enumeration - that is the re-pull cost it replaces)'
Write-BenchResult -Benchmark '08-delta-sync' -Result $results
