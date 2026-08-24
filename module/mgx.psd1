@{
    RootModule        = 'mgx.psm1'
    ModuleVersion     = '2.1.2'
    GUID              = 'a3f7e8d2-5b4c-4a1e-9f6d-2c8b0e3a7d5f'
    Author            = 'Thomas Maillo Grome'
    CompanyName       = 'Mgx'
    Copyright         = '(c) 2026 Thomas Maillo Grome. All rights reserved.'
    Description       = 'Resilient companion for Microsoft.Graph PowerShell. Adds retry, circuit breaker, rate limiting, streaming pagination, batching, and fan-out to any Graph API endpoint.'

    PowerShellVersion = '7.4'
    CompatiblePSEditions = @('Core')

    FormatsToProcess  = @('mgx.Format.ps1xml')

    # Pre-load Mgx.Engine.dll so it resolves into the same load context
    # as Mgx.Cmdlets.dll. Without this, MgxTelemetrySummary (a record type
    # returned by MgxTelemetryCollector.GetSummary()) fails to load at JIT
    # time with TypeLoadException when Get-MgxTelemetry is called.
    RequiredAssemblies = @('Mgx.Engine.dll')

    # Microsoft.Graph.Authentication is deliberately NOT in RequiredModules.
    #
    # Auth is discovered reflectively at call time (GraphSession.Instance, falling back to
    # Get-MgContext), so nothing here links against the SDK and the module imports without it.
    # Declaring it would force the dependency on every consumer - including hosts that supply
    # their own Graph auth and only want the resilience layer - and would install a second copy
    # alongside whatever they already load. Cmdlets that need a token raise
    # GraphAuthModuleNotLoaded with install instructions when it is genuinely absent.

    CmdletsToExport   = @(
        'Invoke-MgxRequest'
        'Invoke-MgxBatchRequest'
        'Export-MgxCollection'
        'Expand-MgxRelation'
        'Set-MgxOption'
        'Get-MgxOption'
        'Enable-MgxResilience'
        'Disable-MgxResilience'
        'Get-MgxResilience'
        'Get-MgxTelemetry'
        'Sync-MgxDelta'
        'Get-MgxContent'
    )

    FunctionsToExport = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags         = @('Microsoft', 'Graph', 'MicrosoftGraph', 'API', 'Azure', 'EntraID', 'Resilience', 'PowerShell', 'Polly', 'Retry', 'RateLimit', 'Batch', 'Delta', 'Throttling', 'Pagination')
            LicenseUri   = 'https://github.com/gromedev/mgx/blob/main/LICENSE'
            ProjectUri   = 'https://github.com/gromedev/mgx'
            ReleaseNotes = @'
v2.1.2

Fixed
- URI handling: a '#' in a path dropped everything after it, pre-encoded query values were encoded twice, piped drive and item ids went unescaped, an absolute -Uri was mangled rather than refused, and a typed parameter could repeat an option already in -Uri.
- Body serialization: enums went out as numbers, byte arrays as integer lists, TimeSpan and a Kind-less DateTime in forms Graph refuses, and a PSCustomObject body lost every property that was not a NoteProperty.
- Headers: content headers from -Headers were dropped, arrays were sent as System.String[], names merged case-sensitively, and a caller's client-request-id was doubled.
- Failed batch items wrote no error records, so -ErrorAction Stop did not stop.
- An empty, non-JSON or malformed response body ended the cmdlet with an unhandled error.
- An unexpected failure discarded the retry history explaining it, and a timed-out attempt could retry after Ctrl-C.

v2.1.1
Patch release. One fix; no feature or API changes.

Fixed
- Enable-MgxResilience ignored throttling: pacing never slowed, telemetry reported no retries, and a throttled request could fail outright.

Changed
- Under Enable-MgxResilience, a 503 or 504 on a write is no longer retried. Throttling (429) is unaffected.
'@
        }
    }
}
