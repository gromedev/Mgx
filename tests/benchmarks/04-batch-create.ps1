# Benchmark 04: create N users - the enterprise-scale write path.
# Contenders:
#   mgx  - Invoke-MgxBatchRequest ($batch, 20 per HTTP call, paced writes)
#   sdk  - New-MgUser loop (single run, watchdogged child - can take 40+ min)
#   rest - Invoke-RestMethod POST loop (single run, watchdogged child)
# Each contender creates a disjoint UPN range (bench04a/b/c); cleanup batch-DELETEs
# everything it created and is itself timed (a free bulk-delete data point).
# Baselines run in watchdogged children: bare SDK cmdlets have hung on dead sockets.
# Also: the update sub-benchmark - PATCH 1000 users via $batch vs concurrent fan-out.
param(
    [int] $CreateCount = 10000,
    [int] $UpdateCount = 1000,
    [switch] $SkipBaselines,
    # child-mode plumbing (internal)
    [ValidateSet('', 'sdkcreate', 'restcreate')] [string] $Contender = '',
    [string] $ResultFile,
    [string] $HeartbeatFile
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Connect-MgxBenchmark

$domain = 'gromedev01.onmicrosoft.com'

function New-Bench04Body([string]$prefix, [int]$i) {
    $upn = '{0}{1:D6}' -f $prefix, $i
    @{
        accountEnabled    = $true
        displayName       = "Bench04 User $i"
        userPrincipalName = "$upn@$domain"
        mailNickname      = $upn
        passwordProfile   = @{ password = [guid]::NewGuid().ToString() + 'aB1!'; forceChangePasswordNextSignIn = $true }
    }
}

# ---------------- child mode: one baseline creator ----------------
if ($Contender) {
    function Beat { Set-Content -Path $HeartbeatFile -Value ([datetime]::UtcNow.Ticks) }
    Beat
    $pass = switch ($Contender) {
        'sdkcreate' {
            Import-Module Microsoft.Graph.Users
            Measure-BenchPass -Name "New-MgUser loop $CreateCount (single run)" -Script {
                $ok = 0; $failed = 0
                foreach ($i in 1..$CreateCount) {
                    $b = New-Bench04Body 'bench04b.' $i
                    $u = New-MgUser -AccountEnabled -DisplayName $b.displayName -UserPrincipalName $b.userPrincipalName `
                        -MailNickname $b.mailNickname -PasswordProfile $b.passwordProfile -ErrorAction SilentlyContinue
                    if ($u) { $ok++ } else { $failed++ }
                    if ($i % 250 -eq 0) { Beat; Write-Host ("    sdk create progress: {0}/{1}" -f $i, $CreateCount) }
                }
                @{ ok = $ok; failed = $failed }
            }
        }
        'restcreate' {
            Measure-BenchPass -Name "REST POST loop $CreateCount (single run)" -Script {
                $tok = Get-BenchAppToken
                $headers = @{ Authorization = "Bearer $tok"; 'Content-Type' = 'application/json' }
                $ok = 0; $failed = 0
                foreach ($i in 1..$CreateCount) {
                    try {
                        $null = Invoke-RestMethod -Method POST -Uri 'https://graph.microsoft.com/v1.0/users' `
                            -Headers $headers -Body ((New-Bench04Body 'bench04c.' $i) | ConvertTo-Json) -TimeoutSec 120
                        $ok++
                    } catch { $failed++ }
                    if ($i % 250 -eq 0) { Beat; Write-Host ("    rest create progress: {0}/{1}" -f $i, $CreateCount) }
                    if ($i % 500 -eq 0) { $tok = Get-BenchAppToken; $headers.Authorization = "Bearer $tok" }
                }
                @{ ok = $ok; failed = $failed }
            }
        }
    }
    $pass | ConvertTo-Json -Depth 8 | Set-Content $ResultFile
    exit 0
}

# ---------------- parent mode ----------------
Import-Module Microsoft.Graph.Users
$results = [ordered]@{}
$tmp = [System.IO.Path]::GetTempPath()

function Get-CreatedIds([string]$prefix) {
    $ids = [System.Collections.Generic.List[string]]::new()
    Invoke-MgxRequest /users -All -Filter "startsWith(userPrincipalName,'$prefix')" -Property id |
        ForEach-Object { $ids.Add($_.id) }
    , $ids
}

function Remove-Created([string]$prefix, [string]$label) {
    $ids = Get-CreatedIds $prefix
    if ($ids.Count -eq 0) { return $null }
    Write-Host ("  cleanup: deleting {0} {1} users..." -f $ids.Count, $label)
    Measure-BenchPass -Name "cleanup $label (batch DELETE)" -Script {
        $urls = $ids | ForEach-Object { "/users/$_" }
        $r = @($urls | Invoke-MgxBatchRequest -Method DELETE -ErrorAction SilentlyContinue -WarningAction SilentlyContinue)
        @{ deleted = @($r | Where-Object Status -lt 400).Count; failed = @($r | Where-Object Status -ge 400).Count }
    }
}

# --- Create: Mgx $batch ---
$results.mgxCreate = Measure-BenchPass -Name "Mgx batch create $CreateCount" -Script {
    $items = foreach ($i in 1..$CreateCount) {
        [pscustomobject]@{ Url = '/users'; Method = 'POST'; Body = (New-Bench04Body 'bench04a.' $i) }
    }
    $r = @($items | Invoke-MgxBatchRequest -ErrorAction SilentlyContinue -WarningAction SilentlyContinue)
    @{ ok = @($r | Where-Object Status -lt 400).Count; failed = @($r | Where-Object Status -ge 400).Count }
}
$results.mgxCleanup = Remove-Created 'bench04a.' 'mgx'

if (-not $SkipBaselines) {
    foreach ($c in 'sdkcreate', 'restcreate') {
        $results[$c] = Invoke-WatchdoggedContender -Name $c -ScriptPath $PSCommandPath `
            -ArgumentList @('-Contender', $c, '-CreateCount', $CreateCount, '-ResultFile', (Join-Path $tmp "bench04-$c.json"), '-HeartbeatFile', (Join-Path $tmp "bench04-$c.beat")) `
            -ResultFile (Join-Path $tmp "bench04-$c.json") -HeartbeatFile (Join-Path $tmp "bench04-$c.beat") `
            -StallSeconds 300
        $prefix = if ($c -eq 'sdkcreate') { 'bench04b.' } else { 'bench04c.' }
        $label  = if ($c -eq 'sdkcreate') { 'sdk' } else { 'rest' }
        $results["${label}Cleanup"] = Remove-Created $prefix $label
    }
}

# --- Update sub-benchmark: batch PATCH vs fan-out PATCH on seeded bench users ---
Write-Host "collecting $UpdateCount seeded user ids for the update comparison..."
$updIds = [System.Collections.Generic.List[string]]::new()
Invoke-MgxRequest /users -All -Filter "startsWith(userPrincipalName,'bench.u')" -Property id |
    Select-Object -First $UpdateCount | ForEach-Object { $updIds.Add($_.id) }

$results.batchPatch = Measure-BenchPass -Name "batch PATCH $($updIds.Count)" -Script {
    $urls = $updIds | ForEach-Object { "/users/$_" }
    $r = @($urls | Invoke-MgxBatchRequest -Method PATCH -Body @{ officeLocation = 'Bench04-Batch' } -ErrorAction SilentlyContinue)
    @{ ok = @($r | Where-Object Status -lt 400).Count }
}
$results.fanoutPatch = Measure-BenchPass -Name "fan-out PATCH $($updIds.Count)" -Script {
    $errs = $null
    $null = $updIds | Invoke-MgxRequest '/users/{id}' -Method PATCH -Body @{ officeLocation = 'Bench04-Fanout' } `
        -ErrorVariable errs -ErrorAction SilentlyContinue
    @{ failed = @($errs).Count }
}

Write-Host ''
Write-Host ("=== BATCH CREATE (N={0}) ===" -f $CreateCount)
foreach ($k in $results.Keys) {
    $r = $results[$k]
    if ($null -eq $r) { continue }
    $suffix = if ($r.PSObject.Properties['Hung'] -and $r.Hung) { 'HUNG (killed by watchdog)' }
              else { ($r.Output | ConvertTo-Json -Compress) }
    Write-Host ("{0,-40} {1,9:F1}s  {2}" -f $r.Name, ($r.ElapsedMs / 1000), $suffix)
}
Write-BenchResult -Benchmark '04-batch-create' -Result $results
