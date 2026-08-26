<#
    The one thing the rest of the suite cannot reach: what Enable-MgxResilience does when Graph
    is actually refusing requests.

    It matters because the failure is silent. The Graph SDK's own retry handler sits inside the
    wrap and answers 429 itself, so a throttled session looks identical to a clean one - the
    pacer never slows down, and telemetry reports zero retries while the SDK sleeps out of sight
    inside the call. Mocks cannot catch that: a mock 429 goes wherever the test author points it.

    This needs a second machine. One client cannot drive a tenant's budget from a single NAT'd
    connection - measured 587 req/s from a laptop against 4,641 from a VM on the same network,
    same test, same minute - and below roughly 700 req/s the tenant never refuses anything, so
    the run proves nothing. Point MGX_LIVE_THROTTLE_SSH at a host with real egress.

    Run:  MGX_LIVE_THROTTLE_SSH='user@host' Invoke-Pester -Path ./tests/Live/Mgx.Throttle.Tests.ps1

    NOT read-only in effect: it deliberately exhausts the tenant's request budget and leaves it
    refusing requests for several minutes afterwards. Run it last, or on its own. It is skipped
    unless MGX_LIVE_THROTTLE_SSH is set, because most people running this suite do not have a
    second machine to hand.
#>

BeforeAll {
    $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    if (-not $env:AZURE_TENANT_ID -or -not $env:AZURE_CLIENT_ID -or
        (-not $env:AZURE_CLIENT_CERTIFICATE_PATH -and -not $env:AZURE_CLIENT_SECRET)) {
        throw "Live tests need AZURE_TENANT_ID, AZURE_CLIENT_ID, and either " +
              "AZURE_CLIENT_CERTIFICATE_PATH or AZURE_CLIENT_SECRET."
    }
    $script:Configured = [bool]$env:MGX_LIVE_THROTTLE_SSH
    . "$repo/tests/benchmarks/common.ps1"
    Import-MgxLocal
    Connect-MgxBenchmark | Out-Null

    if ($script:Configured) {
        $script:Target = $env:MGX_LIVE_THROTTLE_SSH
        $script:SshArgs = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8')
        if ($env:MGX_LIVE_THROTTLE_KEY) { $script:SshArgs = @('-i', $env:MGX_LIVE_THROTTLE_KEY) + $script:SshArgs }
        $script:Remote = '/tmp/mgx-throttle-drain.py'

        # Reads the token on stdin. Never as an argument - argv is world-readable in ps - and
        # never written to disk on the remote host. The certificate itself never leaves here:
        # the assertion is signed locally and only the resulting bearer token crosses.
        $drainer = @'
import http.client, ssl, sys, threading, time
token = sys.stdin.readline().strip()
workers, seconds = int(sys.argv[1]), int(sys.argv[2])
ctx = ssl.create_default_context(); hdrs = {"Authorization": "Bearer " + token}
stop = time.time() + seconds
def run():
    c = None
    while time.time() < stop:
        try:
            if c is None:
                c = http.client.HTTPSConnection("graph.microsoft.com", 443, context=ctx, timeout=20)
            c.request("GET", "/v1.0/users?$top=1&$select=id", headers=hdrs)
            r = c.getresponse(); r.read()
        except Exception:
            try: c.close()
            except Exception: pass
            c = None
ts = [threading.Thread(target=run, daemon=True) for _ in range(workers)]
for t in ts: t.start()
for t in ts: t.join()
'@
        $drainer | & ssh @script:SshArgs $script:Target "cat > $script:Remote"
        if ($LASTEXITCODE -ne 0) { throw "Could not stage the load generator on $script:Target." }

        $script:Token = Get-BenchAppToken
        $script:Drain = Start-Job -ScriptBlock {
            param($sshArgs, $target, $remote, $token)
            $token | & ssh @sshArgs $target "python3 $remote 200 300"
        } -ArgumentList $script:SshArgs, $script:Target, $script:Remote, $script:Token

        # Confirm the tenant is refusing requests before asserting anything about the refusal.
        # Without this the test can only distinguish "the bridge saw no throttle" from "there
        # was no throttle to see" by guessing, and a green result would mean nothing.
        $probe = 'https://graph.microsoft.com/v1.0/users?$top=1&$select=id'
        $deadline = (Get-Date).AddSeconds(120)
        $script:ThrottleConfirmedAt = $null
        while ((Get-Date) -lt $deadline -and -not $script:ThrottleConfirmedAt) {
            $r = Invoke-WebRequest -Uri $probe -Headers @{ Authorization = "Bearer $script:Token" } `
                                   -SkipHttpErrorCheck -ErrorAction SilentlyContinue
            if ($r.StatusCode -eq 429) { $script:ThrottleConfirmedAt = Get-Date }
            else { Start-Sleep -Seconds 3 }
        }
    }
}

AfterAll {
    if ($script:Configured) {
        if ($script:Drain) { Stop-Job $script:Drain -ErrorAction SilentlyContinue; Remove-Job $script:Drain -Force -ErrorAction SilentlyContinue }
        # Stopping the job kills the local ssh client, not the process on the far side - that
        # keeps running to its own deadline, hammering the tenant long after the test reports
        # green. Kill it where it lives. The bracket makes the pattern not match the command
        # carrying it, which would otherwise kill this shell before the rm ran.
        & ssh @script:SshArgs $script:Target "rm -f $script:Remote; pkill -f '[m]gx-throttle-drain'" 2>$null
        Disable-MgxResilience -ErrorAction SilentlyContinue | Out-Null
    }
}

Describe 'Enable-MgxResilience' {
  Context 'under a real throttle' {

    It 'drove the tenant into refusing requests' -Skip:(-not $env:MGX_LIVE_THROTTLE_SSH) {
        # Fails rather than skips: the run was asked for, so not reaching a throttle is a
        # result about the setup, not a reason to report a pass.
        $script:ThrottleConfirmedAt | Should -Not -BeNullOrEmpty -Because `
            'the load generator never pushed the tenant to 429 - check the host has real egress, and that 200 workers is enough for it'
    }

    It 'sees the throttle the SDK would otherwise absorb' -Skip:(-not $env:MGX_LIVE_THROTTLE_SSH) {
        Enable-MgxResilience | Out-Null
        Get-MgxTelemetry -Reset | Out-Null
        foreach ($i in 1..12) { Get-MgUser -Top 1 -ErrorAction SilentlyContinue | Out-Null }
        $t = Get-MgxTelemetry

        # The whole point of the wrap. Zero here with the tenant refusing requests means the
        # SDK's own retry handler answered them first and Mgx never learned a thing.
        $t.ThrottleRetries | Should -BeGreaterThan 0 -Because `
            'the SDK retry handler inside the wrap must be disarmed, or a throttled session is indistinguishable from a clean one'
    }

    It 'lets the pacer learn from it' -Skip:(-not $env:MGX_LIVE_THROTTLE_SSH) {
        # Retries alone are not the win - slowing down is. A pacer still in slow-start after a
        # throttle is one that never saw it.
        (Get-MgxTelemetry).PacingState | Should -Match 'capped' -Because `
            'the pacer should cap its rate once it has met a 429, not stay in slow-start'
    }

    It 'still completes the caller''s requests' -Skip:(-not $env:MGX_LIVE_THROTTLE_SSH) {
        # Surfacing throttling must not mean surfacing failures: the retries exist so the
        # caller's call still returns.
        (Get-MgxTelemetry).Failed | Should -Be 0
    }
  }
}
