# Export all users to a CSV file.
#
# Export-MgxCollection writes JSONL because Graph objects are nested and JSONL
# survives that without losing anything. CSV is flat, so it needs a projection:
# name the columns you want and let Export-Csv stream them to disk as pages
# arrive. Memory stays flat - nothing accumulates in a variable first.
#
# Nested values (assignedLicenses, businessPhones, manager) need flattening by
# hand, as below, or they land in the file as type names.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

$outputFile = "./users.csv"

$count = 0
Invoke-MgxRequest /users -All `
    -Property id,displayName,mail,department,jobTitle,accountEnabled,businessPhones |
    ForEach-Object { $count++; $_ } |
    Select-Object id, displayName, mail, department, jobTitle, accountEnabled,
        @{ Name = 'businessPhones'; Expression = { $_.businessPhones -join ';' } } |
    Export-Csv -Path $outputFile -NoTypeInformation -Encoding utf8

Write-Host "Exported $count users to $outputFile"

# Excel opens UTF-8 CSV correctly only when it starts with a BOM. If the file is
# for Excel rather than for another script, use -Encoding utf8BOM instead.
#
# For a resumable export over a large tenant, write JSONL with
# Export-MgxCollection -CheckpointPath (see export-users-to-jsonl.ps1) and
# convert afterwards:
#   Get-Content ./users.jsonl | ConvertFrom-Json | Export-Csv ./users.csv -NoTypeInformation

<#
Expected output:

Exported 28 users to ./users.csv

users.csv (first 3 lines):
"id","displayName","mail","department","jobTitle","accountEnabled","businessPhones"
"05c76d47-f457-4be9-bf0a-a75b57af73c1","Adele Vance","adelev@contoso.com","Retail","Retail Manager","True","+1 425 555 0109"
"b92d487c-5d51-460b-963a-c395ddc747ba","Alex Wilber","alexw@contoso.com","Marketing","Marketing Assistant","True","+1 858 555 0110"
#>
