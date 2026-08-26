# Shared plumbing for the Mgx benchmark suite. Dot-source from each benchmark script:
#   . "$PSScriptRoot/common.ps1"
# Auth resolution order: existing Graph session, then AZURE_* certificate variables, then
# AZURE_* secret variables, then $env:MGX_BENCH_APP / ~/.mgx-bench/app.json. With both a
# certificate path and a secret set, the certificate wins - and a certificate path that
# does not exist throws rather than falling through, so a stale path masks the secret.

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

    # Certificate first. Each benchmark runs in a fresh pwsh process, so there is never a
    # session to inherit, and the client-secret file below is the only other path - which is
    # why this suite could not run at all once that file went away. The same three AZURE_*
    # variables everything else in this repo uses, and no secret at rest.
    if ($env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $env:AZURE_CLIENT_CERTIFICATE_PATH) {
        $pfx = $env:AZURE_CLIENT_CERTIFICATE_PATH
        if (-not (Test-Path $pfx)) { throw "AZURE_CLIENT_CERTIFICATE_PATH points at '$pfx', which does not exist." }
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $pfx, $env:AZURE_CLIENT_CERTIFICATE_PASSWORD)
        Connect-MgGraph -TenantId $env:AZURE_TENANT_ID -ClientId $env:AZURE_CLIENT_ID `
            -Certificate $cert -NoWelcome
        Write-Host "Connected app-only by certificate ($($env:AZURE_CLIENT_ID))"
        return
    }

    # A secret in the environment, for an app that has no certificate uploaded. Same three
    # variables minus the certificate, and like the certificate path it leaves nothing at rest.
    if ($env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $env:AZURE_CLIENT_SECRET) {
        $cred = [pscredential]::new($env:AZURE_CLIENT_ID,
            (ConvertTo-SecureString $env:AZURE_CLIENT_SECRET -AsPlainText -Force))
        Connect-MgGraph -TenantId $env:AZURE_TENANT_ID -ClientSecretCredential $cred -NoWelcome
        Write-Host "Connected app-only by secret ($($env:AZURE_CLIENT_ID))"
        return
    }

    $credPath = if ($env:MGX_BENCH_APP) { $env:MGX_BENCH_APP }
                else { Join-Path $HOME '.mgx-bench/app.json' }
    if (-not (Test-Path $credPath)) {
        throw "No Graph session, no AZURE_* certificate or secret variables, and no app credentials at '$credPath'."
    }
    $cfg  = Get-Content $credPath -Raw | ConvertFrom-Json
    $cred = [pscredential]::new($cfg.appId, (ConvertTo-SecureString $cfg.clientSecret -AsPlainText -Force))
    Connect-MgGraph -TenantId $cfg.tenantId -ClientSecretCredential $cred -NoWelcome
    Write-Host "Connected app-only ($($cfg.appId))"
}

# Mints a raw app-only bearer token for the Invoke-RestMethod baselines,
# which deliberately bypass every SDK/Mgx layer.
function Get-BenchAppToken {
    # The raw-REST contender needs a bearer token of its own. Under certificate auth there is
    # no secret to POST, so mint one with a signed client assertion (private_key_jwt) using the
    # same certificate the SDK session uses.
    if ($env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $env:AZURE_CLIENT_CERTIFICATE_PATH) {
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $env:AZURE_CLIENT_CERTIFICATE_PATH, $env:AZURE_CLIENT_CERTIFICATE_PASSWORD)
        $aud  = "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/oauth2/v2.0/token"
        $now  = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

        # x5t is the SHA-1 thumbprint, base64url - Entra rejects the assertion without it.
        $b64u = { param($bytes) [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_') }
        $hdr  = @{ alg = 'RS256'; typ = 'JWT'; x5t = (& $b64u $cert.GetCertHash()) } | ConvertTo-Json -Compress
        $pay  = @{ aud = $aud; iss = $env:AZURE_CLIENT_ID; sub = $env:AZURE_CLIENT_ID
                   jti = [guid]::NewGuid().ToString(); nbf = $now; exp = $now + 600 } | ConvertTo-Json -Compress

        $unsigned = "$(& $b64u ([Text.Encoding]::UTF8.GetBytes($hdr))).$(& $b64u ([Text.Encoding]::UTF8.GetBytes($pay)))"
        # RSACertificateExtensions.GetRSAPrivateKey is an EXTENSION method; PowerShell cannot
        # invoke it as $cert.GetRSAPrivateKey(), so call the static form explicitly.
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
        if (-not $rsa) { throw "Certificate '$($env:AZURE_CLIENT_CERTIFICATE_PATH)' has no usable RSA private key." }
        $sig = $rsa.SignData(
            [Text.Encoding]::UTF8.GetBytes($unsigned),
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $assertion = "$unsigned.$(& $b64u $sig)"

        foreach ($attempt in 1..3) {
            try {
                $resp = Invoke-RestMethod -Method POST -Uri $aud -TimeoutSec 30 -Body @{
                    grant_type            = 'client_credentials'
                    client_id             = $env:AZURE_CLIENT_ID
                    client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
                    client_assertion      = $assertion
                    scope                 = 'https://graph.microsoft.com/.default'
                }
                return $resp.access_token
            }
            catch { if ($attempt -eq 3) { throw }; Start-Sleep -Seconds 5 }
        }
    }

    if ($env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $env:AZURE_CLIENT_SECRET) {
        foreach ($attempt in 1..3) {
            try {
                $resp = Invoke-RestMethod -Method POST -TimeoutSec 30 `
                    -Uri "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/oauth2/v2.0/token" `
                    -Body @{ grant_type = 'client_credentials'; client_id = $env:AZURE_CLIENT_ID
                             client_secret = $env:AZURE_CLIENT_SECRET
                             scope = 'https://graph.microsoft.com/.default' }
                return $resp.access_token
            }
            catch { if ($attempt -eq 3) { throw }; Start-Sleep -Seconds 5 }
        }
    }

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

function Get-BenchUserIds {
    <#
    .SYNOPSIS
        Ids to run a benchmark against, preferring the seeded 'bench.u' cohort.

    .DESCRIPTION
        Five benchmarks (02, 03, 04, 07, 08) filter on startsWith(userPrincipalName,'bench.u'),
        and nothing in this repo creates those users - so on any tenant that was not hand-seeded
        the suite died with "seed the tenant first" and no instructions. That is most of the
        reason the benchmark results in results/ went stale: the suite simply would not run.

        Seeded users are still preferred, because they are disposable and a write benchmark can
        safely PATCH them. When there are not enough, fall back to ordinary users and say so
        loudly - a number measured against a different cohort is still a number, it just must not
        be compared against a seeded run. Callers that WRITE must pass -RequireSeeded.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [int] $Count,
        [string] $Prefix = 'bench.u',
        [switch] $RequireSeeded
    )

    $ids = [System.Collections.Generic.List[string]]::new()
    Invoke-MgxRequest /users -All -Filter "startsWith(userPrincipalName,'$Prefix')" -Property id -WarningAction SilentlyContinue |
        Select-Object -First $Count | ForEach-Object { $ids.Add($_.id) }

    if ($ids.Count -ge $Count) {
        Write-Host "  using $($ids.Count) seeded '$Prefix' users"
        return , $ids
    }

    if ($RequireSeeded) {
        throw ("This benchmark WRITES to the users it touches, so it will only run against the " +
               "disposable '$Prefix' cohort. Found $($ids.Count) of $Count. Seed the tenant, or " +
               "run a read-only benchmark instead.")
    }

    Write-Warning ("Only $($ids.Count) '$Prefix' users; falling back to ordinary tenant users. " +
                   "Read-only, but do NOT compare this run against a seeded one.")
    $ids.Clear()
    Invoke-MgxRequest /users -All -Property id -WarningAction SilentlyContinue |
        Select-Object -First $Count | ForEach-Object { $ids.Add($_.id) }

    if ($ids.Count -eq 0) { throw "No users at all in this tenant - nothing to benchmark." }
    if ($ids.Count -lt $Count) {
        Write-Warning "Tenant has only $($ids.Count) users; running at that size instead of $Count."
    }
    return , $ids
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
        [Parameter(Mandatory)] [object] $Result,
        # The run's wall clock. Only needed for the RU rate below; picked up from $Result.WallMs
        # when the caller already records one.
        [long] $WallMs = 0
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
    $wall = if ($WallMs -gt 0) { $WallMs }
            elseif ($Result.WallMs -and [long]$Result.WallMs -gt 0) { [long]$Result.WallMs }
            else { 0 }
    try {
        $t = Get-MgxTelemetry -ErrorAction Stop
        $telemetry = [pscustomobject]@{
            ResourceUnits    = $t.ResourceUnitsConsumed
            TotalRequests    = $t.Requests
            Succeeded        = $t.Succeeded
            Failed           = $t.Failed
            ThrottleRetries  = $t.ThrottleRetries
            OtherRetries     = $t.OtherRetries
            PacingWaitMs     = $t.AdaptivePacingWaitMs
            PacingActivations= $t.AdaptivePacingActivations
            RateLimiterWaitMs= $t.RateLimiterWaitMs
            RuPerRequest     = $(if ($t.Requests -gt 0) {
                                    [math]::Round($t.ResourceUnitsConsumed / $t.Requests, 2)
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
            # A rate is only meaningful against the run's WALL clock. Telemetry's TotalElapsedMs
            # is the SUM of per-request durations, so a concurrent run exceeds its own wall time
            # by roughly the concurrency factor - dividing by it yields RU per request-second and
            # understates the budget draw by that factor (a concurrency-128 run measured at 182
            # RU/s reported 1.6). Callers that know their wall clock pass it; the rest record no
            # rate at all, because a missing number is auditable and a wrong one is not.
            WallMs           = $(if ($wall -gt 0) { $wall } else { $null })
            RuPerSecond      = $(if ($wall -gt 0) {
                                    [math]::Round($t.ResourceUnitsConsumed / ($wall / 1000), 1)
                                } else { $null })
            # The documented budget is 8,000 RU per 10s per application+tenant pair for tenants
            # above 500 users, i.e. 800 RU/s. Recorded as a ratio so a run that approaches the
            # ceiling is obvious without re-deriving the arithmetic each time.
            BudgetFraction   = $(if ($wall -gt 0) {
                                    [math]::Round(($t.ResourceUnitsConsumed / ($wall / 1000)) / 800, 3)
                                } else { $null })
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
