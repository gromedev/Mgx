# Membership of every group: direct, and effective through nesting.
#
# A group's real membership is not its member list - groups contain groups.
# /transitiveMembers flattens the nesting server-side, so there is no recursion
# to write and no cycle handling to get wrong. The groups where effective and
# direct counts differ are the ones nesting hides members in.
#
# The memory model is half the point. Tenant-wide membership is too big to
# hold, so no member list is ever kept: the fan-out streams members out as
# pages arrive, each tagged with _MgxSourceId (the group it came from), and
# the loop reduces them to a count on the spot. RAM holds one row per group,
# not the membership of the tenant.
#
# Requirements: Connect-MgGraph -Scopes "Group.Read.All", "User.Read.All"

Import-Module Mgx

# One row per group is all that is ever kept.
$groups = @(Invoke-MgxRequest /groups -All -Property id,displayName)
$stats  = @{}
foreach ($g in $groups) {
    $stats[$g.id] = [pscustomobject]@{
        Group     = $g.displayName
        Direct    = 0
        Effective = 0
        Nested    = [System.Collections.Generic.List[string]]::new()
    }
}
Write-Host "$($groups.Count) groups"

# The type casts filter server-side: /members/microsoft.graph.user returns only
# user members, so nothing arrives that has to be discarded, and $select=id
# keeps each streamed object to the two fields the tally needs.
$groups | Invoke-MgxRequest '/groups/{id}/members/microsoft.graph.user?$select=id' `
        -All -SkipNotFound -SkipForbidden |
    ForEach-Object { $stats[$_._MgxSourceId].Direct++ }

$groups | Invoke-MgxRequest '/groups/{id}/members/microsoft.graph.group?$select=id,displayName' `
        -All -SkipNotFound -SkipForbidden |
    ForEach-Object { $stats[$_._MgxSourceId].Nested.Add($_.displayName) }

$groups | Invoke-MgxRequest '/groups/{id}/transitiveMembers/microsoft.graph.user?$select=id' `
        -All -SkipNotFound -SkipForbidden |
    ForEach-Object { $stats[$_._MgxSourceId].Effective++ }

$report = $stats.Values |
    Select-Object Group, Direct, Effective, @{ n = 'Nested'; e = { $_.Nested -join ', ' } } |
    Sort-Object { $_.Effective - $_.Direct }, Group -Descending

$report | Format-Table -AutoSize
$hidden = @($report | Where-Object { $_.Effective -gt $_.Direct })
Write-Host "$($hidden.Count) group(s) have members through nesting alone"

# Notes:
#   * Expand-MgxRelation would attach the member arrays to each group instead -
#     right when the enriched objects ARE the deliverable (user-directory-report
#     does exactly that), wrong here, where they would only be counted and
#     thrown away. Expand holds all input and all fetched relations until its
#     fan-out completes; this report only ever holds integers.
#   * Nesting is a security-group phenomenon - Microsoft 365 groups do not
#     accept groups as members, so their two counts always match.
#   * transitiveMembers bills more resource units per call than members;
#     resource-unit-budgeting.ps1 measures both shapes.
#   * The inverse question - every group a user is effectively in, nesting
#     resolved - is one relation: /users/{id}/transitiveMemberOf.
#   * On a tenant where this runs for minutes, progress-for-long-runs.ps1 is
#     the same fan-out with a live status line.

<#
Expected output:

12 groups

Group                 Direct Effective Nested
-----                 ------ --------- ------
Engineering All Hands      3        11 Engineering Leads, Engineering ICs
Retail                     2         9 Retail East, Retail West
U.S. Sales                 5         5
Mark 8 Project Team        8         8
sg-IT-Helpdesk             4         4
...

2 group(s) have members through nesting alone
#>
