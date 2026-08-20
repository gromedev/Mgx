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

<#
Expected output:

id                                   displayName
--                                   -----------
05c76d47-f457-4be9-bf0a-a75b57af73c1 Adele Vance
b92d487c-5d51-460b-963a-c395ddc747ba Alex Wilber
02109083-4f10-4e5e-ab8b-17ec81caa674 Allan Deyoung
aea6b38c-19f7-4225-adb4-feeeea0b4f20 Brian Johnson
d1e24810-61ae-41fb-a250-88e2672cb4e1 Cameron White
27640233-6c77-4337-b2f9-693608bfd60f Christie Cline
af9691ee-cc4e-4748-8646-4605b2921a9c Debra Berger
29d7068e-0922-41e9-aa7a-7e97f477f67b Delia Dennis
f736ca7a-71d9-4708-8aae-3cfa2111a106 Diego Siciliani
26f05c3d-0bcd-4160-9789-cbea7b676305 Emily Braun
#>
