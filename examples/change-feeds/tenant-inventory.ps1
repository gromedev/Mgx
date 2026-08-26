# Everything the tenant has: every root collection, each to its own JSONL.
#
# Graph has no /* - but $metadata is the machine-readable list of every root
# collection the API serves (41 of them on v1.0 today), and it needs no token.
# So "grab everything" is: ask the schema for the collections, export each one,
# and treat refusals as answers rather than failures.
#
# Re-running resumes. A set with a pending checkpoint continues mid-enumeration,
# a set that already finished is skipped, so a killed overnight run picks up
# where it died instead of starting over. Delete the output directory to redo.
#
# This is the snapshot half of a pair: run it once, then tenant-change-feed.ps1
# appends what changes afterwards. Snapshot plus feed is the tenant as of now,
# without ever enumerating it a second time.
#
# Requirements: whatever read scopes you have. Directory.Read.All covers most of
# the directory tier; each extra scope (Sites.Read.All, Policy.Read.All, ...)
# unlocks more sets. A denied set is reported, not fatal.

param(
    [string]$OutputDirectory = './tenant-inventory',
    # directoryObjects is the union of users, groups, service principals, and
    # devices - exporting it re-transfers what other sets already cover.
    [string[]]$Skip = @('directoryObjects')
)

Import-Module Mgx

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# The schema is the wildcard: every EntitySet is a root collection.
$csdl = Invoke-RestMethod 'https://graph.microsoft.com/v1.0/$metadata'
$sets = @($csdl.Edmx.DataServices.Schema.EntityContainer.EntitySet.Name |
    Where-Object { $_ -notin $Skip } | Sort-Object)

Write-Host "$($sets.Count) root collections declared by `$metadata"

$results = foreach ($set in $sets) {
    $outFile    = Join-Path $OutputDirectory "$set.jsonl"
    $checkpoint = Join-Path $OutputDirectory "$set.checkpoint"

    # Finished on a previous run: output present, no checkpoint pending.
    if ((Test-Path $outFile) -and -not (Test-Path $checkpoint)) {
        [pscustomobject]@{ Set = $set; Items = $null; Outcome = 'done (previous run)' }
        continue
    }

    try {
        # Empty sets are normal here, so the 0-item warning (which suspects a
        # single-entity URI) is suppressed - it does not apply to this workload.
        $r = Export-MgxCollection "/$set" -All `
            -OutputFile $outFile -CheckpointPath $checkpoint `
            -WarningAction SilentlyContinue -ErrorAction Stop
        [pscustomobject]@{ Set = $set; Items = $r.ItemCount; Outcome = 'exported' }
    }
    catch {
        $msg = ($_.Exception.Message -split "`n")[0]
        $outcome = switch -Regex ($msg) {
            'Authorization_RequestDenied|Forbidden' { 'denied - scope not granted' }
            'BadRequest|NotImplemented|not support' { 'not enumerable at the root' }
            default                                 { $msg }
        }
        [pscustomobject]@{ Set = $set; Items = $null; Outcome = $outcome }
    }
}

$results | Format-Table Set, Items, Outcome -AutoSize

$exported = @($results | Where-Object Outcome -eq 'exported')
$totalItems = ($exported.Items | Measure-Object -Sum).Sum
$ru = (Get-MgxTelemetry).ResourceUnitsConsumed
Write-Host ("{0} of {1} sets exported, {2:N0} objects, {3:N0} resource units" -f `
    $exported.Count, $sets.Count, $totalItems, $ru)

# Reading the outcomes: an exported set with 0 items is an answer - the
# collection exists and is empty. Only a denial says you cannot know. And the
# refusals are by design, not gaps: chats needs user context, shares needs
# sharing tokens, drives is enumerated per site or user, not tenant-wide.
#
# Properties: each object carries its type's DEFAULT property set. Deliberate -
# naming every declared property makes queries fragile (signInActivity, for
# one, fails the whole request without the license to read it). The full
# per-type property list is in the same $metadata if a set is worth the tuning.
#
# The other half of "everything" is the 31 singletons ($csdl ... .Singleton):
# auditLogs, deviceManagement, reports, security. Those are not collections -
# the interesting data hangs off them as navigation properties, each with its
# own scope, and that is a crawler rather than a loop.

<#
Expected output:

40 root collections declared by $metadata

Set                                Items Outcome
---                                ----- -------
agreementAcceptances                   0 exported
agreements                             0 exported
applications                           9 exported
applicationTemplates                1743 exported
appRoleAssignments                       not enumerable at the root
certificateBasedAuthConfiguration      0 exported
chats                                    not enumerable at the root
connections                            0 exported
contacts                               0 exported
contracts                              0 exported
devices                                5 exported
directoryRoles                        14 exported
domains                                2 exported
groups                                12 exported
oauth2PermissionGrants                19 exported
organization                           1 exported
places                                 0 exported
servicePrincipals                     47 exported
shares                                   not enumerable at the root
sites                                    denied - scope not granted
subscribedSkus                         3 exported
teams                                 12 exported
users                                 28 exported
...

34 of 40 sets exported, 1,934 objects, 78 resource units
#>
