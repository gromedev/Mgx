# Changelog

## 2.1.3

### Added

- Added about_Mgx_Errors documentation detailing termination behavior, error record generation, and -ErrorAction/-ErrorVariable handling.
- Added a performance benchmark measuring throughput under -Concurrency against connection pool limits.
- Live test suite now supports client secret authentication alongside certificate-based authentication.

### Fixed

- SecureString, credential objects, and script blocks in -Body throw explicit errors rather than serializing as reflection noise.
- Property path details are now included in error records when NaN or Infinity values fail -Body serialization.
- Server errors, timeouts, and precondition failures assign specific ErrorCategory values instead of defaulting to NotSpecified.
- Broken circuit exceptions across fan-out execution paths surface consistent error guidance.
- Get-MgxContent forwards the full family of conditional headers (including If-None-Match) to content download hosts.
- Piped input to Get-MgxContent validates -OutFile constraints prior to requesting download URLs.
- BatchChunkFailed events now map underlying exception root causes to appropriate ErrorCategory values.
- Failed batch chunks under -ErrorAction Stop write dead-letter files prior to terminating pipeline execution.
- Typed parameters deferring to options already present in -Uri emit warnings rather than dropping silently.
- Sync-MgxDelta state comparison evaluates active -Property sets correctly when -Uri $select parameters are present.
- Embedded $count parameters in -Uri avoid triggering redundant degradation retries.
- Encoded %24top options in -Uri combined with -Top parameters no longer emit duplicate query parameters.
- Absolute -Uri inputs with leading whitespace are now rejected by the guard instead of being prepended to the versioned base URL.
- Lowercase conditional headers are preserved and forwarded across two-hop content download requests.
- client-request-id headers on two-hop content download paths avoid duplicate values.
- Requests with bodies now emit warnings on malformed header names rather than raising unhandled crashes.
- Caller-supplied Content-Length headers are ignored in favor of computed payload lengths, preventing request failures from length mismatches.
- Request bodies over 4 MB pass directly to the SDK chain under Enable-MgxResilience instead of being rejected.
- HTTP 304 Not Modified responses on conditional downloads complete cleanly without generating error records.
- If-Match headers are stripped when requesting content download URLs to prevent HTTP 412 Precondition Failed errors.
- Byte-level JSON parsing handles UTF-8 BOM prefixes correctly.
- Response bodies and error snippets respect declared response character sets during decoding.
- Export-MgxCollection handles malformed page responses cleanly by emitting error records instead of raw exceptions.
- Circular self-referencing objects in -Body raise validation errors instead of causing process stack overflows.
- Dead-letter file output preserves unescaped readable text formatting.
- Benchmark 10 workload scaling was adjusted to trigger true throttling and measure adaptive pacing accurately.

### Changed

- Unified failure classification logic across retries, circuit breaking, batch retry checks, content downloads, and adaptive pacing.

### Documentation

- Reorganized examples by task to cover end-to-end workflows rather than isolated cmdlet usage.
- Clarified that adaptive pacing responds directly to throttling signals, using observed latency for telemetry reporting rather than as a control input.
- Verbose output now identifies non-JSON string -Body inputs and notes Content-Type override usage.
- Updated cmdlet help to document -Body serialization contracts and prohibited parameter object types.

## 2.1.2

### Fixed

- `Invoke-MgxBatchRequest` wrote no error records for failed items unless a dead-letter write also failed.
- HTTP 204 and empty response bodies on GET requests complete cleanly instead of raising unhandled errors.
- HTML and non-JSON response bodies surface as proper error records instead of unhandled exceptions.
- Unparseable JSON response bodies report raw received text without crashing the pipeline.
- '#' characters in request paths no longer truncate subsequent path content.
- Pre-encoded values in `-Filter`, `-Search`, `-Property`, `-Sort`, and `-ExpandProperty` avoid double-percent-encoding.
- Enum values in `-Body` transmit as strings instead of numbers.
- Byte arrays in `-Body` serialize as base64 strings instead of JSON integer arrays.
- `TimeSpan` values in `-Body` format correctly as ISO-8601 durations.
- `DateTime` values without a `Kind` property include valid offsets to prevent endpoint rejection.
- `PSCustomObject` inputs to `-Body` retain non-NoteProperty members instead of serializing to `{}`.
- Non-ASCII text in `-Body` avoids `\u`-escaping to keep `-Debug` traces and dead-letter files readable.
- Array values in `-Headers` transmit properly instead of serializing to the literal string `System.String[]`.
- `Invoke-MgxRequest`, `Expand-MgxRelation`, and `Export-MgxCollection` preserve absolute `-Uri` inputs without mangling.
- `Get-MgxContent` properly escapes drive and item IDs piped into download paths.
- Content-Type and other content headers in `-Headers` are preserved instead of being silently dropped.
- Custom `client-request-id` values in `-Headers` no longer have internal Mgx IDs appended.
- Case-variant `If-Match` headers in `-Headers` avoid sending duplicate headers.
- Typed parameters duplicating query options already in `-Uri` no longer emit twice.
- Timed-out request attempts during Ctrl-C cancellation no longer schedule redundant retries.
- Requests routed through `Enable-MgxResilience` include required `client-request-id` headers.
- Unexpected failures preserve buffered retry histories for diagnostics.
- A request body over 4 MB passed through `Enable-MgxResilience` unchecked and was buffered whole.

## 2.1.1

### Fixed

- `Enable-MgxResilience` now respects throttling signals, properly slowing pacing, logging telemetry retries, and preventing throttled requests from failing outright.

### Changed

- HTTP 503 and 504 errors on write operations are no longer retried under `Enable-MgxResilience` (throttling 429 retries are unaffected).

## 2.1.0

### Added

- Added adaptive request pacing by default per workload, with opt-out via `Set-MgxOption -NoAdaptivePacing`.
- Added `Get-MgxContent` to download file and media content whole or by byte range (`-First`).
- Added `Sync-MgxDelta -CheckpointPath` to resume interrupted enumerations at page boundaries or every 500 items in JSONL mode.
- Added `Sync-MgxDelta -Latest` to baseline state from the current moment without enumerating.
- Added `Sync-MgxDelta -Prefer` to send drive delta Prefer tokens.
- Added Resource Units Consumed reporting to telemetry outputs, benchmark results, and examples.

### Fixed

- `-Latest` flag behavior preserved after state invalidation to avoid dropping changes since the previous sync.
- Delta resume retains interrupted run items and properly manages delta tokens when previous runs completed.
- Export resume retains interrupted run items when previous exports have written output files.
- Export resume prevents duplicate page item writes when runs are interrupted twice within the same page.
- Delta resume outputs interrupted sync data cleanly without prepending previous sync rows.
- Export resume recovers transient error interruptions without restarting exports from scratch.
- Resume checkpoints ignore non-temp files to prevent consuming them as data.
- Refused delta tokens report distinct error messages instead of claiming unsupported delta endpoints.
- `Sync-MgxDelta` targets correct cloud endpoints when run as the first cmdlet in a session.
- Access-denied or read-only output files raise clear errors on Windows without unhandled exceptions.
- Missing drive items report absent items directly without suggesting retry on beta endpoints.
- `-Top` parameter functions correctly when combined with `-All`.
- `Get-Help` reflects post-2.0.0 output shapes and parameters.
- Enumerations report errors cleanly when `nextLink` URLs are refused instead of returning partial collections.
- Delta syncs handle non-object items within pages without halting.
- `Get-MgxContent` reports unwritable `-OutFile` errors clearly against target paths rather than temporary paths.
- Non-Graph JSON error bodies surface CDN failures accurately without throwing nested exceptions.
- Batch 429 responses clear pacing caps properly without persisting in telemetry endlessly.
- `Get-MgxContent` respects HTTP date-formatted `Retry-After` headers from download hosts.
- `Get-Help` documentation updated with examples for `-CheckpointPath`, `-Latest`, `-Prefer`, adaptive pacing, `Get-MgxContent` endpoints, and `Set-MgxOption -BatchChunkConcurrency`.
- Example table rendering fixed across seven examples, and password secret examples updated to avoid misclassification as certificates.
- Adaptive caps respect `-RateLimitPerSecond` ceilings during repeated 429 throttling events.
- Delta state records API versions to prevent un-versioned runs from silently syncing alternate versions.
- `-Debug` tracing redacts pre-authenticated download URLs from redirect headers and response bodies.
- `Get-MgxContent -OutFile` with piped input appends/manages output files without overwriting all but the final item.
- `-Offset` parameter respected when servers return full-body HTTP 200 responses.
- Offsets past end-of-file preserve existing `-OutFile` targets instead of overwriting with empty files.
- Two-hop content downloads record as successful requests.
- Adaptive pacing prevents synchronized burst requests when queue sleep times are clamped.
- `Enable-MgxResilience` populates telemetry metrics for request counts, HTTP time, and rate-limiter waits.
- `Invoke-MgGraphRequest` supports relative URIs while resilience is enabled.
- `$batch` envelopes bucket under target workloads instead of `Other`.
- Slow start respects rate limit ceilings configured below 4 rps.
- `ResiliencePipelineFactory.Reset()` isolates configuration changes and resets pacer state across credential changes.
- Ctrl-C cancellation during file cleanup completes without throwing disposed-object errors.

### Documentation

- Documented Graph delta response object duplication across pages, recommending `id` deduplication or `-Latest` baselining.
- Clarified that adaptive pacing applies to every request except batch outer POSTs.
- Documented measured throttling behavior: resource unit budgets are scoped per application and tenant, and `x-ms-throttle-limit-percentage` is not emitted.

## 2.0.1

Patch release. Three fixes; no feature or API changes.

### Fixed

- The rate limiter was disposed on a timer while live clients held it, so any Set-MgxOption call broke Enable-MgxResilience sessions minutes later.
- Enable-MgxResilience and Disable-MgxResilience cancelled SDK requests still in flight.
- Sync-MgxDelta -Uri with a query string or trailing slash failed on the second run.

## 2.0.0

### Breaking

- Invoke-MgxRequest, Invoke-MgxBatchRequest, Expand-MgxRelation and Sync-MgxDelta now emit case-insensitive Hashtables instead of PSObjects, matching Invoke-MgGraphRequest. Use -Raw | ConvertFrom-Json for the old shape.
- @odata.type is preserved under its own key; all other @odata.- transport metadata is stripped, including @odata.etag.
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
