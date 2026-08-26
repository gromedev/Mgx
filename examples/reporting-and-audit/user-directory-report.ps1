# A user directory: every enabled user with manager and licenses attached.
#
# Neither relation comes back with the user object, so each needs a call per
# user. Stacked Expand-MgxRelation stages fetch each relation concurrently
# across users, and the second stage receives objects already carrying the
# first - two serial foreach loops collapse into one pass.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

$directory = Invoke-MgxRequest /users -All `
        -Filter 'accountEnabled eq true' `
        -Property id,displayName,department,jobTitle |
    Expand-MgxRelation '/users/{id}/manager'        -As Manager  -Flatten -SkipNotFound |
    Expand-MgxRelation '/users/{id}/licenseDetails' -As Licenses -SkipNotFound

$directory |
    Select-Object displayName, department, jobTitle,
        @{ n = 'Manager';  e = { $_.Manager.displayName } },
        @{ n = 'Licenses'; e = { (@($_.Licenses.skuPartNumber) | Sort-Object) -join ', ' } } |
    Sort-Object department, displayName |
    Format-Table -AutoSize

# The skipped-404 warning is expected: users at the top of the org have no
# manager. Swap Format-Table for Export-Csv to hand the file to whoever asked.

<#
Expected output:

WARNING: Skipped 4 entities due to 404 (Not Found) responses.

displayName       department      jobTitle                   Manager       Licenses
-----------       ----------      --------                   -------       --------
Brian Johnson     Engineering     Senior Engineer            Lee Gu        SPE_E3
Lee Gu            Engineering     Director                                 SPE_E5
Christie Cline    Human Resources Benefits Specialist        Nestor Wilke  SPE_E5
Allan Deyoung     IT              Corporate Security Officer Nestor Wilke  EMSPREMIUM, SPE_E5
Delia Dennis      IT              Support Engineer           Allan Deyoung EMSPREMIUM, SPE_E5
Alex Wilber       Marketing       Marketing Assistant        Megan Bowen   SPE_E5
Debra Berger      Operations      Administrative Assistant   Nestor Wilke  SPE_E5
Adele Vance       Retail          Retail Manager             Nestor Wilke  SPE_E5
Cameron White     Retail          Store Associate            Adele Vance   SPE_E3
...
#>
