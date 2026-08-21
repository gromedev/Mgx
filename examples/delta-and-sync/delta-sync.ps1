# Incremental sync: only fetch users that changed since the last run.
#
# The first run downloads all users and saves a delta token to disk.
# Every subsequent run fetches only changes (new, modified, deleted)
# since the previous sync. Token management is automatic.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

$deltaFile = "./users-delta.json"

$changes = Sync-MgxDelta /users/delta `
    -DeltaPath $deltaFile `
    -Property id,displayName,mail,accountEnabled

Write-Host "Changes since last sync: $($changes.Count)"
$changes | Select-Object id, displayName, mail, accountEnabled | Format-Table -AutoSize

<#
Expected output:

Changes since last sync: 28

id                                   displayName       mail                                accountEnabled
--                                   -----------       ----                                --------------
05c76d47-f457-4be9-bf0a-a75b57af73c1 Adele Vance       adelev@contoso.com                            True
b92d487c-5d51-460b-963a-c395ddc747ba Alex Wilber       alexw@contoso.com                             True
02109083-4f10-4e5e-ab8b-17ec81caa674 Allan Deyoung     allande@contoso.com                           True
aea6b38c-19f7-4225-adb4-feeeea0b4f20 Brian Johnson     brianj@contoso.com                           False
d1e24810-61ae-41fb-a250-88e2672cb4e1 Cameron White     cameronw@contoso.com                         False
27640233-6c77-4337-b2f9-693608bfd60f Christie Cline    christiec@contoso.com                         True
...
#>
