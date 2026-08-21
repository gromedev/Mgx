# Benchmark 14: what adaptive pacing costs a SHORT run.
#
# Benchmark 07 measures pacing across a long fan-out, where slow start is amortised away and the
# gate reads as free. That is true and it is only half the picture: the pacer opens cold at 4 rps
# and doubles each clean second, so a run that finishes DURING the ramp pays for all of it. The
# README table needs both numbers, and until this existed only the long one came from the suite.
#
# Each arm runs in a fresh process, because the adapted rate is per-process state and a warm
# pacer would measure nothing.
param(
    [int] $Count = 50,
    [int] $Concurrency = 8,
    [ValidateSet('both', 'paced', 'unpaced')] [string] $Mode = 'both'
)

. "$PSScriptRoot/common.ps1"

if ($Mode -eq 'both') {
    $results = [ordered]@{}
    foreach ($m in 'paced', 'unpaced') {
        $out = & pwsh -NoProfile -File $PSCommandPath -Mode $m -Count $Count -Concurrency $Concurrency
        $line = $out | Where-Object { $_ -match '^RESULT\|' } | Select-Object -Last 1
        if (-not $line) { throw "arm '$m' produced no result" }
        $null, $secs, $requests, $pacingMs = $line -split '\|'
        $results[$m] = @{ Seconds = [double]$secs; Requests = [int]$requests; PacingWaitMs = [long]$pacingMs }
    }

    Write-Host ''
    Write-Host "=== ADAPTIVE PACING ON A COLD SHORT RUN ($Count lookups, concurrency $Concurrency) ==="
    Write-Host ('{0,-28} {1,8} {2,10} {3,14}' -f 'Arm', 'Wall', 'Requests', 'PacingWaitMs')
    foreach ($k in 'paced', 'unpaced') {
        $r = $results[$k]
        $label = if ($k -eq 'paced') { 'Adaptive pacing on (default)' } else { '-NoAdaptivePacing' }
        Write-Host ('{0,-28} {1,7:F1}s {2,10} {3,14}' -f $label, $r.Seconds, $r.Requests, $r.PacingWaitMs)
    }
    if ($results.unpaced.Seconds -gt 0) {
        Write-Host ('ratio                        {0,6:F1}x' -f ($results.paced.Seconds / $results.unpaced.Seconds))
    }
    Write-Host 'Compare against benchmark 07, where the same gate costs nothing across a long run.'
    Write-BenchResult -Benchmark '14-pacing-cold-cost' -Result $results -WallMs ([long]($results.paced.Seconds * 1000))
    return
}

Import-MgxLocal
Connect-MgxBenchmark | Out-Null
if ($Mode -eq 'unpaced') { Set-MgxOption -NoAdaptivePacing }

$ids = Invoke-MgxRequest /users -Top $Count -Property id -WarningAction SilentlyContinue |
    ForEach-Object { $_.id }
Get-MgxTelemetry -Reset | Out-Null

$sw = [Diagnostics.Stopwatch]::StartNew()
$null = $ids | Invoke-MgxRequest -Uri '/users/{id}?$select=id' -Concurrency $Concurrency
$sw.Stop()

$t = Get-MgxTelemetry
"RESULT|{0}|{1}|{2}" -f $sw.Elapsed.TotalSeconds, $t.Requests, $t.AdaptivePacingWaitMs
