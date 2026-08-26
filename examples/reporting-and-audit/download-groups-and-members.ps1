# Download every group and ALL its members - nesting resolved - with a
# progress bar.
#
# Three JSONL files:
#   groups.jsonl          every group
#   members-direct.jsonl  every direct membership; rows whose @odata.type is a
#                         group are the nesting edges
#   members.jsonl         every membership with nesting resolved
#
# One fan-out, not two, and no transitiveMembers. Asking Graph to flatten the
# nesting per group re-transfers every deeply nested member once per ancestor
# group; the direct download already contains the whole graph, so the closure
# is computed locally instead - each membership crosses the wire exactly once,
# at 2 RU per request instead of 3.
#
# Requirements: Connect-MgGraph -Scopes "Group.Read.All", "User.Read.All"

param(
    [string]$OutputDirectory = './groups-export',
    # Fan-out width. The default of 5 leaves a big tenant latency-bound; at
    # ~2 RU per call, 16 concurrent stays far below the 800 RU/s budget floor
    # (resource-unit-budgeting.ps1 measures your tenant's own numbers).
    [int]$Concurrency = 16,
    [int]$UpdateEvery = 100
)

Import-Module Mgx

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$directFile   = Join-Path $OutputDirectory 'members-direct.jsonl'
$resolvedFile = Join-Path $OutputDirectory 'members.jsonl'

# groups.jsonl is a single enumeration - exactly what Export-MgxCollection is
# for: request loop, JSONL writing, and resume in one cmdlet. The fan-out
# below cannot use it, because Export-MgxCollection drives its own requests
# from a URI rather than accepting piped objects; its rows arrive through the
# pipeline, so those files are written by hand.
$groupsFile = Join-Path $OutputDirectory 'groups.jsonl'
$null = Export-MgxCollection /groups -All `
    -Property id,displayName,description,mail,groupTypes,securityEnabled `
    -OutputFile $groupsFile
$groups = @(Get-Content $groupsFile | ConvertFrom-Json)
Write-Host "$($groups.Count) groups -> groups.jsonl"

# --- pass 1: one fan-out, every direct membership straight to disk ---
$sw      = [System.Diagnostics.Stopwatch]::StartNew()
$started = @{}
$parents = @{}    # child group id -> list of parent group ids (built as rows stream by)
$rows    = 0
# One StreamWriter held open for the run - Add-Content would reopen the file
# once per row.
$writer  = [System.IO.StreamWriter]::new($directFile, $false, [System.Text.UTF8Encoding]::new($false))
try {
    $groups | Invoke-MgxRequest '/groups/{id}/members?$select=id,displayName' `
            -All -Concurrency $Concurrency -SkipNotFound -SkipForbidden |
        ForEach-Object {
            $_['groupId'] = $_['_MgxSourceId']
            $_.Remove('_MgxSourceId')
            $writer.WriteLine(($_ | ConvertTo-Json -Compress -Depth 10))

            if ($_['@odata.type'] -eq '#microsoft.graph.group') {
                if (-not $parents.ContainsKey($_['id'])) {
                    $parents[$_['id']] = [System.Collections.Generic.List[string]]::new()
                }
                $parents[$_['id']].Add($_['groupId'])
            }

            $rows++
            $started[$_['groupId']] = $true
            if ($rows % $UpdateEvery -eq 0) {
                $done = $started.Count / $groups.Count
                $t = Get-MgxTelemetry
                Write-Progress -Activity 'Downloading direct memberships' `
                    -Status ('{0} of {1} groups | {2:N0} rows | {3:N0}/s | {4} requests, {5:N0} RU' -f `
                        $started.Count, $groups.Count, $rows,
                        ($rows / $sw.Elapsed.TotalSeconds), $t.Requests, $t.ResourceUnitsConsumed) `
                    -PercentComplete (100 * $done) `
                    -SecondsRemaining ([int]($sw.Elapsed.TotalSeconds * (1 - $done) / [Math]::Max($done, 0.01)))
            }
        }
}
finally {
    $writer.Dispose()
}
Write-Progress -Activity 'Downloading direct memberships' -Completed
Write-Host ('{0:N0} direct membership row(s) in {1:mm\:ss} -> members-direct.jsonl' -f $rows, $sw.Elapsed)

# --- pass 2: resolve the nesting locally - disk to disk, no requests ---
$ancestorCache = @{}
function Get-Ancestors {
    # Every group transitively containing $GroupId. The visited set makes a
    # membership cycle a non-event rather than a hang.
    param([string]$GroupId)
    if ($ancestorCache.ContainsKey($GroupId)) { return ,$ancestorCache[$GroupId] }
    $seen  = [System.Collections.Generic.HashSet[string]]::new()
    $queue = [System.Collections.Generic.Queue[string]]::new()
    if ($parents.ContainsKey($GroupId)) { foreach ($p in $parents[$GroupId]) { $queue.Enqueue($p) } }
    while ($queue.Count -gt 0) {
        $g = $queue.Dequeue()
        if ($seen.Add($g) -and $parents.ContainsKey($g)) {
            foreach ($p in $parents[$g]) { $queue.Enqueue($p) }
        }
    }
    $ancestorCache[$GroupId] = @($seen)
    return ,$ancestorCache[$GroupId]
}

$resolved = 0
$writer = [System.IO.StreamWriter]::new($resolvedFile, $false, [System.Text.UTF8Encoding]::new($false))
try {
    foreach ($line in [System.IO.File]::ReadLines($directFile)) {
        $writer.WriteLine($line)    # a member of a group is a member of that group
        $resolved++
        $row = $line | ConvertFrom-Json
        foreach ($ancestor in (Get-Ancestors $row.groupId)) {
            $row.groupId = $ancestor
            $writer.WriteLine(($row | ConvertTo-Json -Compress -Depth 10))
            $resolved++
        }
    }
}
finally {
    $writer.Dispose()
}
Write-Host ('{0:N0} resolved membership row(s) -> members.jsonl' -f $resolved)

$t = Get-MgxTelemetry
Write-Host ('done in {0:mm\:ss} - {1} requests, {2:N0} resource units' -f `
    $sw.Elapsed, $t.Requests, $t.ResourceUnitsConsumed)

# Reading it back - everyone in one group, including via nesting. Multiple
# nesting paths collapse to one row, but a member placed DIRECTLY in several
# groups under the same ancestor appears once per placement - dedup on id:
#   Get-Content ./groups-export/members.jsonl | ConvertFrom-Json |
#       Where-Object { $_.groupId -eq $id -and $_.'@odata.type' -eq '#microsoft.graph.user' } |
#       Sort-Object id -Unique
#
# Notes:
#   * The nesting edges are the group-typed rows of members-direct.jsonl:
#       ... | ConvertFrom-Json | Where-Object '@odata.type' -eq '#microsoft.graph.group'
#   * Empty groups contribute no rows, so the bar can top out just short of
#     100 before it clears (see progress-for-long-runs.ps1).
#   * A killed run restarts pass 1: checkpoints belong to single enumerations,
#     not fan-outs. Pass 2 is local and costs nothing to redo.

<#
Expected output (final state; the bar redraws in place while running):

12 groups -> groups.jsonl
1,214 direct membership row(s) in 00:19 -> members-direct.jsonl
1,532 resolved membership row(s) -> members.jsonl
done in 00:20 - 14 requests, 27 RU
#>
