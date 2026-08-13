# Benchmark 05: export every user to JSONL - the memory story.
# Contenders:
#   mgx-export - Export-MgxCollection: streams raw JSON to disk, no object conversion
#   mgx-pipe   - Invoke-MgxRequest -All | Out-File: streams but pays the conversion tax
#   rest       - Invoke-RestMethod: buffers all pages in memory, then writes
# Reports wall time, peak working set, and managed-heap delta for each. Single run per
# contender by default (export is IO-heavy; variance is dominated by service latency).
param(
    [int] $Runs = 1
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Connect-MgxBenchmark

$props = 'id', 'displayName', 'mail', 'department', 'jobTitle', 'accountEnabled'
$select = $props -join ','
$tmp = [System.IO.Path]::GetTempPath()
$results = [ordered]@{}

function Invoke-Runs([string]$name, [scriptblock]$script) {
    if ($Runs -gt 1) { Measure-BenchMedian -Name $name -Runs $Runs -Script $script }
    else { Measure-BenchPass -Name $name -Script $script }
}

$results.mgxExport = Invoke-Runs 'Export-MgxCollection (JSONL)' {
    $out = Join-Path $tmp 'bench-mgx-export.jsonl'
    $r = Export-MgxCollection /users -OutputFile $out -All -Property $props
    $size = (Get-Item $out).Length
    Remove-Item $out -ErrorAction SilentlyContinue
    @{ count = $r.ItemCount; fileMB = [math]::Round($size / 1MB, 1) }
}

$results.mgxPipe = Invoke-Runs 'Invoke-MgxRequest -All | file' {
    $out = Join-Path $tmp 'bench-mgx-pipe.jsonl'
    $n = 0
    Invoke-MgxRequest /users -All -Property $props -Raw |
        ForEach-Object { $n++; $_ } | Out-File $out
    Remove-Item $out -ErrorAction SilentlyContinue
    @{ count = $n }
}

$results.rest = Invoke-Runs 'Invoke-RestMethod buffer+write' {
    $out = Join-Path $tmp 'bench-rest.jsonl'
    $tok = Get-BenchAppToken
    $headers = @{ Authorization = "Bearer $tok" }
    # The way ad-hoc scripts do it: accumulate everything, then write once.
    $all = [System.Collections.Generic.List[object]]::new()
    $url = 'https://graph.microsoft.com/v1.0/users?$top=999&$select=' + $select
    while ($url) {
        $page = Invoke-RestMethod -Uri $url -Headers $headers
        foreach ($u in $page.value) { $all.Add($u) }
        $url = $page.'@odata.nextLink'
    }
    $sw = [System.IO.StreamWriter]::new($out)
    foreach ($u in $all) { $sw.WriteLine(($u | ConvertTo-Json -Compress -Depth 5)) }
    $sw.Close()
    Remove-Item $out -ErrorAction SilentlyContinue
    @{ count = $all.Count }
}

Write-Host ''
Write-Host '=== EXPORT / MEMORY ==='
foreach ($k in $results.Keys) {
    $m = if ($results[$k].PSObject.Properties['Median']) { $results[$k].Median } else { $results[$k] }
    Write-Host ("{0,-32} {1,8:F1}s  count={2}  peakWS={3}MB  wsDelta={4}MB  heapDelta={5}MB" -f `
        $m.Name, ($m.ElapsedMs / 1000), $m.Output.count, $m.PeakWorkingSetMB, $m.WorkingSetDeltaMB, $m.ManagedHeapDeltaMB)
}
Write-BenchResult -Benchmark '05-memory-export' -Result $results
