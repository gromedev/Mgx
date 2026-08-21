# Bulk-create users with dead-letter tracking.
#
# Failed batch items are written to a JSONL file instead of silently
# dropped. Re-run the retry block to attempt them again - useful for
# production scripts where partial failures must not be lost.
#
# Requirements: Connect-MgGraph -Scopes "User.ReadWrite.All"

Import-Module Mgx

$deadLetterFile = "./failed-users.jsonl"
$domain         = "contoso.com"  # replace with your tenant domain

# Build 50 user creation requests
$requests = 1..50 | ForEach-Object {
    [PSCustomObject]@{
        Url    = "/users"
        Method = "POST"
        Body   = @{
            displayName       = "Sample User $_"
            mailNickname      = "sampleuser$_"
            userPrincipalName = "sampleuser$_@$domain"
            accountEnabled    = $true
            passwordProfile   = @{
                password                      = "P@ss$(Get-Random -Minimum 1000 -Maximum 9999)!"
                forceChangePasswordNextSignIn = $false
            }
        }
    }
}

# First attempt - failed items go to dead-letter file
$results = $requests | Invoke-MgxBatchRequest -DeadLetterPath $deadLetterFile
Write-Host "Created: $(($results | Where-Object { $_.Status -lt 400 }).Count)"
Write-Host "Failed:  $(($results | Where-Object { $_.Status -ge 400 }).Count)"

# Retry from dead-letter file
if (Test-Path $deadLetterFile) {
    Write-Host "Retrying failed items..."
    $retry = Get-Content $deadLetterFile | ConvertFrom-Json
    $retry | Invoke-MgxBatchRequest
    Remove-Item $deadLetterFile
}

<#
Expected output:

Invoke-MgxBatchRequest: /tmp/mgx-examples/dead-letter-retry.ps1:33
Line |
  33 |  ...  = $requests | Invoke-MgxBatchRequest -DeadLetterPath $deadLetterFile
     |                     ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
     | POST /users: Request_BadRequest: Another object with the same value for property
     | userPrincipalName already exists.
...
WARNING: 10 of 50 batch items failed after all retry attempts. Check $Error for details on each failed item.
Created: 40
Failed:  10
Retrying failed items...

Name                           Value
----                           -----
Status                         400
Url                            /users
Body                           {[error, System.Collections.Hashtable]}
Method                         POST
...
WARNING: 10 of 10 batch items failed after all retry attempts. Check $Error for details on each failed item.
#>
