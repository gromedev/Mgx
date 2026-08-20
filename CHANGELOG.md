# Changelog

## 2.1.0

Adds proactive throttle avoidance, ranged content downloads, and resumable enumeration.

### Added

- Adaptive request pacing, on by default. Requests are spaced before they are sent, per workload; opt out with Set-MgxOption -NoAdaptivePacing.
- Get-MgxContent, to download file and media content whole or by byte range (-First).
- Sync-MgxDelta -CheckpointPath, to resume interrupted enumerations.
- Sync-MgxDelta -Latest, to baseline state without enumerating.
- Sync-MgxDelta -Prefer, to send drive delta Prefer tokens.
- Resource units consumed, in telemetry output, benchmarks and examples.

### Fixed

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

### Documentation

- Graph delta responses repeat objects across pages; deduplicate on id, or baseline with -Latest.
- Adaptive pacing applies to every request except batch outer POSTs.
- Measured throttling behavior: resource unit budgets are scoped per application and tenant, and x-ms-throttle-limit-percentage is not emitted.

## 2.0.1

Patch release. Three fixes; no feature or API changes.

### Fixed

- The rate limiter was disposed on a timer while live clients held it, so any Set-MgxOption call broke Enable-MgxResilience sessions minutes later.
- Enable-MgxResilience and Disable-MgxResilience cancelled SDK requests still in flight.
- Sync-MgxDelta -Uri with a query string or trailing slash failed on the second run.

## 2.0.0

### Breaking

- Invoke-MgxRequest, Invoke-MgxBatchRequest, Expand-MgxRelation and Sync-MgxDelta now emit case-insensitive Hashtables instead of PSObjects, matching Invoke-MgGraphRequest. Use -Raw | ConvertFrom-Json for the old shape.
- @odata.type is preserved under its own key; all other @odata.* transport metadata is stripped, including @odata.etag.
- Removed the custom format views (Mgx.User, Mgx.Group, and the rest); dictionaries render with PowerShell's built-in Name/Value view.
- Lowered the floor to PowerShell 7.4 (LTS) and .NET 8, down from 7.5.
- Removed Microsoft.Graph.Authentication from RequiredModules. Cmdlets needing auth now report GraphAuthModuleNotLoaded with install instructions.

### Fixed

- Action endpoints returning collection envelopes (e.g. /directoryObjects/getByIds) now unwrap and emit individual items on every response path.
- Fan-out to endpoints like /users/{id} now extracts id from both Hashtables and PSCustomObjects, raising MissingPipelineId if absent.
- Invoke-MgxBatchRequest accepts both Hashtables and PSCustomObjects, so failed items can be piped straight back in.

### Changed

- Updated to Polly.Core 8.7.0 and System.Threading.RateLimiting 10.0.10.

## 1.0.5

### Fixed

- Export-MgxCollection no longer loses progress on an interrupted first run.
- Get-MgxTelemetry now counts per-item Retry-After waits from $batch in RetryDelayMs.
- Count-discrepancy warnings now also fire when an enumeration returns more items than @odata.count reported.

### Documentation

- about_Mgx_Tuning now covers write-cost pacing: Graph throttles writes, not batch items.
- Export-MgxCollection help covers mid-page checkpointing, first-run recovery and id deduplication.
- Corrected the batch-vs-fan-out speedup for PATCH at 1k scale, from 3-4x to the measured ~1.5x.
- Rebuilt the README from the new benchmark suite.

## 1.0.4

### Fixed

- Cmdlets cached credentials from only the first Connect-MgGraph call. Clients are now keyed on the full authentication context.
- JSON string input to -Body was silently converted to an empty object when passed as a PSObject.
- Enable-MgxResilience stayed bound to pre-reconnect SDK clients.
- Set-MgxOption -TotalTimeoutSeconds did not update the HTTP client timeout for existing sessions.
- Temporary 429s permanently slowed Invoke-MgxBatchRequest for the rest of the session.
- The internal type cache did not invalidate when Microsoft.Graph.Authentication was re-imported.
- JSON integers larger than 2^53 lost precision.

### Changed

- Batch items with invalid JSON bodies now fail individually instead of aborting the batch.
- Passing -Body on a GET now warns instead of being silently ignored.
- SdkVersion header strings are derived from the assembly version.

### Added

- -Debug request and response tracing across all cmdlets, with credential redaction and a 4 KB body limit.
- xUnit and Pester suites, and a build-and-test CI workflow.

## 1.0.3

- Remove-Module Mgx failed and left the module loaded, when no Graph request had run in the session.
- The SdkVersion header reported mgx/0.3.0 regardless of the installed version.
- Extracted cmdlet lifecycle and JSON conversion to MgxCmdletCore. Internal refactor; no surface change.
- CA1416 is now an error, so a Windows-only API in cross-platform code fails the build rather than throwing at runtime.

## 1.0.2

- Fixed Linux install: lowercased the manifest, module and format filenames for case-sensitive filesystems.
- Updated the about_Mgx_Tuning version reference to v1.0.1.

## 1.0.1

- Added tab completion for the Uri parameter on every cmdlet that accepts a Graph path.
- Extracted CircuitBreakerMessage on MgxCmdletBase, replacing six inline copies.
- Removed redundant XML doc comments on self-documenting members.
