# Report devices that haven't checked in for 90+ days.
#
# Stale devices are candidates for cleanup. This filters server-side
# by approximateLastSignInDateTime to avoid pulling every device.
#
# Requirements: Connect-MgGraph -Scopes "Device.Read.All"

Import-Module Mgx

$cutoff = (Get-Date).AddDays(-90).ToString("yyyy-MM-ddTHH:mm:ssZ")

$stale = Invoke-MgxRequest /devices `
    -All `
    -Filter "approximateLastSignInDateTime le $cutoff" `
    -Property displayName,operatingSystem,operatingSystemVersion,approximateLastSignInDateTime,accountEnabled `
    -ConsistencyLevel eventual

Write-Host "Stale devices (90+ days): $($stale.Count)"

$stale |
    Sort-Object approximateLastSignInDateTime |
    Select-Object displayName, operatingSystem, approximateLastSignInDateTime, accountEnabled |
    Format-Table -AutoSize

<#
Expected output:

Stale devices (90+ days): 5

displayName      operatingSystem approximateLastSignInDateTime accountEnabled
-----------      --------------- ----------------------------- --------------
ISAIAHL-LAPTOP   Windows         9/13/2025 1:55:10 AM                   False
ADELEV-SURFACE   Windows         1/17/2026 10:17:15 PM                   True
CONTOSO-KIOSK-02 Windows         2/14/2026 3:15:20 AM                    True
CONTOSO-MAC-04   macOS           4/10/2026 1:10:32 PM                    True
MEGANB-IPAD      iOS             5/16/2026 5:23:58 AM                    True
#>
