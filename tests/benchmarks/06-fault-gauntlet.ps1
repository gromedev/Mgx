# Benchmark 06: resilience under controlled fault injection. No tenant required.
#
# Spins up mock-graph-server.ps1 (deterministic 429/503 schedule keyed on entity id),
# then fetches the same N users with four contenders:
#   1. naive      - Invoke-RestMethod loop, no retry (what quick scripts actually do)
#   2. sdk        - Get-MgUser sequential (includes the SDK's own built-in retry handler)
#   3. sdk+mgx    - Get-MgUser sequential with Enable-MgxResilience injected
#   4. mgx        - ids | Invoke-MgxRequest '/users/{id}' (fan-out, full resilience stack)
# The server's /reset is called between contenders so each faces the identical schedule.
# Fault profile: 15% of ids throttle once (429, Retry-After 1s); 3% fail twice with 503.
param(
    [int] $N = 1000,
    [int] $Port = 8787
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Import-Module Microsoft.Graph.Users

# Strings, not integers. Invoke-MgxRequest resolves a piped id as either a bare string or
# an 'id' member; an int is neither, so every record raised MissingPipelineId, -ErrorAction
# SilentlyContinue swallowed all of them, and the fan-out contender died at EndProcessing
# claiming no pipeline input. Real Graph ids are strings, so this matches actual usage.
$ids = @(1..$N | ForEach-Object { "$_" })

# Find a free port (a crashed prior run can leave a zombie listener behind)
$Port = ($Port..($Port + 20)) | Where-Object {
    -not (Test-Connection -TargetName localhost -TcpPort $_ -TimeoutSeconds 1 -Quiet)
} | Select-Object -First 1
if (-not $Port) { throw 'no free port found in range' }
$base = "http://localhost:$Port"

# --- Start mock server as a child process ---
$server = Start-Process pwsh -PassThru `
    -ArgumentList '-NoProfile', '-File', (Join-Path $PSScriptRoot 'mock-graph-server.ps1'), '-Port', $Port
try {
    $up = $false
    foreach ($i in 1..50) {
        try { $null = Invoke-RestMethod "$base/ping" -TimeoutSec 1; $up = $true; break } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $up) { throw "mock server did not come up on port $Port" }
    Write-Host "mock server up (pid $($server.Id))"

    # --- Point the whole Graph stack at the mock ---
    if (-not (Get-MgEnvironment -Name MgxBench -ErrorAction SilentlyContinue)) {
        Add-MgEnvironment -Name MgxBench -GraphEndpoint $base -AzureADEndpoint 'https://login.microsoftonline.com' | Out-Null
    }
    Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null
    Connect-MgGraph -Environment MgxBench `
        -AccessToken (ConvertTo-SecureString 'mock-token-not-validated' -AsPlainText -Force) -NoWelcome
    # -AccessToken auth leaves AuthContext.TenantId empty, and Mgx's auth fingerprint
    # treats an empty TenantId as "not connected" (1.0.4 identity fix). Give it a value.
    [Microsoft.Graph.PowerShell.Authentication.GraphSession]::Instance.AuthContext.TenantId = 'gauntlet-mock-tenant'

    function Reset-Server { $null = Invoke-RestMethod "$base/reset" }
    function Get-ServerStats { Invoke-RestMethod "$base/stats" }
    function Show-Contender($r) {
        Write-Host ("{0,-28} completed {1,5} / failed {2,4}  wall {3,7:F1}s" -f `
            $r.Name, $r.Output.ok, $r.Output.failed, ($r.ElapsedMs / 1000))
    }

    $results = [ordered]@{}

    # --- 1. naive: no retry at all ---
    Reset-Server
    $results.naive = Measure-BenchPass -Name 'naive Invoke-RestMethod' -Script {
        $ok = 0; $failed = 0
        foreach ($id in $ids) {
            try { $null = Invoke-RestMethod "$base/v1.0/users/$id" -TimeoutSec 10; $ok++ }
            catch { $failed++ }
        }
        @{ ok = $ok; failed = $failed; serverStats = (Get-ServerStats) }
    }

    Show-Contender $results.naive

    # --- 2. bare SDK (its built-in retry handler included) ---
    Reset-Server
    $results.sdk = Measure-BenchPass -Name 'Get-MgUser sequential' -Script {
        $ok = 0; $failed = 0
        foreach ($id in $ids) {
            $u = Get-MgUser -UserId "$id" -ErrorAction SilentlyContinue
            if ($u) { $ok++ } else { $failed++ }
        }
        @{ ok = $ok; failed = $failed; serverStats = (Get-ServerStats) }
    }

    Show-Contender $results.sdk

    # --- 3. SDK + Enable-MgxResilience ---
    Reset-Server
    Enable-MgxResilience
    $results.sdkMgx = Measure-BenchPass -Name 'Get-MgUser + MgxResilience' -Script {
        $ok = 0; $failed = 0
        foreach ($id in $ids) {
            $u = Get-MgUser -UserId "$id" -ErrorAction SilentlyContinue
            if ($u) { $ok++ } else { $failed++ }
        }
        @{ ok = $ok; failed = $failed; serverStats = (Get-ServerStats) }
    }
    Disable-MgxResilience
    Show-Contender $results.sdkMgx

    # --- 4. Mgx fan-out ---
    Reset-Server
    $results.mgx = Measure-BenchPass -Name 'Invoke-MgxRequest fan-out' -Script {
        $items = @($ids | Invoke-MgxRequest '/users/{id}' -ErrorVariable mgxErrs -ErrorAction SilentlyContinue)
        @{ ok = $items.Count; failed = $mgxErrs.Count; serverStats = (Get-ServerStats) }
    }
    Show-Contender $results.mgx

    # --- Report ---
    Write-Host ''
    Write-Host ("=== FAULT GAUNTLET (N={0}; 15% single-429, 3% double-503) ===" -f $N)
    foreach ($k in $results.Keys) {
        $r = $results[$k]
        Write-Host ("{0,-28} completed {1,5} / failed {2,4}  wall {3,7:F1}s" -f `
            $r.Name, $r.Output.ok, $r.Output.failed, ($r.ElapsedMs / 1000))
    }
    Write-BenchResult -Benchmark '06-fault-gauntlet' -Result $results
}
finally {
    Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null
    if ($server -and -not $server.HasExited) { $server.Kill() }
}
