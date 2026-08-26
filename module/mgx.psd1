@{
    RootModule        = 'mgx.psm1'
    ModuleVersion     = '2.1.3'
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
v2.1.3
Added
- about_Mgx_Errors documentation: termination behavior, error records, -ErrorAction/-ErrorVariable handling.
- Live test suite supports client secret authentication alongside certificates.
Fixed
- SecureString, credential objects, and script blocks in -Body throw explicit errors rather than serializing as reflection noise.
- Server errors, timeouts, and precondition failures assign specific ErrorCategory values instead of NotSpecified.
- Get-MgxContent forwards the conditional header family (including If-None-Match) to content download hosts.
- Failed batch chunks under -ErrorAction Stop write dead-letter files prior to terminating.
- Typed parameters deferring to options already present in -Uri emit warnings rather than dropping silently.
- Request bodies over 4 MB pass directly to the SDK chain under Enable-MgxResilience instead of being rejected.
- HTTP 304 on conditional downloads completes cleanly without an error record.
- Circular self-referencing objects in -Body raise validation errors instead of a process stack overflow.
Changed
- Unified failure classification across retries, circuit breaking, batch retry checks, content downloads, and adaptive pacing.
See CHANGELOG.md for the full list.

v2.1.2
Fixed
- URI handling: a '#' in a path dropped everything after it, pre-encoded query values were encoded twice, piped drive and item ids went unescaped, an absolute -Uri was mangled rather than refused, and a typed parameter could repeat an option already in -Uri.
- Body serialization: enums went out as numbers, byte arrays as integer lists, TimeSpan and a Kind-less DateTime in forms Graph refuses, and a PSCustomObject body lost every property that was not a NoteProperty.
- Headers: content headers from -Headers were dropped, arrays were sent as System.String[], names merged case-sensitively, and a caller's client-request-id was doubled.
- Failed batch items wrote no error records, so -ErrorAction Stop did not stop.
- An empty, non-JSON or malformed response body ended the cmdlet with an unhandled error.
- An unexpected failure discarded the retry history explaining it, and a timed-out attempt could retry after Ctrl-C.

See CHANGELOG.md for the full history.
'@
        }
    }
}
