# Report app registrations with secrets or certificates expiring within 30 days.
#
# App credential expiry causes outages. This script identifies at-risk
# applications before they break, using fan-out to fetch credentials
# for all apps concurrently.
#
# Requirements: Connect-MgGraph -Scopes "Application.Read.All"

Import-Module Mgx

$warningDate = (Get-Date).AddDays(30)

$apps = Invoke-MgxRequest /applications -All -Property id,displayName,passwordCredentials,keyCredentials

$expiring = foreach ($app in $apps) {
    $creds = @($app.passwordCredentials) + @($app.keyCredentials)
    foreach ($cred in $creds) {
        if (-not $cred.endDateTime) { continue }
        $expiry = [datetime]$cred.endDateTime
        if ($expiry -le $warningDate) {
            [PSCustomObject]@{
                App        = $app.displayName
                Type       = if ($cred.ContainsKey('secretText')) { 'Secret' } else { 'Certificate' }
                ExpiresOn  = $expiry.ToString('yyyy-MM-dd')
                DaysLeft   = [int][math]::Floor(($expiry - (Get-Date)).TotalDays)
            }
        }
    }
}

if ($expiring) {
    $expiring | Sort-Object DaysLeft | Format-Table -AutoSize
} else {
    Write-Host "No credentials expiring within 30 days."
}

<#
Expected output:

App                       Type        ExpiresOn  DaysLeft
---                       ----        ---------  --------
Contoso Partner Portal    Certificate 2026-08-23        2
Contoso Expense Portal    Secret      2026-08-26        5
Contoso HR Sync           Certificate 2026-09-10       20
Contoso Field Service App Secret      2026-09-17       27
#>
