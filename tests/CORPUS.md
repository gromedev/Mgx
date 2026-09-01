# Regression corpus

Every entry below started as a real failure reported against Microsoft365DSC or the Graph
PowerShell SDK. The issue numbers are provenance, never scope: mgx does not reproduce the
reported case, it generalizes the class of failure and pins the guarantee the tests hold.
`tests/Mgx.IntegrationTests/TestSetup/HostileInputs.cs` carries the same provenance on the input
groups themselves; this file is the index over the whole suite.

**Class** is the `MgxErrorClass` the failure surfaces as, from the classifier's own vocabulary in
`src/Mgx.Engine/Errors/MgxErrorClass.cs`. `n/a` is not a gap in the index: several of these
failures never reach the classifier at all, because nothing errors - a truncated enumeration, a
body the service accepts and misreads, a module that fails to load before a request exists.

**Test type** is `wire` for xUnit over the mock transport, `unit` for xUnit with no transport,
`pester` for `tests/Unit`, and `live` for `tests/Live`, which needs a tenant.

Test paths are relative to `tests/`. Where no test exists the row names the release that brings
one rather than pointing at something adjacent: an index that overstates coverage is worse than a
gap.

## Covered

| Issue | Source | Class | Guarantee | Test type | Subsystem | Tests |
| --- | --- | --- | --- | --- | --- | --- |
| M365DSC-7273 | Microsoft365DSC | Throttle | Sustained throttling is absorbed: Retry-After is honored, the rate halves on 429, and it climbs back afterwards. A 429 that outlives its retries reaches the caller under every `-ErrorAction`. | wire, live | pacing, retry | `Mgx.IntegrationTests/Engine/Http/RetryTests.cs`, `Mgx.IntegrationTests/Engine/Http/AdaptivePacingTests.cs`, `Mgx.IntegrationTests/Engine/Http/AdaptiveRequestPacerTests.cs`, `Mgx.IntegrationTests/Engine/Http/RetryAfterDateFormTests.cs`, `Mgx.IntegrationTests/Engine/Http/BatchPacingStateTests.cs`, `Mgx.IntegrationTests/Cmdlets/ErrorActionMatrixTests.cs` (throttling cells), `Live/Mgx.Throttle.Tests.ps1` |
| M365DSC-7274 | Microsoft365DSC | n/a | A large collection enumerates complete - through every nextLink, across page seams, and after a resume. | wire | pagination | `Mgx.IntegrationTests/Engine/Pagination/PaginationTests.cs`, `Mgx.IntegrationTests/Engine/Pagination/BoundarySizeTests.cs`, `Mgx.IntegrationTests/Engine/Pagination/CheckpointTests.cs`, `Mgx.IntegrationTests/Engine/Pagination/FanOutTests.cs`, `Mgx.IntegrationTests/Cmdlets/TopWithAllTests.cs`, `Mgx.IntegrationTests/Cmdlets/ExportCheckpointIntegrityTests.cs` |
| GraphSDK-3654 | Graph PowerShell SDK | InvalidRequest | A PowerShell wrapper never changes the wire JSON: `Serialize(x)` is byte-identical to `Serialize(PSObject.AsPSObject(x))`, at any nesting depth. | wire, unit | serialization | `Mgx.IntegrationTests/Serialization/PSWrapperEquivalenceTests.cs`, `Mgx.IntegrationTests/Serialization/WireBodyTests.cs` |
| GraphSDK-2942, GraphSDK-2709 | Graph PowerShell SDK | n/a | Hostile filter values are escaped exactly once, and the wire value decodes back to what the caller wrote. | wire | URI | `Mgx.IntegrationTests/Cmdlets/UriEncodingTests.cs`, `Mgx.IntegrationTests/TestSetup/HostileInputs.cs` (`FilterValues`) |
| GraphSDK-2488 | Graph PowerShell SDK | NotFound | A service-issued link is followed byte for byte - no re-encoding, no normalization - including across a checkpoint save and load. Validation may refuse a link, but never repairs one. | wire, unit | pagination | `Mgx.IntegrationTests/Engine/Pagination/OpaqueLinkTests.cs`, `Mgx.IntegrationTests/Engine/Pagination/SsrfValidationTests.cs`, `Mgx.IntegrationTests/TestSetup/HostileInputs.cs` (`OpaqueLinks`) |
| GraphSDK-1947 | Graph PowerShell SDK | NotFound | A `#` in a path stays in the path instead of being read as a URI fragment. | wire | URI | `Mgx.IntegrationTests/Cmdlets/UriEncodingTests.cs`, `Mgx.IntegrationTests/TestSetup/HostileInputs.cs` (`FilterValues`, `PathSegments`) |
| GraphSDK-2328 | Graph PowerShell SDK | InvalidRequest | Caller headers reach the wire as given: content headers land on the content collection rather than being dropped, and the merge is case-insensitive like HTTP header names are. | wire, unit | headers | `Mgx.IntegrationTests/Engine/Http/HeaderFidelityTests.cs`, `Mgx.IntegrationTests/Cmdlets/HeaderWireTests.cs` |
| GraphSDK-1425, GraphSDK-2088 | Graph PowerShell SDK | Permanent | A response that is not the JSON entity the caller expected - empty, HTML from a proxy, truncated - surfaces as verbose output or an error record, never an unhandled exception. | wire, unit | transport | `Mgx.IntegrationTests/Cmdlets/ResponseFidelityTests.cs`, `Mgx.IntegrationTests/Engine/ErrorBodyShapeTests.cs` |
| GraphSDK-3361 | Graph PowerShell SDK | InvalidRequest | Scalars and arrays serialize as themselves - a single-element array stays an array, a scalar does not become one. | wire, unit | serialization | `Mgx.IntegrationTests/Serialization/WireBodyTests.cs`, `Mgx.IntegrationTests/Serialization/PSWrapperEquivalenceTests.cs` |
| M365DSC-5306, M365DSC-7175 | Microsoft365DSC | InvalidRequest | A body that cannot be serialized is refused with a message naming the property path, and inside a batch it fails that item while the rest are sent. | wire | serialization, batch | `Mgx.IntegrationTests/Serialization/WireBodyTests.cs` |
| M365DSC-5354 | Microsoft365DSC | NotFound | Path segments carrying spaces, apostrophes, percent signs and non-ASCII survive into the request path unaltered. | wire | URI | `Mgx.IntegrationTests/Cmdlets/UriEncodingTests.cs`, `Mgx.IntegrationTests/TestSetup/HostileInputs.cs` (`PathSegments`) |
| M365DSC-7198 | Microsoft365DSC | NotFound | A failed batch item is an error the pipeline can see: it lands in `$Error`, counts for `-ErrorVariable`, and stops the pipeline under `-ErrorAction Stop`, with or without a dead-letter file. Results from chunks already applied are not lost when a later chunk fails. | wire | batch | `Mgx.IntegrationTests/Cmdlets/BatchErrorSurfacingTests.cs`, `Mgx.IntegrationTests/Engine/Batch/PartialBatchResultTests.cs`, `Mgx.IntegrationTests/Cmdlets/ErrorActionMatrixTests.cs` (batch cells) |

## Partly covered

| Issue | Source | Class | Guarantee | Test type | Subsystem | Covered now / still planned |
| --- | --- | --- | --- | --- | --- | --- |
| M365DSC-4426 | Microsoft365DSC | Authentication | A 401 is classified as authentication rather than retried as transient, and a changed auth context re-keys the cached client instead of reusing the old token. | wire, unit | auth | Covered: classification and the fingerprint, in `Mgx.IntegrationTests/Engine/Errors/ErrorClassifierTests.cs`, `Mgx.IntegrationTests/Engine/Errors/ErrorPolicyParityTests.cs`, `Mgx.IntegrationTests/Cmdlets/Base/AuthFingerprintTests.cs`, `Mgx.IntegrationTests/Cmdlets/ResilienceInjectionScenarioTests.cs`. Planned: the mid-run expiry-and-reconnect drill - 2.1.5 fault injection. |
| GraphSDK-2148 | Graph PowerShell SDK | Permanent | mgx keeps working whatever else the session has loaded and in whatever order: no assembly-binding failure, no accidental SDK dependency. | wire | module load | Covered: the in-process half - injection, re-injection and teardown against a stand-in GraphSession, in `Mgx.IntegrationTests/Cmdlets/ResilienceInjectionScenarioTests.cs`. Covered: the load-order permutations, one child pwsh per permutation against installed Az.Accounts, PnP.PowerShell and ExchangeOnlineManagement, in `tests/Ecosystem/Mgx.Ecosystem.Tests.ps1`. |

## Planned

| Issue | Source | Class | Guarantee | Test type | Subsystem | Brought by |
| --- | --- | --- | --- | --- | --- | --- |
| M365DSC-7010 | Microsoft365DSC | Consistency | A read that immediately follows a write tolerates Graph's replication delay instead of reporting the object missing. | wire | consistency | 2.3.0. `MgxErrorClass.Consistency` is declared and mapped; nothing produces it yet. |

## Unmapped

| Issue | Source | Why it is here |
| --- | --- | --- |
| GraphSDK-2644 | Graph PowerShell SDK | No test claims this id and no failure class is recorded for it. The source issue needs reading before a row can say what it guarantees. Left in the index so the gap stays visible rather than disappearing with the row. |

## Adding an entry

A new provenance id gets a row here and a `(Corpus: <id>, <what it is>.)` line in the summary of
the test class that holds it, so the mapping reads the same from either end. A row whose
guarantee has no test says which release is meant to bring it.

The mapping is guarded one way only, by
`tests/Mgx.IntegrationTests/TestSetup/RegressionCorpusIndexTests.cs`: every issue id written
anywhere under `tests/` has to appear here. The reverse cannot be guarded, because a row that
names the release bringing its test is exactly a row with no file to point at.
