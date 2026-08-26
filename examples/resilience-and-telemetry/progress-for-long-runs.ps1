# A live status line for a long run: how far along, how fast, what it costs.
#
# Streaming is what makes this possible at all. Fan-out results flow through
# the pipeline while the requests are still running, so a ForEach-Object in
# the middle can tally them and draw progress - percent, rate, ETA, and the
# resource units the run has consumed so far. A cmdlet that buffers until
# everything is done has nothing to hang a progress bar on.
#
# The demonstration workload is the transitive-members fan-out from
# group-membership-report.ps1 - the kind of run that takes long enough on a
# real tenant to make you wonder whether it is alive.
#
# Requirements: Connect-MgGraph -Scopes "Group.Read.All", "User.Read.All"

param(
    # Write-Progress is not free; redrawing every item can dominate a fast
    # stream. Every 100th is smooth and costs nothing measurable.
    [int]$UpdateEvery = 100
)

Import-Module Mgx

$groups = @(Invoke-MgxRequest /groups -All -Property id,displayName)
Write-Host "$($groups.Count) groups to enumerate"

$sw      = [System.Diagnostics.Stopwatch]::StartNew()
$started = @{}    # groups whose first member has arrived
$counts  = @{}
$members = 0

$groups | Invoke-MgxRequest '/groups/{id}/transitiveMembers/microsoft.graph.user?$select=id' `
        -All -SkipNotFound -SkipForbidden |
    ForEach-Object {
        $members++
        $counts[$_._MgxSourceId] = ($counts[$_._MgxSourceId] ?? 0) + 1
        $started[$_._MgxSourceId] = $true

        if ($members % $UpdateEvery -eq 0) {
            # "started", not "finished": a group joins the tally when its first
            # member arrives. With the default concurrency of 5, at most 5
            # groups are in flight, so the two differ by at most 5.
            $done = $started.Count / $groups.Count
            $t = Get-MgxTelemetry
            Write-Progress -Activity 'Transitive members, all groups' `
                -Status ('{0} of {1} groups | {2:N0} members | {3:N0}/s | {4} requests, {5:N0} RU' -f `
                    $started.Count, $groups.Count, $members,
                    ($members / $sw.Elapsed.TotalSeconds), $t.Requests, $t.ResourceUnitsConsumed) `
                -PercentComplete (100 * $done) `
                -SecondsRemaining ([int]($sw.Elapsed.TotalSeconds * (1 - $done) / [Math]::Max($done, 0.01)))
        }
    }

Write-Progress -Activity 'Transitive members, all groups' -Completed
Write-Host ('{0:N0} members across {1} groups in {2:mm\:ss}' -f $members, $groups.Count, $sw.Elapsed)

Write-Host "`nLargest groups:"
$counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 5 |
    ForEach-Object {
        $g = $groups | Where-Object id -eq $_.Key
        '{0,8:N0}  {1}' -f $_.Value, $g.displayName
    }

# Honest limits of the numbers on the bar:
#   * Empty groups never emit a member, so they never join the "started" count -
#     the percentage can top out just short of 100 before the bar clears.
#   * The ETA assumes groups are similar in size. They are not; one giant group
#     at the end makes it optimistic. Treat it as a trend, not a promise.

<#
Expected output (final state; the bar redraws in place while running):

12 groups to enumerate
1,178 members across 12 groups in 00:41

Largest groups:
     412  Engineering All Hands
     306  All Employees
     144  Retail
      98  U.S. Sales
      67  sg-IT-Helpdesk
#>
