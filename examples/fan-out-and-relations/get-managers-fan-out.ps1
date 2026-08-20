# Get the manager for every user in the tenant using concurrent fan-out.
#
# The naive approach is a foreach loop: one HTTP call per user.
# With {id} template substitution, Invoke-MgxRequest dispatches all
# requests concurrently (default concurrency: 10) and streams results
# back as they complete.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

# Get all users first, then fan-out to fetch each manager concurrently.
$users = Invoke-MgxRequest /users -All -Property id,displayName

$users |
    Invoke-MgxRequest '/users/{id}/manager' -SkipNotFound |
    Select-Object id, displayName |
    Format-Table -AutoSize

<#
Expected output:

WARNING: This endpoint may only be available in beta. Retry with -ApiVersion beta.
WARNING: Skipped 4 entities due to 404 (Not Found) responses.
id                                   displayName
--                                   -----------
6426ded0-36f8-4bf7-9fe4-1353bd002dbe Nestor Wilke
2d871c04-0885-491c-bcbb-f14ab6f4a375 Megan Bowen
6426ded0-36f8-4bf7-9fe4-1353bd002dbe Nestor Wilke
c5151c70-bc16-4934-acf2-1a776694b6af Lee Gu
05c76d47-f457-4be9-bf0a-a75b57af73c1 Adele Vance
6426ded0-36f8-4bf7-9fe4-1353bd002dbe Nestor Wilke
6426ded0-36f8-4bf7-9fe4-1353bd002dbe Nestor Wilke
02109083-4f10-4e5e-ab8b-17ec81caa674 Allan Deyoung
...
#>
