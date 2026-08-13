# Minimal mock Graph endpoint for the fault-injection gauntlet (06-fault-gauntlet.ps1).
# Serves GET /v1.0/users/{id} with deterministic transient faults keyed on the entity id,
# so every contender faces the identical fault schedule regardless of request order:
#   id % 100  0-14  -> first attempt gets 429 (Retry-After: 1), then success
#   id % 100 15-17  -> first two attempts get 503 (Retry-After: 1), then success
#   otherwise       -> success
# Control endpoints: GET /ping (liveness), GET /reset (clear attempt state between
# contenders), GET /stats (server-side truth: requests served, faults injected).
param(
    [int] $Port = 8787
)

$ErrorActionPreference = 'Stop'
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "mock graph listening on http://localhost:$Port/"

$attempts = @{}
$stats = @{ served = 0; faulted429 = 0; faulted503 = 0 }

function Send-Json($ctx, [int]$status, [string]$json, [hashtable]$headers) {
    $ctx.Response.StatusCode = $status
    if ($headers) { foreach ($k in $headers.Keys) { $ctx.Response.AddHeader($k, $headers[$k]) } }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $ctx.Response.ContentType = 'application/json'
    $ctx.Response.ContentLength64 = $bytes.Length
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
}

while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $path = $ctx.Request.Url.AbsolutePath

    try {
        switch -Regex ($path) {
            '^/ping$'  { Send-Json $ctx 200 '{"ok":true}' $null; continue }
            '^/reset$' { $attempts.Clear(); Send-Json $ctx 200 '{"reset":true}' $null; continue }
            '^/stats$' {
                Send-Json $ctx 200 (@{ served = $stats.served; faulted429 = $stats.faulted429; faulted503 = $stats.faulted503 } | ConvertTo-Json -Compress) $null
                continue
            }
            '^/(v1\.0|beta)/users/(?<id>[^/?]+)$' {
                $stats.served++
                $id = [int]($Matches['id'] -replace '\D', '0')
                if (-not $attempts.ContainsKey($id)) { $attempts[$id] = 0 }
                $attempts[$id]++
                $n = $attempts[$id]
                $bucket = $id % 100

                if ($bucket -lt 15 -and $n -eq 1) {
                    $stats.faulted429++
                    Send-Json $ctx 429 '{"error":{"code":"TooManyRequests","message":"mock throttle"}}' @{ 'Retry-After' = '1' }
                }
                elseif ($bucket -ge 15 -and $bucket -lt 18 -and $n -le 2) {
                    $stats.faulted503++
                    Send-Json $ctx 503 '{"error":{"code":"ServiceUnavailable","message":"mock outage"}}' @{ 'Retry-After' = '1' }
                }
                else {
                    $user = @{
                        id                = "00000000-0000-0000-0000-{0:D12}" -f $id
                        displayName       = "Mock User $id"
                        userPrincipalName = "mock.u$id@mock.local"
                        mail              = "mock.u$id@mock.local"
                        department        = 'Mockestration'
                        jobTitle          = 'Test Subject'
                    } | ConvertTo-Json -Compress
                    Send-Json $ctx 200 $user $null
                }
                continue
            }
            default {
                # Anything else (org probe, list endpoints): benign empty page
                Send-Json $ctx 200 '{"value":[]}' $null
            }
        }
    }
    catch {
        try { $ctx.Response.Close() } catch { }
    }
}
