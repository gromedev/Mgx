# Create users from a CSV, keeping the ones that fail.
#
# A batch of 200 new hires will not be 200 clean creates: a duplicate UPN, a
# rejected password, a domain that is not verified. -DeadLetterPath writes those
# items - request and response - to a JSONL file instead of losing them in the
# console, so the run can be fixed and replayed against only what failed.
#
# The CSV is expected to have DisplayName, MailNickname, UserPrincipalName and
# Department columns:
#
#   DisplayName,MailNickname,UserPrincipalName,Department
#   Grace Blake,graceb,graceb@contoso.com,Retail
#
# Requirements: Connect-MgGraph -Scopes "User.ReadWrite.All"

param(
    [string]$Path = './new-hires.csv',
    [string]$DeadLetterPath = './failed-users.jsonl'
)

Import-Module Mgx

function New-Password {
    # Cryptographically random, unlike Get-Random. A real script delivers this
    # out of band rather than leaving it in a variable.
    $chars = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%^&*'
    -join (1..16 | ForEach-Object {
        $chars[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($chars.Length)]
    })
}

$rows = Import-Csv $Path
Write-Host "$($rows.Count) row(s) in $Path"

$requests = $rows | ForEach-Object {
    [PSCustomObject]@{
        Url    = '/users'
        Method = 'POST'
        Body   = @{
            displayName       = $_.DisplayName
            mailNickname      = $_.MailNickname
            userPrincipalName = $_.UserPrincipalName
            department        = $_.Department
            accountEnabled    = $true
            passwordProfile   = @{
                password                      = New-Password
                forceChangePasswordNextSignIn = $true
            }
        }
    }
}

$results = $requests | Invoke-MgxBatchRequest -DeadLetterPath $DeadLetterPath

$created = @($results | Where-Object { $_.Status -lt 400 })
$failed  = @($results | Where-Object { $_.Status -ge 400 })
Write-Host "Created: $($created.Count)  Failed: $($failed.Count)"

# What went wrong, grouped - one bad domain looks like 40 unrelated failures
# until they are collapsed like this.
if ($failed) {
    $failed |
        Group-Object { $_.Body.error.message } |
        Select-Object Count, Name |
        Format-Table -AutoSize -Wrap
    Write-Host "Failed items saved to $DeadLetterPath"
}

# Replaying: fix the CSV, or edit the dead-letter file and feed it straight back.
# Each line is a complete request, so nothing has to be rebuilt.
#
#   Get-Content $DeadLetterPath | ConvertFrom-Json | Invoke-MgxBatchRequest

<#
Expected output:

12 row(s) in ./new-hires.csv
WARNING: 2 of 12 batch items failed after all retry attempts. Check $Error for details on each failed item.
Created: 10  Failed: 2

Count Name
----- ----
    2 Another object with the same value for property userPrincipalName already exists.

Failed items saved to ./failed-users.jsonl
#>
