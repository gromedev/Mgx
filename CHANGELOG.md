# Changelog

## 2.0.1

Patch release. Three fixes; no feature or API changes.

### Fixed

- The rate limiter was disposed on a timer while live clients still held it, so any Set-MgxOption call left Enable-MgxResilience sessions throwing ObjectDisposedException minutes later. It is now retired by dropping the reference rather than disposed.
- Enable-MgxResilience and Disable-MgxResilience disposed the injected HTTP client synchronously, cancelling SDK requests still in flight. Restoring the SDK's own client already stops new traffic.
- Sync-MgxDelta -Uri with a query string or trailing slash failed on the second run with a DeltaLinkPathMismatch error. The resource-path check now compares paths to paths.

## 2.0.0

### Breaking

- Output shape changed for Invoke-MgxRequest, Invoke-MgxBatchRequest, Expand-MgxRelation, and Sync-MgxDelta to emit case-insensitive Hashtables instead of PSObjects, matching Invoke-MgGraphRequest output. Raw JSON output is still accessible via -Raw | ConvertFrom-Json.
- Preserved @odata.type verbatim under its own key for read-modify-write round-trips while stripping all other @odata.- transport metadata (including @odata.etag to prevent phantom drift in state comparisons).
- Removed custom Graph entity format views (Mgx.User, Mgx.Group, Mgx.Application, Mgx.ServicePrincipal, Mgx.DirectoryRole, Mgx.BatchResult) since dictionaries render using PowerShell's built-in Name/Value view.
- Lowered the platform floor to target .NET 8, adding support for PowerShell 7.4 (LTS) and later (down from 7.5). Replaced the .NET 9 API in the -Debug tracer with a WaitAsync equivalent.
- Removed Microsoft.Graph.Authentication from RequiredModules to decouple the module from the full Graph SDK. Cmdlets needing authentication now report a GraphAuthModuleNotLoaded error with installation instructions if the module is missing.

### Fixed

- Action endpoints returning collection envelopes (e.g., /directoryObjects/getByIds) now correctly unwrap and emit individual items across all response paths, including fan-out bulk writes, with truncation warnings if @odata.nextLink is present.
- Fan-out requests piped to endpoints like '/users/{id}' now extract id members from both Hashtables and PSCustomObjects, throwing a MissingPipelineId error if neither is found instead of embedding literal type names in the URL.
- Invoke-MgxBatchRequest now parses input items from both Hashtables and PSCustomObjects, enabling failed items to be piped directly back in for retries.

### Changed

- Updated dependencies to Polly.Core 8.7.0 and System.Threading.RateLimiting 10.0.10.

## 1.0.5

### Fixed

- Export-MgxCollection no longer loses progress on interrupted first runs. Cancelled runs promote temp files to the output path with matching checkpoints, and killed or crashed runs recover on the next invocation by trimming orphaned temp files to the last checkpointed item count. The "Resume with:" hint now accurately reflects first-run states.
- Get-MgxTelemetry now includes per-item Retry-After wait times from $batch processing into RetryDelayMs instead of incorrectly reporting zero retry delay for throttled batch sessions.
- Count-discrepancy warnings now also trigger when an enumeration returns more items than @odata.count reported (using a 0.5% threshold with a 50-item floor) and recommend deduplicating on id downstream.

### Documentation

- Added write-cost pacing documentation to about_Mgx_Tuning, explaining that Graph throttles write operations rather than batch items (e.g., a group create with 20 member bindings costs ~21 writes) and how to budget BatchItemsPerSecond accordingly.
- Updated Export-MgxCollection help with details on mid-page checkpointing, first-run recovery, and downstream id deduplication hygiene.
- Adjusted performance claims for Invoke-MgxRequest, updating the batch-vs-fan-out speedup from 3–4x down to the measured ~1.5x for PATCH operations at 1k scale.
- Rebuilt the README based on measured results from the new benchmark suite.


## 1.0.4

### Fixed

- Fixed Mgx cmdlets caching credentials from only the first Connect-MgGraph call. HTTP clients are now keyed on a fingerprint of the full authentication context so reconnecting with a different application, certificate, account, scope set, or secret properly updates the identity.
- Fixed JSON string input to -Body being silently converted to an empty object ({}) when passed as a PSObject. IDictionary, PSCustomObject, array, and nested bodies now serialize correctly.
- Fixed Enable-MgxResilience remaining bound to pre-reconnect SDK clients by automatically re-injecting resilience settings when the Graph identity changes.
- Fixed Set-MgxOption -TotalTimeoutSeconds failing to update the HTTP client timeout for existing sessions by rebuilding the client on value changes.
- Fixed temporary 429 throttling permanently slowing Invoke-MgxBatchRequest rates for the remainder of the session; write pacing now recovers after clean chunks and fully resets after five unthrottled minutes.
- Fixed the internal type cache failing to invalidate when Microsoft.Graph.Authentication was re-imported at a different version or in a new load context.
- Fixed JSON integers larger than 2^53 losing precision due to double-precision floating-point conversion.

### Changed

- Batch items with invalid JSON bodies now fail individually instead of aborting the entire batch.
- Passing -Body on a GET request now emits a warning instead of being silently ignored.
- SdkVersion header strings are now derived automatically from the assembly version set in Directory.Build.props.

### Added

- Added -Debug request and response tracing across all cmdlets (single requests, pagination, fan-out, $batch) with credential redaction and a 4 KB body truncation limit.
- Added xUnit and Pester test suites alongside a build-and-test CI workflow to the repository.


## 1.0.3

- Fixed `Remove-Module Mgx` failing and leaving the module loaded. Static-state cleanup moved into `AlcInitializer.OnRemove`, ahead of the ALC resolver detaching; it previously ran from the module `OnRemove` scriptblock, by which point `Polly.Core` was unresolvable. Only triggered when no Graph request had run in the session
- Fixed the `SdkVersion` request header reporting `mgx/0.3.0` regardless of the installed version. It now matches the module version, and `build.ps1` fails the build if the constant and the manifest ever disagree
- Extracted cmdlet lifecycle and JSON conversion to `MgxCmdletCore`. Internal refactor; no change to the cmdlet surface
- Treat `CA1416` as an error in both projects, so a Windows-only API reaching the cross-platform code paths fails the build instead of throwing `PlatformNotSupportedException` on Linux or macOS

## 1.0.2

- Fixed Linux install: renamed `Mgx.psd1`, `Mgx.psm1`, and `Mgx.Format.ps1xml` to lowercase so `Install-Module Mgx` works on case-sensitive filesystems (PSGallery lowercases the module folder name)
- Updated `about_Mgx_Tuning` version reference to v1.0.1

## 1.0.1

- Added tab completion for the Uri parameter on all cmdlets that accept Graph API paths (Invoke-MgxRequest, Invoke-MgxBatchRequest, Export-MgxCollection, Expand-MgxRelation, Sync-MgxDelta)
- Extracted `CircuitBreakerMessage` protected property on `MgxCmdletBase` to eliminate repeated inline circuit breaker message strings across six cmdlet files
- Removed redundant XML doc comments on self-documenting members in `MgxCmdletBase` and `ResilientGraphClient`