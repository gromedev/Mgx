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
- -Latest was honoured after a state invalidation, dropping every change since the last sync.
- Repeat 429s could raise the adaptive cap above -RateLimitPerSecond.
- Delta state did not record its API version, so runs omitting -ApiVersion silently synced the other one.
- -Debug wrote redirect Location headers verbatim, leaking pre-authenticated download URLs.
- Get-MgxContent -OutFile with piped input downloaded every item but kept only the last.
- -Offset was ignored when a server returned a whole-body 200.
- An offset past end of file overwrote an existing -OutFile with an empty one.
- Two-hop content downloads were reported as failed requests.
- Adaptive pacing fired synchronized bursts when sleep times were clamped.
- Enable-MgxResilience recorded no telemetry, reporting TotalRequests=0 and dividing by zero.
- $batch envelopes were bucketed as Other rather than their target workload.
- Slow start opened above ceilings set below 4 rps.
- ResiliencePipelineFactory.Reset() reverted configuration and kept batch pacer state across credential changes.
- Ctrl-C during file cleanup raised disposed-object errors.

v2.0.1
Patch release. Three fixes; no feature or API changes.
- The rate limiter was disposed on a timer while live clients held it, so any Set-MgxOption call broke Enable-MgxResilience sessions minutes later.
- Enable-MgxResilience and Disable-MgxResilience cancelled SDK requests still in flight.
- Sync-MgxDelta -Uri with a query string or trailing slash failed on the second run.

v2.0.0
Breaking release. Output shape changes to Hashtable; the floor moves DOWN to PowerShell 7.4.
Incorporates the breaking work contributed in the Microsoft365DSC fork by Fabien Tschanz.
- BREAKING: Invoke-MgxRequest, Invoke-MgxBatchRequest, Expand-MgxRelation and Sync-MgxDelta
  emit case-insensitive Hashtables instead of PSObjects, matching Invoke-MgGraphRequest, so
  results drop into scripts written for the Graph SDK. Migration for code that needs the old
  shape: -Raw | ConvertFrom-Json
- BREAKING: @odata.type is returned verbatim under its own key instead of as ODataType. All
  other @odata.* metadata is stripped, including @odata.etag: it changes on every write, so
  keeping it makes two reads of an unchanged entity compare unequal. Use -Raw for the etag
- BREAKING: the six Graph entity format views are removed - PowerShell renders any dictionary
  with its built-in Name/Value view, so a custom view can never apply. Key order is undefined:
  use Select-Object or Format-Table to fix column order
- The floor moves DOWN: targets .NET 8 and supports PowerShell 7.4 (LTS) and later, where 1.x
  required 7.5. Nothing ever needed the newer runtime - the net9.0 target dated to 1.0.1 and was
  never code-driven. The single .NET 9 API (LoadIntoBufferAsync(CancellationToken), in the -Debug
  tracer) is replaced with a WaitAsync equivalent that keeps the same timeout behaviour
- BREAKING: Microsoft.Graph.Authentication is no longer in RequiredModules. Auth was already
  resolved reflectively at call time, so declaring it only forced the SDK onto every consumer
  and installed a second copy beside one already loaded. mgx now imports without it; cmdlets
  needing a token report GraphAuthModuleNotLoaded with install instructions
- Collection envelopes are unwrapped on every response path, so an action endpoint that
  answers a write with {"value":[...]} (for example /directoryObjects/getByIds) now emits one
  object per element instead of the envelope
- Pipeline input accepts the new shape end to end: fan-out reads 'id' from Hashtables and
  PSCustomObjects instead of stringifying them into the URL, and Invoke-MgxBatchRequest
  accepts its own output, so failed items can be piped straight back in for retry
- Polly.Core 8.7.0, System.Threading.RateLimiting 10.0.10
v1.0.5
Fixes and hardening driven by a rebuilt benchmark suite run against a seeded 100k-user tenant.
- Fixed Export-MgxCollection losing all progress when a first run was interrupted: a cancelled
  run now promotes its temp file with a checkpoint matching its exact content, and a killed or
  crashed run is recovered on the next invocation from the orphaned temp file, trimmed to the
  checkpointed item count. The "Resume with:" hint is now truthful for first runs
- Fixed Get-MgxTelemetry reporting zero retry delay for batch-heavy sessions: per-item
  Retry-After waits inside $batch processing now flow into RetryDelayMs
- The count-discrepancy warning now also fires when an enumeration returns more items than
  @odata.count reported (detects transient duplicated pages; dedup on id downstream)
- Documented write-cost pacing in about_Mgx_Tuning: Graph throttles writes, not items -
  budget BatchItemsPerSecond by writes per item (a 20-member group create costs ~21 writes)
- New reproducible benchmark suite under tests/benchmarks, including a fault-injection
  gauntlet that runs against a local mock Graph and needs no tenant; README rebuilt on the
  suite's measured results
v1.0.4
Fixes ported from the Microsoft365DSC fork, contributed by Fabien Tschanz.
- Fixed Mgx cmdlets keeping the credentials of the first Connect-MgGraph call: the cached
  HTTP client was keyed on tenant id alone, so reconnecting to the same tenant with a
  different application, certificate, account, or scope set silently reused the previous
  identity. The client is now keyed on a fingerprint of the full auth context
- Fixed a JSON string passed to -Body being silently dropped (sent as {}), so a piped or
  ConvertTo-Json body now serializes correctly
- Fixed Enable-MgxResilience staying bound to the pre-reconnect SDK client; resilience is
  re-injected when the Graph identity changes
- Fixed Set-MgxOption -TotalTimeoutSeconds not reaching the HTTP client
- Fixed a single 429 slowing Invoke-MgxBatchRequest for the rest of the session; the write
  rate now recovers after clean chunks and resets after five quiet minutes
- Fixed the internal type cache never invalidating when Microsoft.Graph.Authentication was
  re-imported at a different version
- Fixed JSON integers above 2^53 losing precision
- Added -Debug request/response tracing on all cmdlets, with credential redaction
- A batch item with an invalid JSON body now fails on its own instead of aborting the batch;
  -Body on GET now warns instead of being silently ignored
- The SdkVersion header is now derived from the assembly version instead of a hand-maintained
  constant

v1.0.3
- Fixed Remove-Module Mgx failing and leaving the module loaded (cleanup now runs before the
  ALC resolver detaches). Only triggered when no Graph request had run in the session
- Fixed the SdkVersion request header reporting mgx/0.3.0 regardless of installed version
- Internal: cmdlet lifecycle and JSON conversion extracted to MgxCmdletCore. No surface change

v1.0.2
- Fixed Linux install: renamed module files to lowercase so Install-Module works on case-sensitive filesystems
- Updated about_Mgx_Tuning version reference

v1.0.1
- Added tab completion for Uri parameter on all cmdlets
- Extracted CircuitBreakerMessage protected property on MgxCmdletBase
- Removed redundant XML doc comments on self-documenting members

v1.0.0 - Initial public release

v0.3.0 - Plug-and-play resilience
- NEW: Expand-MgxRelation: pipeline-composable relation enrichment
  - Enrich Graph objects with related data via concurrent fan-out
  - Auto-detects collection vs singleton endpoints (no silent data loss)
  - -Top caps per-relation items (server-side $top + client-side truncation)
  - -Flatten unwraps single-value relations, warns on multi-item
  - -SkipNotFound/-SkipForbidden for partial failure tolerance
  - Chaining: pipe through multiple Expand-MgxRelation stages
- NEW: Enable-MgxResilience / Disable-MgxResilience / Get-MgxResilience: zero-change resilience injection
  - Wraps SDK HttpClient with Polly retry, circuit breaker, and rate limiting
  - All Microsoft.Graph SDK cmdlets (Get-MgUser, etc.) gain resilience automatically
  - Preserves full SDK handler chain (OData, NationalCloud, Redirect, Auth)
  - Re-call after Connect-MgGraph to re-inject
  - Thread-safe for concurrent runspaces
  - Sovereign cloud support (auto-detects Graph endpoint)
- NEW: Get-MgxOption / Set-MgxOption: runtime resilience pipeline tuning
  - 11 configurable parameters: rate limiting, retry, circuit breaker, timeouts
  - CircuitBreakerSamplingDurationSeconds: control the failure measurement window (5-300s)
  - Partial updates: only explicitly passed values are changed
  - Set-MgxOption -Reset restores all defaults
- FIXED: Circuit breaker and rate limiter now shared across cmdlet invocations
  - Previously created fresh per invocation, making both non-functional
  - New ResiliencePipelineFactory manages shared static Polly pipeline
- FIXED: Rate limiter GC root leak on options change
  - TokenBucketRateLimiter holds an internal Timer (GC root via AutoReplenishment)
  - Old limiters now disposed after TotalTimeoutSeconds delay to avoid ObjectDisposedException
- FIXED: nextLink SSRF validation strengthened
  - Rejects non-HTTPS scheme (prevents token leak over plaintext)
  - Compares full authority (host + port), not just host
  - ConcurrentFanOut rejects all nextLinks if initial URL parse fails
- FIXED: FindType() cache no longer caches null results
  - Previously, importing Mgx before Microsoft.Graph permanently broke lookups
- IMPROVED: Request cloning copies Options, Version, and all content headers on retry

v0.2.0 - Restructure as companion module
- NEW: Invoke-MgxRequest: general-purpose resilient client for any Graph endpoint
  - Streaming pagination with -All, -Top, -PageSize
  - Fan-out concurrency with {id} template substitution
  - Write operations (POST, PATCH, PUT, DELETE)
  - -ApiVersion (v1.0/beta), -ConsistencyLevel, -NoPageSize
  - -SkipNotFound/-SkipForbidden for fan-out error handling
  - -Raw for JSON string output, -CheckpointPath for resume
  - Progress reporting, pipeline stop support
  - ArgumentCompleters for tab completion
- IMPROVED: @odata.type preserved as ODataType property (polymorphic queries)
- IMPROVED: DateTime strings parsed to DateTimeOffset
- NEW: Export-MgxCollection: JSONL streaming export with checkpoint/resume
  - Raw JSON to disk, no PSObject overhead, constant memory
  - -All, -Top, -CheckpointPath for resumable exports
  - SupportsShouldProcess for -WhatIf/-Confirm
- IMPROVED: Set-MgxOption only overrides explicitly passed values
- FIXED: Set-MgxOption referenced deleted base class
- REMOVED: Get-MgxUser, Get-MgxGroup, Get-MgxGroupMember, Get-MgxApplication, Get-MgxServicePrincipal, Get-MgxDirectoryRole
  - Use Invoke-MgxRequest '/users', '/groups', etc. instead
'@
        }
    }
}
