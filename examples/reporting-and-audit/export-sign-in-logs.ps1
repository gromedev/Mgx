# Export sign-in audit logs to JSONL with checkpoint/resume.
#
# Sign-in logs can contain millions of records. This streams directly
# to disk - no memory accumulation. If the script is interrupted
# (Ctrl+C, network drop, etc.), re-run with the same paths and it
# resumes from the last completed page.
#
# Requirements: Connect-MgGraph -Scopes "AuditLog.Read.All"

Import-Module Mgx

$outputFile     = "./signins.jsonl"
$checkpointFile = "./signins-checkpoint.json"

# Filter to last 7 days
$since = (Get-Date).AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ssZ")

$result = Export-MgxCollection /auditLogs/signIns `
    -OutputFile $outputFile `
    -CheckpointPath $checkpointFile `
    -Filter "createdDateTime ge $since" `
    -All `
    -ApiVersion beta

Write-Host "Exported $($result.ItemCount) sign-in records to $outputFile"

<#
Expected output:

Exported 246 sign-in records to ./signins.jsonl

signins.jsonl (first 2 lines):
{"id":"f8984da0-286a-464f-9631-fb15a7f1075f","createdDateTime":"2026-08-20T12:14:58Z","userDisp...
{"id":"cd13719f-f4de-483e-8ab6-b81a8c6b2ed3","createdDateTime":"2026-08-20T12:10:49Z","userDisp...
#>
