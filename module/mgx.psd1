@{
    RootModule        = 'mgx.psm1'
    ModuleVersion     = '2.1.4'
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
v2.1.4
Fixed
- Interrupted exports resume safely: unreadable, foreign, legacy, and case-differing checkpoints are handled without data loss or duplication.
- Batch items the server answered keep their results through chunk failures; the dead-letter file never contains an applied write.
- Batch summaries count only sent operations, and any chunk refusal reads as a failure.
- A stalled response or error body fails at the body-read timeout in the error contract's terms.
- A UTF-8 byte-order mark no longer discards fan-out, bulk-write, or expanded-relation results.
- Headers, telemetry ratios, and flags enum combinations render culture-invariantly with their attribute-supplied names.
- Removing the module restores the SDK's own client; re-enabling resilience never stacks wrappers.
- A resuming run never takes a file a live run is writing, and never resumes a checkpoint it gave up on.
- A caller's stop keeps the next retry off the wire; completed runs leave no abandoned partial files.
Added
- Ecosystem isolation matrix over Az.Accounts, PnP.PowerShell, and ExchangeOnlineManagement, with its own CI job.
See CHANGELOG.md for the full list.
'@
        }
    }
}
