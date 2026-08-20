# Export all Conditional Access policies to JSON.
#
# CA policies are only available on the beta endpoint. -ApiVersion beta
# accesses them without installing any extra modules.
#
# Requirements: Connect-MgGraph -Scopes "Policy.Read.All"

Import-Module Mgx

$policies = Invoke-MgxRequest /identity/conditionalAccess/policies `
    -All `
    -ApiVersion beta `
    -Property id,displayName,state,createdDateTime,modifiedDateTime

Write-Host "Conditional Access policies: $($policies.Count)"
$policies | Sort-Object displayName | Select-Object displayName, state, modifiedDateTime | Format-Table -AutoSize

# Export full policy details to JSON for backup/audit
$policies | ConvertTo-Json -Depth 10 | Out-File "./conditional-access-policies.json"
Write-Host "Full export saved to conditional-access-policies.json"

<#
Expected output:

Conditional Access policies: 4

displayName                              state                             modifiedDateTime
-----------                              -----                             ----------------
Block legacy authentication              enabled                           7/20/2026 12:48:56 PM
Block sign-in from unsupported countries disabled                          5/15/2026 12:17:15 AM
Require compliant device for Exchange    enabledForReportingButNotEnforced 8/16/2026 2:13:54 AM
Require MFA for all users                enabled                           8/11/2026 9:47:30 AM

Full export saved to conditional-access-policies.json

conditional-access-policies.json (first lines):
[
  {
    "createdDateTime": "2025-07-04T09:20:08Z",
    "state": "enabled",
    "displayName": "Block legacy authentication",
    "id": "d7d8695f-36ef-4a02-9045-cc9efadb0eb1",
    "modifiedDateTime": "2026-07-20T12:48:56Z"
...
#>
