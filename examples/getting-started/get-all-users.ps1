# Get all users from your tenant and display them in a table.
#
# The Microsoft.Graph SDK equivalent (Get-MgUser -All) buffers every user
# in memory before returning. This streams results to the pipeline as each
# page arrives, keeping memory constant regardless of tenant size.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

Invoke-MgxRequest /users -All -Property displayName,mail,department,jobTitle |
    Select-Object displayName, mail, department, jobTitle |
    Format-Table -AutoSize

<#
Expected output:

displayName       mail                                department      jobTitle
-----------       ----                                ----------      --------
Adele Vance       adelev@contoso.com                  Retail          Retail Manager
Alex Wilber       alexw@contoso.com                   Marketing       Marketing Assistant
Allan Deyoung     allande@contoso.com                 IT              Corporate Security Officer
Brian Johnson     brianj@contoso.com                  Engineering     Senior Engineer
Cameron White     cameronw@contoso.com                Retail          Store Associate
Christie Cline    christiec@contoso.com               Human Resources Benefits Specialist
Debra Berger      debrab@contoso.com                  Operations      Administrative Assistant
Delia Dennis      deliad@contoso.com                  IT              Support Engineer
...
#>
