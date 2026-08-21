@{
    RootModule        = 'mgx.psm1'
    ModuleVersion     = '2.1.0'
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
v2.1.0
Adds proactive throttle avoidance, ranged content downloads, and resumable enumeration.

Added
- Adaptive request pacing, on by default. Requests are spaced before they are sent, per workload; opt out with Set-MgxOption -NoAdaptivePacing.
- Get-MgxContent, to download file and media content whole or by byte range (-First).
- Sync-MgxDelta -CheckpointPath, to resume interrupted enumerations.
- Sync-MgxDelta -Latest, to baseline state without enumerating.
- Sync-MgxDelta -Prefer, to send drive delta Prefer tokens.
- Resource units consumed, in telemetry output, benchmarks and examples.

Fixed
- -Latest was honored after a state invalidation, dropping every change since the last sync.
- Delta resume dropped the interrupted run's items when an earlier run had already completed, and advanced the delta token past them.
- Export resume dropped the interrupted run's items when an earlier export had already written the output file.
- Export resume wrote a page's items twice when a run was interrupted twice inside the same page.
- Delta resume kept the previous sync's rows in front of the interrupted sync's output.
- Export resume started the export over when the interrupted run had died on a transient error.
- A resume checkpoint naming a file that was never a temp consumed it as data.
- A refused delta token was reported as an endpoint not supporting delta queries.
- Sync-MgxDelta as a session's first cmdlet sent its request to the public cloud endpoint.
- A denied or read-only output file ended a delta sync with an unhandled error on Windows.
- A missing drive item suggested retrying in beta instead of reporting the item absent.
- -Top was ignored when combined with -All, returning the whole collection.
- Get-Help described output shapes and parameters from before 2.0.0.
- Enumeration returned part of a collection without error when a nextLink was refused.
- A delta sync stopped with an error when a page contained a non-object item.
- Get-MgxContent left an unwritable -OutFile as an unhandled error naming a temp path.
- A non-Graph JSON error body threw from inside the exception it was being used to build, surfacing a CDN failure as a security refusal.
- A $batch 429 set a pacing cap that nothing enforced and nothing cleared, so telemetry reported it forever.
- Get-MgxContent ignored Retry-After when a download host sent it as an HTTP date.
- Get-Help was missing the examples for -CheckpointPath, -Latest, -Prefer, adaptive pacing, and two Get-MgxContent endpoints, and did not document Set-MgxOption -BatchChunkConcurrency.
- Seven examples rendered blank tables, and one reported password secrets as certificates.
- Repeat 429s could raise the adaptive cap above -RateLimitPerSecond.
- Delta state did not record its API version, so runs omitting -ApiVersion silently synced the other one.
- -Debug wrote pre-authenticated download URLs verbatim, from redirect headers and from response bodies.
- Get-MgxContent -OutFile with piped input downloaded every item but kept only the last.
- -Offset was ignored when a server returned a whole-body 200.
- An offset past end of file overwrote an existing -OutFile with an empty one.
- Two-hop content downloads were reported as failed requests.
- Adaptive pacing fired synchronized bursts when sleep times were clamped.
- Enable-MgxResilience recorded no telemetry: request counts, HTTP time and rate-limiter wait all stayed 0.
- Invoke-MgGraphRequest with a relative URI failed while resilience was enabled.
- $batch envelopes were bucketed as Other rather than their target workload.
- Slow start opened above ceilings set below 4 rps.
- ResiliencePipelineFactory.Reset() reverted configuration and kept batch pacer state across credential changes.
- Ctrl-C during file cleanup raised disposed-object errors.

Documentation
- Graph delta responses repeat objects across pages; deduplicate on id, or baseline with -Latest.
- Adaptive pacing applies to every request except batch outer POSTs.
- Measured throttling behavior: resource unit budgets are scoped per application and tenant, and x-ms-throttle-limit-percentage is not emitted.

v2.0.1
Patch release. Three fixes; no feature or API changes.
- The rate limiter was disposed on a timer while live clients held it, so any Set-MgxOption call broke Enable-MgxResilience sessions minutes later.
- Enable-MgxResilience and Disable-MgxResilience cancelled SDK requests still in flight.
- Sync-MgxDelta -Uri with a query string or trailing slash failed on the second run.

Earlier releases: https://github.com/gromedev/Mgx/blob/main/CHANGELOG.md
'@
        }
    }
}
