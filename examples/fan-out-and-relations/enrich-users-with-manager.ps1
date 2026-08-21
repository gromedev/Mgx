# Enrich a list of users with their manager's display name.
#
# Expand-MgxRelation concurrently fetches the related data for each
# piped object and attaches it as a new property. This replaces a
# manual foreach loop with N serial HTTP calls.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

Invoke-MgxRequest /users -Top 50 -Property id,displayName,mail |
    Expand-MgxRelation '/users/{id}/manager' -As Manager -Flatten -SkipNotFound |
    Select-Object displayName, mail, @{ n='Manager'; e={ $_.Manager.displayName } } |
    Format-Table -AutoSize

<#
Expected output:

WARNING: Skipped 4 entities due to 404 (Not Found) responses.

displayName       mail                                Manager
-----------       ----                                -------
Adele Vance       adelev@contoso.com                  Nestor Wilke
Alex Wilber       alexw@contoso.com                   Megan Bowen
Allan Deyoung     allande@contoso.com                 Nestor Wilke
Brian Johnson     brianj@contoso.com                  Lee Gu
Cameron White     cameronw@contoso.com                Adele Vance
Christie Cline    christiec@contoso.com               Nestor Wilke
Debra Berger      debrab@contoso.com                  Nestor Wilke
Delia Dennis      deliad@contoso.com                  Allan Deyoung
...
#>
