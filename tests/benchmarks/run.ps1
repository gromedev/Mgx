# Runs the Mgx benchmark suite. Each benchmark executes in a fresh pwsh process so
# Mgx's static state (rate limiter, adapted pacing, circuit breaker) never leaks
# between benchmarks. Order matters: read benchmarks run first; the two
# throttle-provoking write benchmarks (07, 04) run last with a cooldown, so they
# cannot poison the read numbers.
# Usage:
#   ./run.ps1                     # everything except the slow ones
#   ./run.ps1 -IncludeSlow        # everything, including 04 full baselines (~2h)
#   ./run.ps1 -Only 01,05         # just those benchmarks
param(
    [string[]] $Only,
    [switch] $IncludeSlow,
    [int] $CooldownSeconds = 300
)

$ErrorActionPreference = 'Stop'
$logDir = Join-Path $PSScriptRoot 'results/logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

# name, script, extra args, needsCooldownAfter
$suite = @(
    @{ id = '06'; script = '06-fault-gauntlet.ps1';  args = @();                 cooldown = $false },
    @{ id = '01'; script = '01-list-users.ps1';      args = @();                 cooldown = $false },
    @{ id = '05'; script = '05-memory-export.ps1';   args = @();                 cooldown = $false },
    @{ id = '02'; script = '02-fanout-lookup.ps1';   args = @();                 cooldown = $false },
    @{ id = '03'; script = '03-user-report.ps1';     args = @();                 cooldown = $false },
    @{ id = '08'; script = '08-delta-sync.ps1';      args = @();                 cooldown = $false },
    @{ id = '09'; script = '09-kill-resume.ps1';     args = @();                 cooldown = $false },
    @{ id = '07'; script = '07-adaptive-pacing.ps1'; args = @();                 cooldown = $true },
    @{ id = '04'; script = '04-batch-create.ps1';    args = $(if ($IncludeSlow) { @() } else { @('-SkipBaselines') }); cooldown = $false }
)

if ($Only) {
    # pwsh -File passes "05,02" as one literal string - split so both call styles work
    $Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
    $suite = @($suite | Where-Object { $Only -contains $_.id })
}
if ($suite.Count -eq 0) { throw "no benchmarks matched -Only $($Only -join ',')" }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outcomes = @()
foreach ($b in $suite) {
    $log = Join-Path $logDir "$($b.id)-$stamp.log"
    Write-Host ''
    Write-Host ("### running {0} -> {1}" -f $b.script, $log)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot $b.script) @($b.args) 2>&1 | Tee-Object -FilePath $log
    $exit = $LASTEXITCODE
    $sw.Stop()
    $outcomes += [pscustomobject]@{ Benchmark = $b.id; ExitCode = $exit; Minutes = [math]::Round($sw.Elapsed.TotalMinutes, 1) }
    if ($exit -ne 0) { Write-Warning "$($b.script) exited $exit - continuing" }
    if ($b.cooldown) {
        Write-Host "cooldown ${CooldownSeconds}s before next benchmark..."
        Start-Sleep -Seconds $CooldownSeconds
    }
}

Write-Host ''
Write-Host '=== SUITE COMPLETE ==='
$outcomes | Format-Table -AutoSize
Write-Host "per-benchmark results in $(Join-Path $PSScriptRoot 'results')"
