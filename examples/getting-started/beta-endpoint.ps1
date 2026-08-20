# Access beta Graph endpoints without installing Microsoft.Graph.Beta.*.
#
# The -ApiVersion parameter switches between v1.0 (default) and beta
# on the same cmdlet, with the same resilience and pagination support.
# No extra module installs needed.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

# Get users with beta-only properties (e.g. customSecurityAttributes)
$users = Invoke-MgxRequest /users `
    -ApiVersion beta `
    -Top 10 `
    -Property id,displayName,customSecurityAttributes

$users | Select-Object id, displayName | Format-Table -AutoSize
