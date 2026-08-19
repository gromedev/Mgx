# Shared plumbing for the Mgx benchmark suite. Dot-source from each benchmark script:
#   . "$PSScriptRoot/common.ps1"
# Auth resolution order: existing Graph session, then $env:MGX_BENCH_APP, then ~/.mgx-bench/app.json.

$ErrorActionPreference = 'Stop'

# Benchmark output must be locale-stable (39.2s, never 39,2s) - results land in the README.
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture

function Connect-MgxBenchmark {
    if (-not (Get-Module Microsoft.Graph.Authentication)) {
        Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
    }

    $ctx = Get-MgContext
    if ($ctx) {
        Write-Host "Using existing Graph session ($($ctx.AuthType): $($ctx.Account ?? $ctx.ClientId))"
        return
    }

    $credPath = if ($env:MGX_BENCH_APP) { $env:MGX_BENCH_APP }
                else { Join-Path $HOME '.mgx-bench/app.json' }
    if (-not (Test-Path $credPath)) {
        throw "No Graph session and no app credentials at '$credPath'. Connect-MgGraph first, or set MGX_BENCH_APP."
    }
    $cfg  = Get-Content $credPath -Raw | ConvertFrom-Json
    $cred = [pscredential]::new($cfg.appId, (ConvertTo-SecureString $cfg.clientSecret -AsPlainText -Force))
    Connect-MgGraph -TenantId $cfg.tenantId -ClientSecretCredential $cred -NoWelcome
    Write-Host "Connected app-only ($($cfg.appId))"
}

# Mints a raw app-only bearer token for the Invoke-RestMethod baselines,
# which deliberately bypass every SDK/Mgx layer.
function Get-BenchAppToken {
    $credPath = if ($env:MGX_BENCH_APP) { $env:MGX_BENCH_APP }
                else { Join-Path $HOME '.mgx-bench/app.json' }
    if (-not (Test-Path $credPath)) { throw "No app credentials at '$credPath' (set MGX_BENCH_APP)." }
    $cfg = Get-Content $credPath -Raw | ConvertFrom-Json
    # Bounded + retried: an unbounded token mint hung a 5,000-call benchmark at item 500
    foreach ($attempt in 1..3) {
        try {
            $resp = Invoke-RestMethod -Method POST -Uri "https://login.microsoftonline.com/$($cfg.tenantId)/oauth2/v2.0/token" `
                -Body @{ grant_type = 'client_credentials'; client_id = $cfg.appId; client_secret = $cfg.clientSecret; scope = 'https://graph.microsoft.com/.default' } `
                -TimeoutSec 30
            return $resp.access_token
        }
        catch { if ($attempt -eq 3) { throw }; Start-Sleep -Seconds 5 }
    }
}

function Import-MgxLocal {
    # Users FIRST: it auto-loads its exactly-matching Microsoft.Graph.Authentication.
    # Importing Mgx (or Authentication) first pulls the newest Auth assembly, and a
    # different-versioned Users then fails with "assembly with same name already loaded".
    if (-not (Get-Module Microsoft.Graph.Users)) { Import-Module Microsoft.Graph.Users }
    # Local build first (repo checkout), gallery module as fallback.
    $local = Join-Path $PSScriptRoot '../../module/mgx.psd1'
    if (Test-Path $local) { Import-Module $local -Force }
    else { Import-Module Mgx -Force }
}

# Runs one measured pass: telemetry snapshot around $Script, wall time, peak working set
# sampled from a background thread. Returns a result object; does not write anywhere.
function Measure-BenchPass {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Script
    )

    [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
    $proc = [System.Diagnostics.Process]::GetCurrentProcess()
    $proc.Refresh()
    $wsBefore = $proc.WorkingSet64
    $heapBefore = [GC]::GetTotalMemory($true)

    # Peak-RSS sampler in compiled C#: a PowerShell scriptblock cannot run on a bare
    # .NET thread (no runspace), so the sampling loop must not be PowerShell at all.
    if (-not ('MgxBench.RssSampler' -as [type])) {
        Add-Type -TypeDefinition @'
namespace MgxBench {
    public class RssSampler {
        private System.Threading.Thread _t;
        private volatile bool _stop;
        public long Peak;
        public void Start() {
            var p = System.Diagnostics.Process.GetCurrentProcess();
            Peak = p.WorkingSet64;
            _t = new System.Threading.Thread(() => {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                while (!_stop) {
                    proc.Refresh();
                    if (proc.WorkingSet64 > Peak) Peak = proc.WorkingSet64;
                    System.Threading.Thread.Sleep(200);
                }
            });
            _t.IsBackground = true;
            _t.Start();
        }
        public void Stop() { _stop = true; if (_t != null) _t.Join(2000); }
    }
}
'@
    }
    $sampler = [MgxBench.RssSampler]::new()
    $sampler.Start()

    $telemetryBefore = $null
    try { $telemetryBefore = Get-MgxTelemetry } catch { }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $scriptOutput = & $Script
    $sw.Stop()

    $sampler.Stop()

    $telemetryAfter = $null
    try { $telemetryAfter = Get-MgxTelemetry } catch { }

    $proc.Refresh()
    $heapAfter = [GC]::GetTotalMemory($false)

    $tel = $null
    if ($telemetryBefore -and $telemetryAfter) {
        $tel = [pscustomobject]@{
            Requests          = $telemetryAfter.Requests         - $telemetryBefore.Requests
            ThrottleRetries   = $telemetryAfter.ThrottleRetries  - $telemetryBefore.ThrottleRetries
            OtherRetries      = $telemetryAfter.OtherRetries     - $telemetryBefore.OtherRetries
            HttpMs            = $telemetryAfter.HttpMs           - $telemetryBefore.HttpMs
            RateLimiterWaitMs = $telemetryAfter.RateLimiterWaitMs - $telemetryBefore.RateLimiterWaitMs
            RetryDelayMs      = $telemetryAfter.RetryDelayMs     - $telemetryBefore.RetryDelayMs
            BatchItemThrottles = $telemetryAfter.BatchItemThrottles - $telemetryBefore.BatchItemThrottles
        }
    }

    [pscustomobject]@{
        Name          = $Name
        ElapsedMs     = $sw.ElapsedMilliseconds
        PeakWorkingSetMB   = [math]::Round($sampler.Peak / 1MB, 1)
        WorkingSetDeltaMB  = [math]::Round(($sampler.Peak - $wsBefore) / 1MB, 1)
        ManagedHeapDeltaMB = [math]::Round(($heapAfter - $heapBefore) / 1MB, 1)
        MgxTelemetry  = $tel
        Output        = $scriptOutput
        Timestamp     = (Get-Date).ToString('o')
    }
}

# Runs Measure-BenchPass $Runs times and returns per-run results plus the median-by-time run.
function Measure-BenchMedian {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Script,
        [int] $Runs = 3
    )
    $passes = for ($i = 1; $i -le $Runs; $i++) {
        Write-Host ("  {0}: run {1}/{2}..." -f $Name, $i, $Runs)
        Measure-BenchPass -Name ("{0} (run {1})" -f $Name, $i) -Script $Script
    }
    $sorted = $passes | Sort-Object ElapsedMs
    [pscustomobject]@{
        Name   = $Name
        Median = $sorted[[math]::Floor(($sorted.Count - 1) / 2)]
        Runs   = $passes
    }
}

# Runs a benchmark contender in a child pwsh under a stall watchdog. The child writes
# its Measure-BenchPass result as JSON to $ResultFile and touches $HeartbeatFile as it
# progresses. If the heartbeat goes stale (dead-socket hang: observed twice with bare
# SDK cmdlets - no default timeout), the child is killed and the hang itself becomes
# the recorded outcome.
function Invoke-WatchdoggedContender {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $ScriptPath,
        [string[]] $ArgumentList = @(),
        [Parameter(Mandatory)] [string] $ResultFile,
        [Parameter(Mandatory)] [string] $HeartbeatFile,
        [int] $StallSeconds = 300
    )
    Remove-Item $ResultFile, $HeartbeatFile -ErrorAction SilentlyContinue
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process pwsh -PassThru -NoNewWindow `
        -ArgumentList (@('-NoProfile', '-File', $ScriptPath) + $ArgumentList)
    while (-not $p.HasExited) {
        Start-Sleep -Seconds 10
        $lastBeat = if (Test-Path $HeartbeatFile) { (Get-Item $HeartbeatFile).LastWriteTime } else { $p.StartTime }
        if (((Get-Date) - $lastBeat).TotalSeconds -gt $StallSeconds) {
            $p.Kill()
            Write-Host ("  WATCHDOG: '{0}' no progress for {1}s - killed after {2:F0}s total" -f $Name, $StallSeconds, $sw.Elapsed.TotalSeconds)
            return [pscustomobject]@{
                Name = $Name; Hung = $true; ElapsedMs = [long]$sw.ElapsedMilliseconds
                Output = [pscustomobject]@{ note = "HUNG - no progress for ${StallSeconds}s, killed by watchdog" }
            }
        }
    }
    if (Test-Path $ResultFile) {
        $r = Get-Content $ResultFile -Raw | ConvertFrom-Json
        $r | Add-Member -NotePropertyName Hung -NotePropertyValue $false -Force
        return $r
    }
    [pscustomobject]@{
        Name = $Name; Hung = $true; ElapsedMs = [long]$sw.ElapsedMilliseconds
        Output = [pscustomobject]@{ note = "child exited $($p.ExitCode) without writing a result" }
    }
}

# Appends a result object to results/<benchmark>.json (one JSON doc per file, array of entries).
function Write-BenchResult {
    param(
        [Parameter(Mandatory)] [string] $Benchmark,
        [Parameter(Mandatory)] [object] $Result
    )
    $dir = Join-Path $PSScriptRoot 'results'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $file = Join-Path $dir "$Benchmark.json"
    $entries = @()
    if (Test-Path $file) { $entries = @(Get-Content $file -Raw | ConvertFrom-Json) }
    # Resource units are the currency Graph actually throttles directory workloads in, so a
    # benchmark that records only wall time describes half the cost. Captured from session
    # telemetry, which accumulates x-ms-resource-unit across every response.
    $telemetry = $null
    try {
        $t = Get-MgxTelemetry -ErrorAction Stop
        $telemetry = [pscustomobject]@{
            ResourceUnits    = $t.ResourceUnitsConsumed
            TotalRequests    = $t.TotalRequests
            Succeeded        = $t.Succeeded
            Failed           = $t.Failed
            ThrottleRetries  = $t.ThrottleRetries
            OtherRetries     = $t.OtherRetries
            PacingWaitMs     = $t.AdaptivePacingWaitMs
            PacingActivations= $t.AdaptivePacingActivations
            RateLimiterWaitMs= $t.RateLimiterWaitMs
            RuPerRequest     = $(if ($t.TotalRequests -gt 0) {
                                    [math]::Round($t.ResourceUnitsConsumed / $t.TotalRequests, 2)
                                } else { 0 })
            # Per-workload state, parsed from the pacer's own description. RU itself is a single
            # tenant-wide counter, but the buckets tell you WHICH workload was being paced when
            # the units were spent - a directory fan-out and a drive pull draw on limits that are
            # documented and measured as independent, so a total alone hides which one is near
            # its ceiling.
            PacingState      = $t.PacingState
            PacingBuckets    = $(
                                    if ($t.PacingState) {
                                        @($t.PacingState -split ';' | Where-Object { $_ } |
                                          ForEach-Object {
                                              $name, $rest = $_ -split ':', 2
                                              [pscustomobject]@{
                                                  Workload = $name.Trim()
                                                  State    = if ($rest) { $rest.Trim() } else { '' }
                                              }
                                          })
                                    } else { @() }
                                )
            # -1 means Graph never sent x-ms-throttle-limit-percentage. Measured live it never
            # arrives, even during active 429s, so recording it per run is how we would notice
            # if that ever changed.
            LastThrottlePct  = $t.LastThrottlePercentage
            RuPerSecond      = $(if ($t.ElapsedMs -gt 0) {
                                    [math]::Round($t.ResourceUnitsConsumed / ($t.ElapsedMs / 1000), 1)
                                } else { 0 })
            # The documented budget is 8,000 RU per 10s per application+tenant pair for tenants
            # above 500 users, i.e. 800 RU/s. Recorded as a ratio so a run that approaches the
            # ceiling is obvious without re-deriving the arithmetic each time.
            BudgetFraction   = $(if ($t.ElapsedMs -gt 0) {
                                    [math]::Round(($t.ResourceUnitsConsumed / ($t.ElapsedMs / 1000)) / 800, 3)
                                } else { 0 })
        }
    }
    catch {
        # A benchmark that does not load Mgx (the bare-SDK comparison arms) has no telemetry.
        # That is expected; record its absence rather than failing the run.
        $telemetry = $null
    }

    $meta = [pscustomobject]@{
        Result     = $Result
        Telemetry  = $telemetry
        MgxVersion = (Get-Module Mgx -ErrorAction SilentlyContinue)?.Version?.ToString()
        SdkVersion = (Get-Module Microsoft.Graph.Authentication -ErrorAction SilentlyContinue)?.Version?.ToString()
        PSVersion  = $PSVersionTable.PSVersion.ToString()
        RecordedAt = (Get-Date).ToString('o')
    }
    # -InputObject (not pipeline) so a single-element array still serializes as a JSON array
    $all = @($entries) + @($meta)
    ConvertTo-Json -InputObject $all -Depth 12 | Set-Content -Path $file
    Write-Host "  result appended to $file"
}
